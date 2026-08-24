using RainWorldSaveManager.Core.Backups;
using RainWorldSaveManager.Core.System;

namespace RainWorldSaveManager.Tests;

/// <summary>
/// A snapshot is the only thing standing behind a restore, and the pre-restore safety copy is
/// the only thing standing behind a restore that goes wrong. These tests are about the one
/// failure that nothing downstream can catch: a snapshot that agrees with itself while
/// disagreeing with the saves it claims to hold.
/// </summary>
public class SnapshotIntegrityTests
{
    private const string AppVersion = "1.0.0-test";

    /// <summary>
    /// Steam Cloud, or the game writing on its way out, truncates a save between the moment the
    /// scope measures it and the moment the copy loop reaches it. Recording what landed, without
    /// ever comparing it to the source, certifies the truncation: the manifest says two bytes,
    /// the file in the snapshot is those two bytes, and Verify agrees because it re-derives the
    /// same hash from the same file.
    /// </summary>
    [Fact]
    public void A_file_that_changes_before_the_copy_abandons_the_snapshot_instead_of_certifying_it()
    {
        using var world = new BackupWorld();
        var hook = ProgressHook.On(
            "Copying sav2",
            fired => world.Live.WriteBytes("sav2", new byte[fired + 1]));

        var failure = Record.Exception(() => world.Service.CreateBackup("first", null, BackupKind.Manual, hook));

        Assert.NotNull(failure);
        Assert.Contains("sav2", failure!.Message, StringComparison.Ordinal);

        var snapshot = Assert.Single(world.Service.ListBackups());
        Assert.False(snapshot.IsComplete, "a snapshot of a file that moved under the copy was written as finished");
        Assert.Null(snapshot.Manifest);
    }

    /// <summary>
    /// The same failure a step later: the file is torn while the copy is running, so the length
    /// and the timestamp still match what was measured and only the bytes differ. Nothing but
    /// hashing the source as well as the copy sees this.
    /// </summary>
    [Fact]
    public void A_file_torn_during_the_copy_abandons_the_snapshot_instead_of_certifying_it()
    {
        using var world = new BackupWorld();
        var original = world.Live.ReadBytes("sav2");
        var writeTime = File.GetLastWriteTimeUtc(world.Live.Resolve("sav2"));

        var hook = ProgressHook.On("Checking sav2", fired =>
        {
            // Same length and same timestamp, different bytes. Only the source hash catches it.
            var torn = (byte[])original.Clone();
            torn[128] ^= (byte)(fired + 1);
            world.Live.WriteBytes("sav2", torn);
            File.SetLastWriteTimeUtc(world.Live.Resolve("sav2"), writeTime);
        });

        var failure = Record.Exception(() => world.Service.CreateBackup("first", null, BackupKind.Manual, hook));

        Assert.NotNull(failure);
        Assert.Contains("sav2", failure!.Message, StringComparison.Ordinal);
        Assert.False(Assert.Single(world.Service.ListBackups()).IsComplete);
    }

    /// <summary>
    /// One change is copied again rather than refused outright, and what the manifest then says
    /// is what is actually on disk, hash included.
    /// </summary>
    [Fact]
    public void A_file_that_settles_after_one_change_is_copied_again_and_recorded_as_it_now_is()
    {
        using var world = new BackupWorld();
        var replacement = SyntheticSave.SaveFile(SyntheticSave.SavePayload(cycle: 42), paddingBytes: 64);
        var hook = ProgressHook.On("Copying sav2", _ => world.Live.WriteBytes("sav2", replacement), limit: 1);

        var snapshot = world.Service.CreateBackup("first", null, BackupKind.Manual, hook);

        Assert.True(snapshot.IsComplete);
        var entry = snapshot.Manifest!.Files.Single(f => SaveTree.Normalize(f.RelativePath) == "sav2");
        Assert.Equal(replacement.Length, entry.SizeBytes);
        Assert.Equal(SnapshotLayout.Sha256(world.Live.Resolve("sav2")), entry.Sha256, ignoreCase: true);
        Assert.True(world.Service.Verify(snapshot).Ok);
    }

