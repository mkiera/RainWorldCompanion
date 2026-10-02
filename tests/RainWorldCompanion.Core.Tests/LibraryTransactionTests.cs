using System.Text.Json;
using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Editing;
using RainWorldCompanion.Core.Library;
using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Tests;

public class LibraryTransactionTests
{
    private static readonly SaveSlotRef Source = new(SaveRealm.Local, 2);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Failed_manifest_commit_preserves_both_generations_and_settings_on_retry(bool campaign, bool priorUpdate)
    {
        using var world = new LibraryWorld();
        world.Seed("sav2", "White", 12);
        world.Live.WriteText(@"ModConfigs\moreslugcats.txt", "generation = 1");
        var entry = Store(world, campaign);
        if (priorUpdate)
        {
            world.Seed("sav2", "White", 13);
            world.Live.WriteText(@"ModConfigs\moreslugcats.txt", "generation = 2");
            entry = world.Library.UpdateEntry(entry, Source);
        }

        var before = ReadGeneration(entry);
        world.Seed("sav2", "White", 14);
        world.Live.WriteText(@"ModConfigs\moreslugcats.txt", "generation = 3");
        using (new FileStream(entry.ManifestPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.True(Record.Exception(() => world.Library.UpdateEntry(entry, Source)) is IOException or UnauthorizedAccessException);
        }

        var reopened = new SaveLibrary(world.Backups, world.LibraryRoot.Path, world.Detector, LibraryWorld.AppVersion);
        var recovered = Assert.Single(reopened.ListEntries());
        Assert.True(reopened.VerifyEntry(recovered).Ok);
        SnapshotLayout.AssertTreeUnchanged(before, ReadGeneration(recovered));
        var updated = reopened.UpdateEntry(entry, Source);
        Assert.True(reopened.VerifyEntry(updated).Ok);
        Assert.Equal(before[entry.ContentFileName], File.ReadAllBytes(updated.PreviousContentPath));
        var undone = reopened.UndoUpdate(updated);
        Assert.Equal(before[entry.ContentFileName], File.ReadAllBytes(undone.ContentPath));
        Assert.Equal(before[@"configs\moreslugcats.txt"], File.ReadAllBytes(Path.Combine(undone.ConfigsPath, "moreslugcats.txt")));
        Assert.True(reopened.VerifyEntry(undone).Ok);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_undo_manifest_commit_preserves_updated_save_and_earlier_generation(bool campaign)
    {
        using var world = new LibraryWorld();
        world.Seed("sav2", "White", 12);
        var entry = Store(world, campaign);
        world.Seed("sav2", "White", 14);
        entry = world.Library.UpdateEntry(entry, Source);
        var before = ReadGeneration(entry);

        using (new FileStream(entry.ManifestPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.True(Record.Exception(() => world.Library.UndoUpdate(entry)) is IOException or UnauthorizedAccessException);
        }

        var recovered = Assert.Single(world.Library.ListEntries());
        Assert.True(world.Library.VerifyEntry(recovered).Ok);
        SnapshotLayout.AssertTreeUnchanged(before, ReadGeneration(recovered));
        Assert.True(world.Library.VerifyEntry(world.Library.UndoUpdate(recovered)).Ok);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_update_refuses_inconsistent_existing_content_before_replacing_any_generation(bool campaign)
    {
        using var world = new LibraryWorld();
        var entry = Store(world, campaign);
        SaveLibraryTests.FlipOneByte(entry.ContentPath);
        var before = ReadGeneration(entry);

        Assert.Throws<IOException>(() => world.Library.UpdateEntry(entry, Source));

        SnapshotLayout.AssertTreeUnchanged(before, ReadGeneration(entry));
    }

    [Theory]
    [InlineData(false, "Earlier save kept")]
    [InlineData(false, "New save installed")]
    [InlineData(false, "Earlier settings kept")]
    [InlineData(false, "New settings installed")]
    [InlineData(true, "Earlier save kept")]
    [InlineData(true, "New save installed")]
    [InlineData(true, "Earlier settings kept")]
    [InlineData(true, "New settings installed")]
    public void Failure_after_each_generation_change_restores_the_complete_entry(bool campaign, string checkpoint)
    {
        using var world = new LibraryWorld();
        world.Seed("sav2", "White", 12);
        var entry = Store(world, campaign);
        world.Seed("sav2", "White", 13);
        entry = world.Library.UpdateEntry(entry, Source);
        var before = ReadGeneration(entry);
        world.Seed("sav2", "White", 14);
        world.Live.WriteText(@"ModConfigs\moreslugcats.txt", "new settings");
        var failure = ProgressHook.On(checkpoint, _ => throw new IOException("Injected interrupted update"));

        Assert.Throws<IOException>(() => world.Library.UpdateEntry(entry, Source, failure));

        Assert.Equal(1, failure.Fired);
        var recovered = LibraryEntry.Load(entry.DirectoryPath);
        SnapshotLayout.AssertTreeUnchanged(before, ReadGeneration(recovered));
        Assert.True(world.Library.VerifyEntry(recovered).Ok);
        Assert.True(Hashing.FileMatchesHash(recovered.PreviousContentPath, recovered.Manifest!.PreviousSha256!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_blocked_rollback_remains_unavailable_until_reopen_can_recover_it(bool campaign)
    {
        using var world = new LibraryWorld();
        world.Seed("sav2", "White", 12);
        var entry = Store(world, campaign);
        world.Seed("sav2", "White", 13);
        entry = world.Library.UpdateEntry(entry, Source);
        var before = ReadGeneration(entry);
        world.Seed("sav2", "White", 14);
        FileStream? locked = null;
        var failure = ProgressHook.On("New settings installed", _ =>
        {
            locked = new FileStream(entry.ContentPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            throw new IOException("Injected interrupted update");
        });

        try
        {
            Assert.True(Record.Exception(() => world.Library.UpdateEntry(entry, Source, failure))
                        is IOException or UnauthorizedAccessException);
            Assert.Equal(1, failure.Fired);
            Assert.True(Directory.Exists(Path.Combine(entry.DirectoryPath, LibraryEntryTransaction.PendingFolderName)));
            var unavailable = LibraryEntry.Load(entry.DirectoryPath);
            Assert.False(unavailable.IsComplete);
            Assert.Contains("could not be recovered", unavailable.Problem);
            Assert.Throws<InvalidOperationException>(() => world.Library.UpdateEntry(entry, Source));
        }
        finally
        {
            locked?.Dispose();
        }

        var reopened = new SaveLibrary(world.Backups, world.LibraryRoot.Path, world.Detector, LibraryWorld.AppVersion);
        var recovered = Assert.Single(reopened.ListEntries());
        SnapshotLayout.AssertTreeUnchanged(before, ReadGeneration(recovered));
        Assert.True(reopened.VerifyEntry(recovered).Ok);
        var retried = reopened.UpdateEntry(entry, Source);
        Assert.Equal(before[entry.ContentFileName], File.ReadAllBytes(retried.PreviousContentPath));
        Assert.True(reopened.VerifyEntry(reopened.UndoUpdate(retried)).Ok);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(false, 4)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    [InlineData(true, 4)]
    public void Reopen_recovers_persisted_interruption_at_each_update_step(bool campaign, int step)
    {
        using var world = new LibraryWorld();
        world.Seed("sav2", "White", 12);
        var entry = Store(world, campaign);
        world.Seed("sav2", "White", 13);
        entry = world.Library.UpdateEntry(entry, Source);
        var before = ReadGeneration(entry);
        WritePendingSnapshot(entry, before);
        world.Seed("sav2", "White", 14);
        var replacement = campaign
            ? CampaignFile.ToBytes(SaveEditSession.Open(world.Live.Resolve("sav2")).TakeCampaign("White")!)
            : world.Live.ReadBytes("sav2");

        File.Move(entry.ContentPath, entry.PreviousContentPath, overwrite: true);
        if (step >= 1)
        {
            File.WriteAllBytes(entry.ContentPath, replacement);
        }

        if (step >= 2)
        {
            Directory.Delete(entry.PreviousConfigsPath, recursive: true);
            Directory.Move(entry.ConfigsPath, entry.PreviousConfigsPath);
        }

        if (step >= 3)
        {
            Directory.CreateDirectory(entry.ConfigsPath);
            File.WriteAllText(Path.Combine(entry.ConfigsPath, "moreslugcats.txt"), "new settings");
        }

        if (step >= 4)
        {
            var manifest = entry.Manifest!;
            manifest.PreviousSha256 = manifest.Sha256;
            manifest.Sha256 = Hashing.ComputeSha256(replacement);
            File.WriteAllText(entry.ManifestPath, JsonSerializer.Serialize(manifest, BackupJson.Options));
        }

        var recovered = LibraryEntry.Load(entry.DirectoryPath);
        SnapshotLayout.AssertTreeUnchanged(before, ReadGeneration(recovered));
        Assert.True(world.Library.VerifyEntry(recovered).Ok);
        Assert.True(Hashing.FileMatchesHash(recovered.PreviousContentPath, recovered.Manifest!.PreviousSha256!));
        Assert.False(Directory.Exists(Path.Combine(entry.DirectoryPath, LibraryEntryTransaction.PendingFolderName)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_damaged_pending_snapshot_is_preserved_and_blocks_recovery(bool campaign)
    {
        using var world = new LibraryWorld();
        var entry = Store(world, campaign);
        var before = ReadGeneration(entry);
        WritePendingSnapshot(entry, before);
        var pending = Path.Combine(entry.DirectoryPath, LibraryEntryTransaction.PendingFolderName);
        SaveLibraryTests.FlipOneByte(Path.Combine(pending, entry.ContentFileName));

        var unavailable = LibraryEntry.Load(entry.DirectoryPath);

        Assert.False(unavailable.IsComplete);
        Assert.Contains("recovery checksum", unavailable.Problem);
        Assert.True(Directory.Exists(pending));
        SnapshotLayout.AssertTreeUnchanged(before, ReadGeneration(entry));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_update_refuses_to_discard_a_damaged_previous_generation(bool campaign)
    {
        using var world = new LibraryWorld();
        var entry = world.Library.UpdateEntry(Store(world, campaign), Source);
        SaveLibraryTests.FlipOneByte(entry.PreviousContentPath);
        var before = ReadGeneration(entry);

        Assert.Throws<IOException>(() => world.Library.UpdateEntry(entry, Source));

        SnapshotLayout.AssertTreeUnchanged(before, ReadGeneration(entry));
    }

    private static void WritePendingSnapshot(LibraryEntry entry, Dictionary<string, byte[]> files)
    {
        var pending = Path.Combine(entry.DirectoryPath, LibraryEntryTransaction.PendingFolderName);
        Directory.CreateDirectory(pending);
        foreach (var (relative, bytes) in files)
        {
            var path = Path.Combine(pending, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }

        var inventory = files.ToDictionary(pair => pair.Key, pair => Hashing.ComputeSha256(pair.Value));
        File.WriteAllText(Path.Combine(pending, "files.json"), JsonSerializer.Serialize(inventory));
    }

    private static LibraryEntry Store(LibraryWorld world, bool campaign)
        => campaign
            ? world.Library.StoreCampaign(Source, "White", "A campaign", null)
            : world.Library.StoreSlot(Source, "A slot", null);

    private static Dictionary<string, byte[]> ReadGeneration(LibraryEntry entry)
        => Directory.GetFiles(entry.DirectoryPath, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(entry.DirectoryPath, path).StartsWith(".", StringComparison.Ordinal)
                           && !path.EndsWith(".tmp", StringComparison.Ordinal))
            .ToDictionary(path => Path.GetRelativePath(entry.DirectoryPath, path).Replace('/', '\\'), File.ReadAllBytes);
}
