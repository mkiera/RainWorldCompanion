using RainWorldCompanion.Core.Backups;

namespace RainWorldCompanion.Tests;

/// <summary>
/// Restore is the only operation that overwrites a player's saves. Every test here either proves
/// the bytes come back exactly or proves that a refused restore left the live folder alone.
/// </summary>
public class RestoreTests
{
    private const string NewInScopeFile = @"dvrmentSaveStates\contents_1_Rivulet_story.txt";
    // Game settings, not save data. ModConfigs\*.txt is in scope now, so an unrelated mod's
    // config no longer works as the out-of-scope file this suite adds.
    private const string NewOutOfScopeFile = "localoptions.txt";

    // ---- PlanRestore ----

    [Fact]
    public void A_plan_against_an_untouched_folder_reports_everything_unchanged()
    {
        using var world = new BackupWorld();
        var snapshot = world.Service.CreateBackup("first", null);

        var plan = world.Service.PlanRestore(snapshot);

        Assert.Equal(SaveTree.Sorted(SaveTree.InScope), SaveTree.Sorted(plan.Unchanged));
        Assert.Empty(plan.Added);
        Assert.Empty(plan.Overwritten);
        Assert.Empty(plan.Deleted);
    }

    [Fact]
    public void A_plan_sorts_changed_files_into_added_overwritten_unchanged_and_deleted()
    {
        using var world = new BackupWorld();
        var snapshot = world.Service.CreateBackup("first", null);
        DivergeLiveFolder(world);

        var plan = world.Service.PlanRestore(snapshot);

        Assert.Equal(new[] { "online_sav" }, SaveTree.Sorted(plan.Added));
        Assert.Equal(new[] { "sav3" }, SaveTree.Sorted(plan.Overwritten));
        Assert.Equal(new[] { NewInScopeFile }, SaveTree.Sorted(plan.Deleted));
        Assert.Equal(
            SaveTree.Sorted(SaveTree.InScope.Except(new[] { "online_sav", "sav3" })),
            SaveTree.Sorted(plan.Unchanged));
    }

    [Fact]
    public void A_plan_never_mentions_an_out_of_scope_file()
    {
        using var world = new BackupWorld();
        var snapshot = world.Service.CreateBackup("first", null);
        DivergeLiveFolder(world);

        var plan = world.Service.PlanRestore(snapshot);

        var everything = SaveTree.Sorted(
            plan.Added.Concat(plan.Overwritten).Concat(plan.Unchanged).Concat(plan.Deleted));

        foreach (var outOfScope in SaveTree.OutOfScope.Concat(new[] { NewOutOfScopeFile }))
        {
            Assert.DoesNotContain(SaveTree.Normalize(outOfScope), everything);
        }
    }

    [Fact]
    public void Planning_does_not_write_anything()
    {
        using var world = new BackupWorld();
        var snapshot = world.Service.CreateBackup("first", null);
        DivergeLiveFolder(world);
        var before = world.Live.ReadTree();

        world.Service.PlanRestore(snapshot);

        SnapshotLayout.AssertTreeUnchanged(before, world.Live.ReadTree());
    }

    // ---- RestoreBackup, the happy path ----

    [Fact]
    public void Restoring_puts_every_in_scope_file_back_byte_for_byte()
    {
        using var world = new BackupWorld();
        var original = world.Live.ReadTree();
        var snapshot = world.Service.CreateBackup("first", null);
        DivergeLiveFolder(world);

        var result = world.Service.RestoreBackup(snapshot);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Empty(result.Errors);
        foreach (var relativePath in SaveTree.InScope)
        {
            Assert.True(world.Live.FileExists(relativePath), $"{relativePath} was not restored");
            SnapshotLayout.AssertBytesEqual(original[relativePath], world.Live.ReadBytes(relativePath), relativePath);
        }
    }

    [Fact]
    public void The_padded_container_files_come_back_with_their_nul_padding_intact()
    {
        using var world = new BackupWorld();
        var originalSav2 = world.Live.ReadBytes("sav2");
        var originalSav3 = world.Live.ReadBytes("sav3");
        var snapshot = world.Service.CreateBackup("first", null);
        world.Live.WriteBytes("sav2", SyntheticSave.SaveFile(SyntheticSave.SavePayload(cycle: 99)));
        world.Live.WriteBytes("sav3", Array.Empty<byte>());

        world.Service.RestoreBackup(snapshot);

        SnapshotLayout.AssertBytesEqual(originalSav2, world.Live.ReadBytes("sav2"), "sav2");
        SnapshotLayout.AssertBytesEqual(originalSav3, world.Live.ReadBytes("sav3"), "sav3");
        Assert.Equal(98288, world.Live.ReadBytes("sav2").Length);
        Assert.Equal(0, (int)world.Live.ReadBytes("sav2")[^1]);
    }