    /// <summary>
    /// The manifest hash has to be a fact about the live file, not only about the copy. Both are
    /// hashed, so this holds by construction rather than by luck.
    /// </summary>
    [Fact]
    public void Every_manifest_hash_matches_both_the_copy_and_the_live_file()
    {
        using var world = new BackupWorld();

        var snapshot = world.Service.CreateBackup("first", null);

        foreach (var entry in snapshot.Manifest!.Files)
        {
            var live = world.Live.Resolve(entry.RelativePath);
            var copy = SnapshotLayout.FindFile(snapshot, entry.RelativePath)!;

            Assert.Equal(SnapshotLayout.Sha256(live), entry.Sha256, ignoreCase: true);
            Assert.Equal(SnapshotLayout.Sha256(copy), entry.Sha256, ignoreCase: true);
        }
    }

    // ---- Two operations at once ----

    /// <summary>
    /// Two operations starting in the same wall-clock second used to be handed the same folder,
    /// because Directory.Exists followed by Directory.CreateDirectory cannot decide who owns a
    /// name: CreateDirectory succeeds on a folder that is already there. The claim file is the
    /// step the filesystem makes atomic.
    /// </summary>
    [Fact]
    public async Task Two_threads_claiming_a_snapshot_folder_at_once_never_get_the_same_one()
    {
        using var world = new BackupWorld();
        var second = new BackupService(world.Live.Path, world.BackupRoot.Path, FakeGameDetector.NotRunning(), AppVersion);

        for (var round = 0; round < 20; round++)
        {
            using var barrier = new Barrier(2);
            var claimed = new string[2];

            var first = Task.Run(() =>
            {
                barrier.SignalAndWait();
                claimed[0] = world.Service.CreateSnapshotDirectory();
            });

            var next = Task.Run(() =>
            {
                barrier.SignalAndWait();
                claimed[1] = second.CreateSnapshotDirectory();
            });

            await Task.WhenAll(first, next);

            Assert.False(
                string.Equals(claimed[0], claimed[1], StringComparison.OrdinalIgnoreCase),
                $"round {round}: both operations were handed {claimed[0]}");
        }
    }

    /// <summary>
    /// The folder claim settles who owns a name. This settles whether two operations run over
    /// each other at all, including from a second copy of the app in another process.
    /// </summary>
    [Fact]
    public void A_second_operation_is_refused_while_one_is_already_running()
    {
        using var world = new BackupWorld();
        var second = new BackupService(world.Live.Path, world.BackupRoot.Path, FakeGameDetector.NotRunning(), AppVersion);

        Exception? refusal = null;
        var hook = ProgressHook.On(
            "Copying",
            _ => refusal = Record.Exception(() => second.CreateBackup("overlapping", null)),
            limit: 1);

        var snapshot = world.Service.CreateBackup("first", null, BackupKind.Manual, hook);

        Assert.True(snapshot.IsComplete);
        Assert.IsType<BackupBusyException>(refusal);
    }

    [Fact]
    public void The_backup_folder_is_free_again_once_an_operation_finishes()
    {
        using var world = new BackupWorld();
        var second = new BackupService(world.Live.Path, world.BackupRoot.Path, FakeGameDetector.NotRunning(), AppVersion);

        world.Service.CreateBackup("first", null);
        var later = second.CreateBackup("second", null);

        Assert.True(later.IsComplete);
        Assert.Equal(2, world.Service.ListBackups().Count);
    }

    // ---- Links ----

