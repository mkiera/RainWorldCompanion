using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Library;
using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Tests;

public class LibraryLoadTests
{
    private static readonly SaveSlotRef LocalTwo = new(SaveRealm.Local, 2);
    private static readonly SaveSlotRef LocalThree = new(SaveRealm.Local, 3);
    private static readonly SaveSlotRef OnlineTwo = new(SaveRealm.Online, 2);

    [Fact]
    public void Loading_writes_the_stored_bytes_into_the_slot()
    {
        using var world = new LibraryWorld();
        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);

        var result = world.Library.LoadEntry(entry, LocalThree);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        SnapshotLayout.AssertBytesEqual(
            File.ReadAllBytes(entry.SavePath), world.Live.ReadBytes("sav3"), "sav3");
    }

    [Fact]
    public void The_loaded_save_still_parses_and_its_checksum_still_verifies()
    {
        // The checksum covers the payload plus the game's salt, so a payload short by even one
        // character would still parse but fail this check.
        using var world = new LibraryWorld();
        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);

        world.Library.LoadEntry(entry, LocalThree);

        var metadata = SaveMetadataExtractor.Extract(world.Live.Resolve("sav3"), 3);
        Assert.Null(metadata.ParseError);
        Assert.True(metadata.ChecksumValid);
        var campaign = Assert.Single(metadata.Campaigns);
        Assert.Equal("White", campaign.SlugcatId);
        Assert.Equal(17, campaign.CycleNum);
    }

    [Fact]
    public void Loading_leaves_the_entry_untouched()
    {
        using var world = new LibraryWorld();
        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);
        var stored = File.ReadAllBytes(entry.SavePath);

        world.Library.LoadEntry(entry, LocalThree);

        SnapshotLayout.AssertBytesEqual(stored, File.ReadAllBytes(entry.SavePath), "save.bin");
        Assert.True(world.Library.VerifyEntry(world.Reload(entry)).Ok);
    }

    [Fact]
    public void Loading_into_a_slot_with_no_file_yet_works()
    {
        // online_sav2 is not in the fixture folder, the state before a Rain Meadow player has
        // played online.
        using var world = new LibraryWorld();
        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);
        Assert.False(world.Live.FileExists("online_sav2"));

        var result = world.Library.LoadEntry(entry, OnlineTwo);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        SnapshotLayout.AssertBytesEqual(
            File.ReadAllBytes(entry.SavePath), world.Live.ReadBytes("online_sav2"), "online_sav2");
    }

    [Fact]
    public void Loading_reports_how_much_it_wrote_and_where()
    {
        using var world = new LibraryWorld();
        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);

        var result = world.Library.LoadEntry(entry, LocalThree);

        Assert.Equal(new FileInfo(world.Live.Resolve("sav3")).Length, result.BytesCopied);
        Assert.True(result.LiveFolderModified);
        Assert.Contains("Ironclaw run", result.Headline(), StringComparison.Ordinal);
        Assert.Contains("sav3", result.Headline(), StringComparison.Ordinal);
    }

    [Fact]
    public void Loading_records_the_slot_it_went_into()
    {
        using var world = new LibraryWorld();
        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);

        world.Library.LoadEntry(entry, LocalThree);

        var manifest = world.Reload(entry).Manifest!;
        Assert.Equal(new SaveSlotRef(SaveRealm.Local, 3), manifest.LastLoadedSlotRef);
        Assert.NotNull(manifest.LastLoadedUtc);
        Assert.Equal(new FileInfo(world.Live.Resolve("sav3")).Length, manifest.LastLoadedSizeBytes);
    }

    [Fact]
    public void Loading_takes_a_safety_snapshot_that_holds_the_file_it_replaced()
    {
        using var world = new LibraryWorld();
        var replaced = world.Live.ReadBytes("sav3");
        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);

        var result = world.Library.LoadEntry(entry, LocalThree);

        var safety = Assert.IsType<BackupSnapshot>(result.SafetySnapshot);
        Assert.Equal(BackupKind.PreRestoreSafety, safety.Kind);
        var kept = SnapshotLayout.FindFile(safety, "sav3");
        Assert.NotNull(kept);
        SnapshotLayout.AssertBytesEqual(replaced, File.ReadAllBytes(kept!), "sav3 in the safety snapshot");
    }

    [Fact]
    public void Restoring_the_safety_snapshot_puts_the_slot_back()
    {
        using var world = new LibraryWorld();
        var before = world.Live.ReadTree();
        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);

        var loaded = world.Library.LoadEntry(entry, LocalThree);
        var restored = world.Backups.RestoreBackup(loaded.SafetySnapshot!);

        Assert.True(restored.Success, string.Join("; ", restored.Errors));
        SnapshotLayout.AssertTreeUnchanged(before, world.Live.ReadTree());
    }

    [Fact]
    public void The_safety_snapshot_is_named_after_the_save_that_was_loaded()
    {
        using var world = new LibraryWorld();
        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);

        var result = world.Library.LoadEntry(entry, LocalThree);

        Assert.Contains("Ironclaw run", result.SafetySnapshot!.Label ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void Loading_refuses_while_the_game_is_running()
    {
        using var world = new LibraryWorld();
        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);
        var before = world.Live.ReadTree();
        world.Detector.RunningProcessName = "RainWorld";

        Assert.Throws<GameRunningException>(() => world.Library.LoadEntry(entry, LocalThree));

        SnapshotLayout.AssertTreeUnchanged(before, world.Live.ReadTree());
    }

    [Fact]
    public void Loading_refuses_a_stored_save_with_a_flipped_byte()
    {
        using var world = new LibraryWorld();
        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);
        var before = world.Live.ReadTree();
        SaveLibraryTests.FlipOneByte(entry.SavePath);

        var result = world.Library.LoadEntry(world.Reload(entry), LocalThree);

        Assert.False(result.Success);
        Assert.False(result.LiveFolderModified);
        Assert.NotEmpty(result.Errors);
        SnapshotLayout.AssertTreeUnchanged(before, world.Live.ReadTree());
    }

    [Fact]
    public void Loading_refuses_a_stored_save_that_was_truncated()
    {
        using var world = new LibraryWorld();
        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);
        var before = world.Live.ReadTree();
        var bytes = File.ReadAllBytes(entry.SavePath);
        File.WriteAllBytes(entry.SavePath, bytes[..(bytes.Length / 2)]);

        var result = world.Library.LoadEntry(world.Reload(entry), LocalThree);

        Assert.False(result.Success);
        SnapshotLayout.AssertTreeUnchanged(before, world.Live.ReadTree());
    }

    [Fact]
    public void Loading_refuses_a_stored_save_that_went_away()
    {
        using var world = new LibraryWorld();
        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);
        var before = world.Live.ReadTree();
        File.Delete(entry.SavePath);

        var result = world.Library.LoadEntry(world.Reload(entry), LocalThree);

        Assert.False(result.Success);
        SnapshotLayout.AssertTreeUnchanged(before, world.Live.ReadTree());
    }

    [Fact]
    public void Loading_refuses_an_entry_that_did_not_finish_being_stored()
    {
        using var world = new LibraryWorld();
        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);
        File.Delete(entry.ManifestPath);
        var before = world.Live.ReadTree();

        var result = world.Library.LoadEntry(world.Reload(entry), LocalThree);

        Assert.False(result.Success);
        SnapshotLayout.AssertTreeUnchanged(before, world.Live.ReadTree());
    }

    [Fact]
    public void Loading_refuses_a_slot_number_the_game_does_not_have()
    {
        using var world = new LibraryWorld();
        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);
        var before = world.Live.ReadTree();

        var result = world.Library.LoadEntry(entry, new SaveSlotRef(SaveRealm.Local, 9));

        Assert.False(result.Success);
        SnapshotLayout.AssertTreeUnchanged(before, world.Live.ReadTree());
    }

    [Fact]
    public void Loading_refuses_while_another_window_holds_the_backup_folder()
    {
        using var world = new LibraryWorld();
        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);
        var before = world.Live.ReadTree();

        Directory.CreateDirectory(world.BackupRoot.Path);
        using (new FileStream(
                   Path.Combine(world.BackupRoot.Path, ".operation-lock"),
                   FileMode.OpenOrCreate,
                   FileAccess.ReadWrite,
                   FileShare.None))
        {
            var result = world.Library.LoadEntry(entry, LocalThree);

            Assert.False(result.Success);
            Assert.False(result.LiveFolderModified);
            SnapshotLayout.AssertTreeUnchanged(before, world.Live.ReadTree());
        }
    }

    [Fact]
    public void A_refused_load_says_nothing_was_changed()
    {
        using var world = new LibraryWorld();
        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);
        SaveLibraryTests.FlipOneByte(entry.SavePath);

        var result = world.Library.LoadEntry(world.Reload(entry), LocalThree);

        Assert.Contains("nothing", result.Headline(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_plan_describes_both_sides_without_changing_anything()
    {
        using var world = new LibraryWorld();
        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);
        var before = world.Live.ReadTree();

        var plan = world.Library.PlanLoad(entry, LocalThree);

        Assert.True(plan.CanLoad);
        Assert.Empty(plan.Problems);
        Assert.Equal("sav3", plan.Target.FileName);
        Assert.True(plan.Target.Exists);
        SnapshotLayout.AssertTreeUnchanged(before, world.Live.ReadTree());
    }

    [Fact]
    public void A_plan_for_a_slot_with_no_file_says_so()
    {
        using var world = new LibraryWorld();
        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);

        var plan = world.Library.PlanLoad(entry, OnlineTwo);

        Assert.True(plan.CanLoad);
        Assert.False(plan.Target.Exists);
    }

    [Fact]
    public void A_plan_warns_when_an_empty_save_would_land_on_a_campaign()
    {
        using var world = new LibraryWorld();

        // online_sav holds the explored map and progression record but no campaign, which is
        // what a Rain Meadow slot looks like before a story run is saved.
        var entry = world.Library.StoreSlot(new SaveSlotRef(SaveRealm.Online, 1), "just the map", null);

        var plan = world.Library.PlanLoad(entry, LocalThree);

        Assert.True(plan.CanLoad);
        Assert.Contains(
            plan.Warnings,
            warning => warning.Contains("no campaign", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_plan_for_a_damaged_save_refuses_before_the_user_agrees_to_anything()
    {
        using var world = new LibraryWorld();
        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);
        SaveLibraryTests.FlipOneByte(entry.SavePath);

        var plan = world.Library.PlanLoad(world.Reload(entry), LocalThree);

        Assert.False(plan.CanLoad);
        Assert.NotEmpty(plan.Problems);
    }
}