    [Fact]
    public void Restoring_deletes_the_in_scope_file_the_snapshot_does_not_have()
    {
        using var world = new BackupWorld();
        var snapshot = world.Service.CreateBackup("first", null);
        DivergeLiveFolder(world);
        Assert.True(world.Live.FileExists(NewInScopeFile));

        world.Service.RestoreBackup(snapshot);

        Assert.False(world.Live.FileExists(NewInScopeFile), "an in-scope file missing from the snapshot survived");
    }

    [Fact]
    public void Restoring_leaves_out_of_scope_files_exactly_as_they_were()
    {
        using var world = new BackupWorld();
        var snapshot = world.Service.CreateBackup("first", null);
        DivergeLiveFolder(world);
        var outOfScopeBefore = SaveTree.OutOfScope
            .Concat(new[] { NewOutOfScopeFile })
            .Where(world.Live.FileExists)
            .ToDictionary(p => p, world.Live.ReadBytes, StringComparer.OrdinalIgnoreCase);

        world.Service.RestoreBackup(snapshot);

        foreach (var entry in outOfScopeBefore)
        {
            Assert.True(world.Live.FileExists(entry.Key), $"{entry.Key} was deleted by a restore");
            SnapshotLayout.AssertBytesEqual(entry.Value, world.Live.ReadBytes(entry.Key), entry.Key);
        }
    }

    [Fact]
    public void Restoring_leaves_the_options_file_alone_even_though_it_is_a_save_container()
    {
        using var world = new BackupWorld();
        var snapshot = world.Service.CreateBackup("first", null);
        world.Live.WriteText("options", "ScreenResolution<optB>3");

        world.Service.RestoreBackup(snapshot);

        Assert.Equal("ScreenResolution<optB>3", File.ReadAllText(world.Live.Resolve("options")));
    }

    [Fact]
    public void Restoring_makes_the_live_folder_match_the_plan_it_reported()
    {
        using var world = new BackupWorld();
        var snapshot = world.Service.CreateBackup("first", null);
        DivergeLiveFolder(world);
        var plan = world.Service.PlanRestore(snapshot);

        world.Service.RestoreBackup(snapshot);

        foreach (var relativePath in plan.Added.Concat(plan.Overwritten).Concat(plan.Unchanged))
        {
            Assert.True(world.Live.FileExists(relativePath), $"{relativePath} was planned but is missing");
        }

        foreach (var relativePath in plan.Deleted)
        {
            Assert.False(world.Live.FileExists(relativePath), $"{relativePath} was planned for deletion but remains");
        }
    }

    [Fact]
    public void Restoring_verifies_clean_afterwards()
    {
        using var world = new BackupWorld();
        var snapshot = world.Service.CreateBackup("first", null);
        DivergeLiveFolder(world);

        world.Service.RestoreBackup(snapshot);

        Assert.True(world.Service.Verify(snapshot).Ok);
    }

    // ---- The safety snapshot ----

    [Fact]
    public void Restoring_takes_a_pre_restore_safety_snapshot_first()
    {
        using var world = new BackupWorld();
        var snapshot = world.Service.CreateBackup("first", null);
        DivergeLiveFolder(world);

        var result = world.Service.RestoreBackup(snapshot);

        Assert.NotNull(result.SafetySnapshot);
        Assert.True(result.SafetySnapshot!.IsComplete);
        Assert.Equal(BackupKind.PreRestoreSafety, result.SafetySnapshot.Manifest!.Kind);
        Assert.NotEqual(snapshot.Id, result.SafetySnapshot.Id);
    }

    [Fact]
    public void The_safety_snapshot_holds_the_state_from_just_before_the_restore()
    {
        using var world = new BackupWorld();
        var snapshot = world.Service.CreateBackup("first", null);
        DivergeLiveFolder(world);
        var preRestoreSav3 = world.Live.ReadBytes("sav3");
        var preRestoreNewFile = world.Live.ReadBytes(NewInScopeFile);

        var safety = world.Service.RestoreBackup(snapshot).SafetySnapshot!;

        var savedSav3 = SnapshotLayout.FindFile(safety, "sav3");
        Assert.NotNull(savedSav3);
        SnapshotLayout.AssertBytesEqual(preRestoreSav3, File.ReadAllBytes(savedSav3!), "sav3");

        var savedNewFile = SnapshotLayout.FindFile(safety, NewInScopeFile);
        Assert.NotNull(savedNewFile);
        SnapshotLayout.AssertBytesEqual(preRestoreNewFile, File.ReadAllBytes(savedNewFile!), NewInScopeFile);
    }

    [Fact]
    public void The_safety_snapshot_omits_the_file_that_was_missing_before_the_restore()
    {
        using var world = new BackupWorld();
        var snapshot = world.Service.CreateBackup("first", null);
        DivergeLiveFolder(world);

        var safety = world.Service.RestoreBackup(snapshot).SafetySnapshot!;

        Assert.DoesNotContain(safety.Manifest!.Files, f => SaveTree.Normalize(f.RelativePath) == "online_sav");
        Assert.Null(SnapshotLayout.FindFile(safety, "online_sav"));
    }