    /// <summary>
    /// Moving dvrmentSaveStates onto another drive and leaving a junction behind is a common way
    /// to relocate large mod data. The scope will not walk through the junction, and it must not
    /// then report a backup holding none of those files as a plain success.
    /// </summary>
    [JunctionFact]
    public void A_junctioned_scope_folder_is_reported_rather_than_silently_left_out()
    {
        using var world = new BackupWorld();
        using var elsewhere = new TempDirectory("devourment-elsewhere");
        MoveDevourmentStatesBehindAJunction(world, elsewhere);

        var scan = world.Service.Scope.Scan();
        var snapshot = world.Service.CreateBackup("first", null);

        Assert.Contains("dvrmentSaveStates", scan.SkippedLinks, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(scan.Files, f => f.RelativePath.StartsWith("dvrmentSaveStates", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("dvrmentSaveStates", snapshot.Manifest!.SkippedLinks, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            snapshot.Manifest.Files,
            f => f.RelativePath.StartsWith("dvrmentSaveStates", StringComparison.OrdinalIgnoreCase));
    }

    [JunctionFact]
    public void A_skipped_link_is_named_in_the_progress_the_user_watches()
    {
        using var world = new BackupWorld();
        using var elsewhere = new TempDirectory("devourment-elsewhere");
        MoveDevourmentStatesBehindAJunction(world, elsewhere);
        var progress = new CollectingProgress();

        world.Service.CreateBackup("first", null, BackupKind.Manual, progress);

        Assert.Contains(progress.Messages, m => m.Contains("dvrmentSaveStates", StringComparison.OrdinalIgnoreCase));
    }

    [JunctionFact]
    public void A_restore_warns_about_the_links_the_backup_could_not_hold()
    {
        using var world = new BackupWorld();
        using var elsewhere = new TempDirectory("devourment-elsewhere");
        MoveDevourmentStatesBehindAJunction(world, elsewhere);
        var snapshot = world.Service.CreateBackup("first", null);

        var result = world.Service.RestoreBackup(snapshot);

        Assert.Contains(result.Warnings, w => w.Contains("dvrmentSaveStates", StringComparison.OrdinalIgnoreCase));
    }

    // ---- The service refuses a backup root it would eat ----

    [Fact]
    public void A_backup_root_inside_the_save_folder_is_refused_by_the_service_itself()
    {
        using var live = new TempDirectory("live");
        SaveTree.Populate(live);

        Assert.ThrowsAny<ArgumentException>(() => new BackupService(
            live.Path,
            live.Resolve(@"dvrmentSaveStates\backups"),
            FakeGameDetector.NotRunning(),
            AppVersion));
    }

    [JunctionFact]
    public void A_backup_root_junctioned_into_the_save_folder_is_refused_by_the_service_itself()
    {
        using var live = new TempDirectory("live");
        using var alias = new TempDirectory("alias-parent");
        SaveTree.Populate(live);

        var target = live.Resolve(@"dvrmentSaveStates\backups");
        var link = alias.Resolve("backups");
        Assert.True(Links.TryCreateDirectoryJunction(link, target));

        Assert.ThrowsAny<ArgumentException>(() => new BackupService(
            live.Path, link, FakeGameDetector.NotRunning(), AppVersion));
    }

    [JunctionFact]
    public void A_junction_and_the_folder_it_points_at_resolve_to_the_same_path()
    {
        using var parent = new TempDirectory("resolve");
        var target = parent.Resolve("target");
        var link = parent.Resolve("link");
        Assert.True(Links.TryCreateDirectoryJunction(link, target));

        Assert.Equal(CanonicalPath.Resolve(target), CanonicalPath.Resolve(link), ignoreCase: true);
    }

    private static void MoveDevourmentStatesBehindAJunction(BackupWorld world, TempDirectory elsewhere)
    {
        var folder = world.Live.Resolve("dvrmentSaveStates");
        foreach (var file in Directory.GetFiles(folder))
        {
            File.Move(file, Path.Combine(elsewhere.Path, Path.GetFileName(file)));
        }

        Directory.Delete(folder, recursive: true);
        Assert.True(Links.TryCreateDirectoryJunction(folder, elsewhere.Path));
    }
}
