using RainWorldSaveManager.Core.Backups;

namespace RainWorldSaveManager.Tests;

/// <summary>
/// What a restore does when it cannot finish. Every test here is about a save folder that has
/// already been written to: which files it is then allowed to delete, whether the game can be
/// launched under it, and whether the user is told the difference between a restore that
/// refused to start and one that stopped half way.
/// </summary>
public class RestoreFailureTests
{
    private const string AppVersion = "1.0.0-test";
    private const string NewInScopeFile = @"dvrmentSaveStates\contents_1_Rivulet_story.txt";

    /// <summary>
    /// The backup root is on a drive that goes away, so every copy fails. The delete step used
    /// to run anyway, on the grounds that it had not been cancelled, and removed the live files
    /// the snapshot does not contain. A restore that put nothing back must not be the thing that
    /// deletes today's expedition and Devourment files.
    /// </summary>
    [Fact]
    public void A_restore_whose_copies_failed_does_not_delete_the_files_the_backup_lacks()
    {
        using var world = new BackupWorld();
        var snapshot = world.Service.CreateBackup("first", null);
        world.Live.WriteText(NewInScopeFile, "Rivulet|Slugcat|1|stomach");

        var result = RestoreWithSav3Locked(world, snapshot);

        Assert.False(result.Success);
        Assert.True(
            world.Live.FileExists(NewInScopeFile),
            "a restore that could not put every file back still deleted a live file");
    }