    [Fact]
    public void The_safety_snapshot_can_undo_the_restore()
    {
        using var world = new BackupWorld();
        var snapshot = world.Service.CreateBackup("first", null);
        DivergeLiveFolder(world);
        var preRestore = world.Live.ReadTree();

        var safety = world.Service.RestoreBackup(snapshot).SafetySnapshot!;
        var undo = world.Service.RestoreBackup(world.Service.ListBackups().Single(s => s.Id == safety.Id));

        Assert.True(undo.Success, string.Join("; ", undo.Errors));
        foreach (var relativePath in SaveTree.InScope.Where(preRestore.ContainsKey))
        {
            SnapshotLayout.AssertBytesEqual(preRestore[relativePath], world.Live.ReadBytes(relativePath), relativePath);
        }
    }

    // ---- Refusals ----

    [Fact]
    public void Restoring_refuses_while_the_game_is_running()
    {
        using var world = new BackupWorld();
        var snapshot = world.Service.CreateBackup("first", null);
        DivergeLiveFolder(world);
        var before = world.Live.ReadTree();
        world.Detector.RunningProcessName = "Rain World";

        var error = Assert.Throws<GameRunningException>(() => world.Service.RestoreBackup(snapshot));

        Assert.Equal("Rain World", error.ProcessName);
        SnapshotLayout.AssertTreeUnchanged(before, world.Live.ReadTree());
    }

    [Fact]
    public void Restoring_refuses_a_snapshot_with_no_manifest_and_touches_nothing()
    {
        using var world = new BackupWorld();
        world.BackupRoot.WriteText(@"broken-snapshot\sav", "not a real snapshot");
        var broken = world.Service.ListBackups().Single(s => s.Id == "broken-snapshot");
        var before = world.Live.ReadTree();

        var result = world.Service.RestoreBackup(broken);

        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
        SnapshotLayout.AssertTreeUnchanged(before, world.Live.ReadTree());
    }

    [Fact]
    public void Restoring_refuses_a_snapshot_whose_manifest_is_unreadable_and_touches_nothing()
    {
        using var world = new BackupWorld();
        world.BackupRoot.WriteText(@"corrupt-snapshot\manifest.json", "{ this is not json ]");
        var corrupt = world.Service.ListBackups().Single(s => s.Id == "corrupt-snapshot");
        var before = world.Live.ReadTree();

        var result = world.Service.RestoreBackup(corrupt);

        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
        SnapshotLayout.AssertTreeUnchanged(before, world.Live.ReadTree());
    }

    [Fact]
    public void Restoring_refuses_when_a_snapshot_file_was_corrupted_after_the_manifest_was_written()
    {
        using var world = new BackupWorld();
        var snapshot = world.Service.CreateBackup("first", null);
        var copied = SnapshotLayout.FindFile(snapshot, "sav2")!;
        var bytes = File.ReadAllBytes(copied);
        bytes[64] ^= 0xFF;
        File.WriteAllBytes(copied, bytes);
        DivergeLiveFolder(world);
        var before = world.Live.ReadTree();

        var result = world.Service.RestoreBackup(world.Service.ListBackups().Single(s => s.Id == snapshot.Id));

        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
        SnapshotLayout.AssertTreeUnchanged(before, world.Live.ReadTree());
    }

    [Fact]
    public void Restoring_refuses_when_a_snapshot_file_listed_in_the_manifest_is_gone()
    {
        using var world = new BackupWorld();
        var snapshot = world.Service.CreateBackup("first", null);
        File.Delete(SnapshotLayout.FindFile(snapshot, "sav3")!);
        DivergeLiveFolder(world);
        var before = world.Live.ReadTree();

        var result = world.Service.RestoreBackup(world.Service.ListBackups().Single(s => s.Id == snapshot.Id));

        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
        SnapshotLayout.AssertTreeUnchanged(before, world.Live.ReadTree());
    }

    [Fact]
    public void Restoring_refuses_when_a_snapshot_file_was_truncated_after_the_manifest_was_written()
    {
        using var world = new BackupWorld();
        var snapshot = world.Service.CreateBackup("first", null);
        File.WriteAllBytes(SnapshotLayout.FindFile(snapshot, "sav2")!, Array.Empty<byte>());
        var before = world.Live.ReadTree();

        var result = world.Service.RestoreBackup(world.Service.ListBackups().Single(s => s.Id == snapshot.Id));

        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
        SnapshotLayout.AssertTreeUnchanged(before, world.Live.ReadTree());
    }

    /// <summary>
    /// Moves the live folder away from the snapshot in all four directions at once: one file
    /// changed, one deleted, one in-scope file added, one out-of-scope file added.
    /// </summary>
    private static void DivergeLiveFolder(BackupWorld world)
    {
        world.Live.WriteBytes("sav3", SyntheticSave.SaveFile(SyntheticSave.SavePayload(cycle: 999), paddingBytes: 16));
        File.Delete(world.Live.Resolve("online_sav"));
        world.Live.WriteText(NewInScopeFile, "Rivulet|Slugcat|1|stomach");
        world.Live.WriteText(NewOutOfScopeFile, "fullscreen<optB>true");
    }
}
