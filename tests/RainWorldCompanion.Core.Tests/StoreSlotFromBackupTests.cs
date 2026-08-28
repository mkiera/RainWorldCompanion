using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Library;
using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Tests;

/// <summary>
/// A whole slot out of a backup, rather than one campaign at a time. The snapshot is a copy taken
/// at a moment, so it is read where it lies and nothing about it changes.
/// </summary>
public class StoreSlotFromBackupTests
{
    private static readonly SaveSlotRef LocalTwo = new(SaveRealm.Local, 2);

    private const string DevourmentPath = @"ModConfigs\devourment.txt";

    private static (BackupSnapshot Snapshot, string SlotPath) Backed(LibraryWorld world)
    {
        var snapshot = world.Backups.CreateBackup("KNOWN GOOD", null, BackupKind.Manual);
        return (snapshot, Path.Combine(snapshot.DirectoryPath, LocalTwo.FileName));
    }

    /// <summary>Every file under a folder with its digest, for proving a read changed nothing.</summary>
    private static SortedDictionary<string, string> Tree(string root)
    {
        var tree = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            tree[Path.GetRelativePath(root, file)] = Hashing.ComputeFileSha256(file);
        }

        return tree;
    }

    [Fact]
    public void The_slot_lands_in_the_library_byte_for_byte()
    {
        using var world = new LibraryWorld();
        var (snapshot, slotPath) = Backed(world);

        var entry = world.Library.StoreSlotFrom(
            slotPath, LocalTwo.FileName, SaveRealm.Local, 2, "out of the backup", null);

        Assert.Equal(
            Hashing.ComputeFileSha256(slotPath),
            Hashing.ComputeFileSha256(Path.Combine(entry.DirectoryPath, LibraryEntry.SaveFileName)));
        Assert.Equal(entry.Manifest!.Sha256, Hashing.ComputeFileSha256(slotPath));
    }

    [Fact]
    public void Every_campaign_in_that_slot_comes_with_it()
    {
        using var world = new LibraryWorld();
        world.Seed(LocalTwo.FileName, "White", cycle: 40);
        var (_, slotPath) = Backed(world);

        var entry = world.Library.StoreSlotFrom(
            slotPath, LocalTwo.FileName, SaveRealm.Local, 2, "out of the backup", null);

        Assert.NotEmpty(entry.Manifest!.Metadata!.Campaigns);
        Assert.False(entry.IsCampaign);
    }

    [Fact]
    public void The_slot_it_came_from_is_recorded()
    {
        using var world = new LibraryWorld();
        var (_, slotPath) = Backed(world);

        var entry = world.Library.StoreSlotFrom(
            slotPath, LocalTwo.FileName, SaveRealm.Local, 2, "out of the backup", null);

        Assert.Equal(LocalTwo.FileName, entry.Manifest!.SourceFileName);
        Assert.Equal(2, entry.Manifest.SourceSlot);
        Assert.Equal(SaveRealm.Local, entry.Manifest.SourceRealm);
    }

    /// <summary>
    /// The settings that were beside those bytes, not the ones on this machine now. A snapshot's
    /// ModConfigs folder is a faithful copy, so it is read the same way the live one is.
    /// </summary>
    [Fact]
    public void It_carries_the_settings_the_backup_holds_rather_than_the_ones_on_now()
    {
        using var world = new LibraryWorld();
        world.Live.WriteText(DevourmentPath, "back then = 1");
        var (snapshot, slotPath) = Backed(world);
        world.Live.WriteText(DevourmentPath, "right now = 1");

        var entry = world.Library.StoreSlotFrom(
            slotPath, LocalTwo.FileName, SaveRealm.Local, 2, "out of the backup", null,
            configsRoot: snapshot.DirectoryPath);

        var kept = Path.Combine(entry.DirectoryPath, LibraryEntry.ConfigsFolderName, "devourment.txt");
        Assert.Equal("back then = 1", File.ReadAllText(kept));
    }

    [Fact]
    public void The_backup_is_not_touched()
    {
        using var world = new LibraryWorld();
        var (snapshot, slotPath) = Backed(world);
        var before = Tree(snapshot.DirectoryPath);

        world.Library.StoreSlotFrom(
            slotPath, LocalTwo.FileName, SaveRealm.Local, 2, "out of the backup", null,
            configsRoot: snapshot.DirectoryPath);

        Assert.Equal(before, Tree(snapshot.DirectoryPath));
    }

    [Fact]
    public void The_save_folder_is_not_touched()
    {
        using var world = new LibraryWorld();
        var (_, slotPath) = Backed(world);
        var before = Tree(world.Live.Path);

        world.Library.StoreSlotFrom(
            slotPath, LocalTwo.FileName, SaveRealm.Local, 2, "out of the backup", null);

        Assert.Equal(before, Tree(world.Live.Path));
    }

    /// <summary>
    /// Unlike storing a live slot, which reads the folder the game writes to. A snapshot is nobody
    /// else's to rewrite, so there is nothing here for a running game to spoil.
    /// </summary>
    [Fact]
    public void The_game_running_does_not_stop_it()
    {
        using var world = new LibraryWorld();
        var (_, slotPath) = Backed(world);
        world.Detector.RunningProcessName = "RainWorld";

        var entry = world.Library.StoreSlotFrom(
            slotPath, LocalTwo.FileName, SaveRealm.Local, 2, "out of the backup", null);

        Assert.True(entry.IsComplete);
    }

    [Fact]
    public void A_slot_file_that_is_gone_is_refused_by_name()
    {
        using var world = new LibraryWorld();
        var (_, slotPath) = Backed(world);
        File.Delete(slotPath);

        var thrown = Assert.Throws<FileNotFoundException>(() => world.Library.StoreSlotFrom(
            slotPath, LocalTwo.FileName, SaveRealm.Local, 2, "out of the backup", null));

        Assert.Contains(LocalTwo.FileName, thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_name_of_nothing_is_refused()
    {
        using var world = new LibraryWorld();
        var (_, slotPath) = Backed(world);

        Assert.Throws<ArgumentException>(() => world.Library.StoreSlotFrom(
            slotPath, LocalTwo.FileName, SaveRealm.Local, 2, "   ", null));
    }

    [Fact]
    public void The_entry_it_makes_can_be_put_back_in_a_slot()
    {
        using var world = new LibraryWorld();
        world.Seed(LocalTwo.FileName, "White", cycle: 77);
        var (_, slotPath) = Backed(world);

        var entry = world.Library.StoreSlotFrom(
            slotPath, LocalTwo.FileName, SaveRealm.Local, 2, "out of the backup", null);

        var result = world.Library.LoadEntry(entry, new SaveSlotRef(SaveRealm.Local, 3));

        Assert.True(result.Success);
        Assert.Equal(
            Hashing.ComputeFileSha256(slotPath),
            Hashing.ComputeFileSha256(world.Live.Resolve("sav3")));
    }
}