    [Fact]
    public void A_restore_that_failed_part_way_says_so_and_names_the_safety_snapshot()
    {
        using var world = new BackupWorld();
        var snapshot = world.Service.CreateBackup("first", null);
        world.Live.WriteText(NewInScopeFile, "Rivulet|Slugcat|1|stomach");

        var result = RestoreWithSav3Locked(world, snapshot);

        Assert.False(result.Success);
        Assert.True(result.LiveFolderModified, "files were overwritten but the result says the folder is untouched");
        Assert.NotNull(result.SafetySnapshot);
        Assert.Contains("part restored", result.Headline(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.SafetySnapshot!.Id, result.Headline(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_restore_that_refused_to_start_says_nothing_was_changed()
    {
        using var world = new BackupWorld();
        world.BackupRoot.WriteText(@"broken-snapshot\sav", "not a real snapshot");
        var broken = world.Service.ListBackups().Single(s => s.Id == "broken-snapshot");

        var result = world.Service.RestoreBackup(broken);

        Assert.False(result.Success);
        Assert.False(result.LiveFolderModified);
        Assert.Contains("nothing in the save folder was changed", result.Headline(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_restore_that_finished_leads_with_the_plain_success_line()
    {
        using var world = new BackupWorld();
        var snapshot = world.Service.CreateBackup("first", null);

        var result = world.Service.RestoreBackup(snapshot);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal("Restore finished.", result.Headline());
    }

    // ---- The game launched under the restore ----

    /// <summary>
    /// The safety snapshot takes several seconds of hashing and copying on a real save folder.
    /// A player who clicks Restore and then launches the game through Steam during that wait
    /// used to have the game write its own progression back over the restored files.
    /// </summary>
    [Fact]
    public void A_game_launched_while_the_safety_snapshot_runs_stops_the_restore_before_the_first_write()
    {
        using var world = new BackupWorld();
        var snapshot = world.Service.CreateBackup("first", null);
        world.Live.WriteBytes("sav3", SyntheticSave.SaveFile(SyntheticSave.SavePayload(cycle: 999)));
        var before = world.Live.ReadTree();

        // The safety snapshot reports this last, so the game appears after it is safely on disk.
        var hook = ProgressHook.On("Writing manifest", _ => world.Detector.RunningProcessName = "RainWorld", limit: 1);

        Assert.Throws<GameRunningException>(() => world.Service.RestoreBackup(snapshot, hook));

        SnapshotLayout.AssertTreeUnchanged(before, world.Live.ReadTree());
        Assert.Contains(world.Service.ListBackups(), s => s.Kind == BackupKind.PreRestoreSafety);
    }

    /// <summary>
    /// And once the overwrite loop is running, the same check aborts it rather than writing more
    /// files under a process that is reading them.
    /// </summary>
    [Fact]
    public void A_game_launched_during_the_overwrite_loop_stops_the_restore_part_way()
    {
        using var world = new BackupWorld();
        for (var i = 0; i < 40; i++)
        {
            world.Live.WriteText($@"dvrmentSaveStates\contents_{i}_Filler_story.txt", $"filler {i}");
        }

        var snapshot = world.Service.CreateBackup("first", null);
        var diverged = SyntheticSave.SaveFile(SyntheticSave.SavePayload(cycle: 999));
        world.Live.WriteBytes("sav3", diverged);
        world.Live.WriteText(NewInScopeFile, "Rivulet|Slugcat|1|stomach");

        var hook = ProgressHook.On("Restoring ", _ => world.Detector.RunningProcessName = "RainWorld", limit: 1);
        var result = world.Service.RestoreBackup(snapshot, hook);

        Assert.False(result.Success);
        Assert.True(result.LiveFolderModified);
        Assert.Contains(result.Errors, e => e.Contains("Rain World", StringComparison.OrdinalIgnoreCase));

        // sav3 sorts last, so the loop never reached it, and the deletion step never ran.
        SnapshotLayout.AssertBytesEqual(diverged, world.Live.ReadBytes("sav3"), "sav3");
        Assert.True(world.Live.FileExists(NewInScopeFile), "an abandoned restore deleted a live file anyway");
    }

    // ---- Warnings against errors ----

    /// <summary>
    /// An empty folder that will not go away leaves the restored saves exactly right. Reporting
    /// it as a failed restore sends the user to a dialog that says the restore did not finish,
    /// for a folder with no files in it.
    /// </summary>
    [Fact]
    public void An_empty_folder_that_cannot_be_removed_is_a_note_rather_than_a_failure()
    {
        using var world = new BackupWorld();
        var stuck = world.Live.CreateSubdirectory(@"dvrmentSaveStates\stuck");
        var snapshot = world.Service.CreateBackup("first", null);

        var attributes = File.GetAttributes(stuck);
        File.SetAttributes(stuck, attributes | FileAttributes.ReadOnly);

        try
        {
            var result = world.Service.RestoreBackup(snapshot);

            Assert.True(result.Success, string.Join("; ", result.Errors));
            Assert.Contains(result.Warnings, w => w.Contains("stuck", StringComparison.OrdinalIgnoreCase));
            Assert.True(Directory.Exists(stuck));
        }
        finally
        {
            File.SetAttributes(stuck, attributes);
        }
    }

    // ---- Links ----

    /// <summary>
    /// A file that was real when the backup was taken and is a symlink now. Copying onto it
    /// writes through the link, over a file outside the save folder that no dialog mentioned and
    /// no safety snapshot holds.
    /// </summary>
    [SymlinkFact]
    public void A_restore_refuses_to_write_through_a_symlinked_save_file()
    {
        using var world = new BackupWorld();
        using var elsewhere = new TempDirectory("documents");
        var snapshot = world.Service.CreateBackup("first", null);

        var outside = elsewhere.WriteText("notes.txt", "the user's own notes");
        var outsideBefore = File.ReadAllBytes(outside);
        var target = world.Live.Resolve(@"dvrmentSaveStates\contents_0_White_story.txt");
        File.Delete(target);
        Assert.True(Links.TryCreateFileSymbolicLink(target, outside));

        var result = world.Service.RestoreBackup(snapshot);

        SnapshotLayout.AssertBytesEqual(outsideBefore, File.ReadAllBytes(outside), "notes.txt");
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("contents_0_White_story.txt", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The same hole one level up: the file name is ordinary but a folder on the way to it is a
    /// junction, which a textual containment check cannot see.
    /// </summary>
    [JunctionFact]
    public void A_restore_refuses_to_write_through_a_junctioned_save_folder()
    {
        using var world = new BackupWorld();
        using var elsewhere = new TempDirectory("archive");
        world.Live.WriteText(@"dvrmentSaveStates\sub\contents_9_White_story.txt", "the backed up state");
        var snapshot = world.Service.CreateBackup("first", null);

        var sub = world.Live.Resolve(@"dvrmentSaveStates\sub");
        Directory.Delete(sub, recursive: true);
        Assert.True(Links.TryCreateDirectoryJunction(sub, elsewhere.Path));

        var result = world.Service.RestoreBackup(snapshot);

        Assert.False(
            File.Exists(Path.Combine(elsewhere.Path, "contents_9_White_story.txt")),
            "the restore wrote through a junction and landed outside the save folder");
        Assert.False(result.Success);
    }

    /// <summary>
    /// Relocating dvrmentSaveStates to another drive and leaving a junction behind used to cost
    /// the empty folders on the far side of it, because SearchOption.AllDirectories walks through
    /// a junction and the tidy-up step deleted whatever it found empty.
    /// </summary>
    [JunctionFact]
    public void The_tidy_up_step_does_not_delete_empty_folders_on_the_far_side_of_a_junction()
    {
        using var world = new BackupWorld();
        using var elsewhere = new TempDirectory("archive");
        var outsideEmptyFolder = elsewhere.CreateSubdirectory("2024");
        Assert.True(Links.TryCreateDirectoryJunction(world.Live.Resolve(@"dvrmentSaveStates\archive"), elsewhere.Path));

        var snapshot = world.Service.CreateBackup("first", null);
        var result = world.Service.RestoreBackup(snapshot);

        Assert.True(
            Directory.Exists(outsideEmptyFolder),
            "a restore deleted a folder outside the save folder, on the far side of a junction");
        Assert.True(Directory.Exists(world.Live.Resolve(@"dvrmentSaveStates\archive")));
        Assert.True(result.Success, string.Join("; ", result.Errors));
    }

    // ---- A failure after the first live write ----

    /// <summary>
    /// The save folder walk in the deletion step is not wrapped in anything, so a folder that
    /// disappears under it throws straight out of RestoreBackup. The live files have already
    /// been overwritten by then, and the exception takes the safety snapshot id with it, which
    /// is the one thing that can undo what just happened.
    /// </summary>
    [Fact]
    public void A_failure_after_the_live_files_were_written_still_returns_the_safety_snapshot()
    {
        using var world = new BackupWorld();
        var snapshot = world.Service.CreateBackup("first", null);
        world.Live.WriteBytes("sav3", SyntheticSave.SaveFile(SyntheticSave.SavePayload(cycle: 999)));

        // The safety snapshot walks the folder once; the deletion step is the second walk.
        var service = new BackupService(
            world.Live.Path,
            world.BackupRoot.Path,
            FakeGameDetector.NotRunning(),
            AppVersion,
            new FailingScope(world.Live.Path, failOnCall: 2));

        var result = service.RestoreBackup(service.ListBackups().Single(s => s.Id == snapshot.Id));

        Assert.False(result.Success);
        Assert.NotNull(result.SafetySnapshot);
        Assert.True(result.LiveFolderModified);
        Assert.Contains(result.SafetySnapshot!.Id, result.Headline(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Runs a restore in which one live file is locked the way a cloud sync service locks it, so
    /// the copy onto it fails. The lock is taken at the first live write rather than up front,
    /// because a file locked before the safety snapshot would stop the restore at that step
    /// instead, which is a different case entirely.
    /// </summary>
    private static RestoreResult RestoreWithSav3Locked(BackupWorld world, BackupSnapshot snapshot)
    {
        FileStream? locked = null;

        var hook = ProgressHook.On(
            "Restoring ",
            _ => locked = new FileStream(
                world.Live.Resolve("sav3"), FileMode.Open, FileAccess.Read, FileShare.None),
            limit: 1);

        try
        {
            return world.Service.RestoreBackup(snapshot, hook);
        }
        finally
        {
            locked?.Dispose();
        }
    }
}
