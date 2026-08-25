using System.Security.Cryptography;

using RainWorldCompanion.Core.Backups;

namespace RainWorldCompanion.Tests;

/// <summary>
/// A backup is the only thing standing behind a restore, so these check the copies are exact,
/// the manifest describes what was actually written, and a running game blocks the whole thing.
/// </summary>
public class BackupServiceTests
{
    private const string AppVersion = "1.0.0-test";

    [Fact]
    public void CreateBackup_writes_a_manifest_beside_the_copied_files()
    {
        using var world = new BackupWorld();

        var snapshot = world.Service.CreateBackup("first", "a note");

        Assert.True(Directory.Exists(snapshot.DirectoryPath));
        Assert.True(File.Exists(System.IO.Path.Combine(snapshot.DirectoryPath, "manifest.json")));
        Assert.True(snapshot.IsComplete);
        Assert.Null(snapshot.Problem);
        Assert.NotNull(snapshot.Manifest);
        Assert.Equal("first", snapshot.Manifest!.Label);
        Assert.Equal("a note", snapshot.Manifest.Note);
        Assert.Equal(BackupKind.Manual, snapshot.Manifest.Kind);
        Assert.Equal(AppVersion, snapshot.Manifest.AppVersion);
        Assert.Equal(BackupManifest.CurrentSchemaVersion, snapshot.Manifest.SchemaVersion);
        Assert.NotEqual(default(DateTime), snapshot.CreatedUtc);
        Assert.True(snapshot.TotalSizeBytes > 0);
    }

    [Fact]
    public void The_snapshot_directory_lives_under_the_backup_root()
    {
        using var world = new BackupWorld();

        var snapshot = world.Service.CreateBackup(null, null);

        var parent = System.IO.Path.GetFullPath(
            System.IO.Path.GetDirectoryName(snapshot.DirectoryPath.TrimEnd(
                System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar))!);
        Assert.Equal(System.IO.Path.GetFullPath(world.BackupRoot.Path), parent);
        Assert.Equal(System.IO.Path.GetFileName(snapshot.DirectoryPath.TrimEnd(
            System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)), snapshot.Id);
    }

    [Fact]
    public void The_manifest_lists_exactly_the_in_scope_files()
    {
        using var world = new BackupWorld();

        var snapshot = world.Service.CreateBackup("first", null);

        var listed = snapshot.Manifest!.Files.Select(f => f.RelativePath);
        Assert.Equal(SaveTree.Sorted(SaveTree.InScope), SaveTree.Sorted(listed));
    }

    [Fact]
    public void Manifest_sizes_and_hashes_match_a_recomputed_sha256_of_the_live_files()
    {
        using var world = new BackupWorld();

        var snapshot = world.Service.CreateBackup("first", null);

        foreach (var entry in snapshot.Manifest!.Files)
        {
            var livePath = world.Live.Resolve(entry.RelativePath);
            Assert.True(File.Exists(livePath), $"{entry.RelativePath} is not a live file");
            Assert.Equal(new FileInfo(livePath).Length, entry.SizeBytes);
            Assert.Equal(SnapshotLayout.Sha256(livePath), entry.Sha256, ignoreCase: true);
        }
    }

    [Fact]
    public void The_copies_are_byte_identical_to_the_live_files()
    {
        using var world = new BackupWorld();

        var snapshot = world.Service.CreateBackup("first", null);

        foreach (var relativePath in SaveTree.InScope)
        {
            var copied = SnapshotLayout.FindFile(snapshot, relativePath);
            Assert.NotNull(copied);
            SnapshotLayout.AssertBytesEqual(world.Live.ReadBytes(relativePath), File.ReadAllBytes(copied!), relativePath);
        }
    }

    [Fact]
    public void The_padded_container_files_keep_their_trailing_nul_bytes()
    {
        using var world = new BackupWorld();

        var snapshot = world.Service.CreateBackup("first", null);

        var copied = File.ReadAllBytes(SnapshotLayout.FindFile(snapshot, "sav2")!);
        Assert.Equal(98288, copied.Length);
        Assert.Equal(0, (int)copied[^1]);
    }

    [Fact]
    public void Nothing_out_of_scope_is_copied_into_the_snapshot()
    {
        using var world = new BackupWorld();

        var snapshot = world.Service.CreateBackup("first", null);

        foreach (var relativePath in SaveTree.OutOfScope)
        {
            Assert.Null(SnapshotLayout.FindFile(snapshot, relativePath));
        }
    }

    [Fact]
    public void The_manifest_embeds_the_slot_metadata_read_at_backup_time()
    {
        using var world = new BackupWorld();

        var snapshot = world.Service.CreateBackup("first", null);

        var slots = snapshot.Manifest!.Slots;
        Assert.NotEmpty(slots);
        Assert.Contains(slots, s => s.Slot == 1 && s.FileName == "sav");
        Assert.Contains(slots, s => s.Slot == 2 && s.FileName == "sav2");
        Assert.Contains(slots, s => s.Slot == 3 && s.FileName == "sav3");

        var slotTwo = slots.Single(s => s.Slot == 2);
        Assert.Null(slotTwo.ParseError);
        Assert.Contains(slotTwo.Campaigns, c => c.SlugcatId == "White" && c.CycleNum == 17);
    }

    [Fact]
    public void The_live_files_are_untouched_by_a_backup()
    {
        using var world = new BackupWorld();
        var before = world.Live.ReadTree();

        world.Service.CreateBackup("first", null);

        SnapshotLayout.AssertTreeUnchanged(before, world.Live.ReadTree());
    }

    [Fact]
    public void Two_backups_taken_in_the_same_second_land_in_separate_directories()
    {
        using var world = new BackupWorld();

        var first = world.Service.CreateBackup("a", null);
        var second = world.Service.CreateBackup("b", null);

        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(first.DirectoryPath, second.DirectoryPath);
        Assert.True(Directory.Exists(first.DirectoryPath));
        Assert.True(Directory.Exists(second.DirectoryPath));
        Assert.Equal(2, world.Service.ListBackups().Count);
    }

    [Fact]
    public void CreateBackup_accepts_a_progress_reporter_without_failing()
    {
        using var world = new BackupWorld();
        var progress = new CollectingProgress();

        var snapshot = world.Service.CreateBackup("first", null, BackupKind.Manual, progress);

        Assert.True(snapshot.IsComplete);
    }

    [Fact]
    public void A_pre_restore_safety_backup_records_its_kind()
    {
        using var world = new BackupWorld();

        var snapshot = world.Service.CreateBackup(null, null, BackupKind.PreRestoreSafety);

        Assert.Equal(BackupKind.PreRestoreSafety, snapshot.Manifest!.Kind);
    }

    [Fact]
    public void CreateBackup_refuses_while_the_game_is_running()
    {
        using var world = new BackupWorld(FakeGameDetector.Running("RainWorld"));

        var error = Assert.Throws<GameRunningException>(() => world.Service.CreateBackup("first", null));

        Assert.Equal("RainWorld", error.ProcessName);
    }

    [Fact]
    public void CreateBackup_writes_nothing_when_the_game_is_running()
    {
        using var world = new BackupWorld(FakeGameDetector.Running("Rain World"));
        var liveBefore = world.Live.ReadTree();

        Assert.Throws<GameRunningException>(() => world.Service.CreateBackup("first", null));

        Assert.Empty(world.Service.ListBackups());
        Assert.Empty(world.BackupRoot.ReadTree());
        SnapshotLayout.AssertTreeUnchanged(liveBefore, world.Live.ReadTree());
    }

    [Fact]
    public void SaveRoot_and_BackupRoot_are_the_paths_the_service_was_built_with()
    {
        using var world = new BackupWorld();

        Assert.Equal(world.Live.Path, world.Service.SaveRoot);
        Assert.Equal(world.BackupRoot.Path, world.Service.BackupRoot);
    }

    [Fact]
    public void ListBackups_returns_the_snapshots_newest_first()
    {
        using var world = new BackupWorld();
        var older = world.Service.CreateBackup("older", null);
        Thread.Sleep(1200);
        var newer = world.Service.CreateBackup("newer", null);

        var listed = world.Service.ListBackups();

        Assert.Equal(2, listed.Count);
        Assert.Equal(newer.Id, listed[0].Id);
        Assert.Equal(older.Id, listed[1].Id);
        Assert.True(listed[0].CreatedUtc >= listed[1].CreatedUtc);
    }

    [Fact]
    public void A_snapshot_directory_with_no_manifest_lists_as_incomplete_with_a_reason()
    {
        using var world = new BackupWorld();
        var good = world.Service.CreateBackup("good", null);
        world.BackupRoot.WriteText(@"broken-snapshot\sav", "not a real snapshot");

        var listed = world.Service.ListBackups();

        Assert.Equal(2, listed.Count);
        var broken = listed.Single(s => s.Id == "broken-snapshot");
        Assert.False(broken.IsComplete);
        Assert.Null(broken.Manifest);
        Assert.False(string.IsNullOrWhiteSpace(broken.Problem));

        var stillGood = listed.Single(s => s.Id == good.Id);
        Assert.True(stillGood.IsComplete);
        Assert.NotNull(stillGood.Manifest);
    }

    [Fact]
    public void A_snapshot_with_an_unreadable_manifest_lists_as_incomplete_with_a_reason()
    {
        using var world = new BackupWorld();
        world.Service.CreateBackup("good", null);
        world.BackupRoot.WriteText(@"corrupt-snapshot\manifest.json", "{ this is not json ]");

        var listed = world.Service.ListBackups();

        var corrupt = listed.Single(s => s.Id == "corrupt-snapshot");
        Assert.False(corrupt.IsComplete);
        Assert.False(string.IsNullOrWhiteSpace(corrupt.Problem));
        Assert.Equal(2, listed.Count);
    }

    [Fact]
    public void An_incomplete_snapshot_still_reports_a_creation_time_from_its_directory()
    {
        using var world = new BackupWorld();
        world.BackupRoot.WriteText(@"broken-snapshot\sav", "not a real snapshot");

        var broken = Assert.Single(world.Service.ListBackups());

        Assert.NotEqual(default(DateTime), broken.CreatedUtc);
    }

    [Fact]
    public void A_missing_backup_root_lists_as_empty()
    {
        using var live = new TempDirectory("live");
        using var parent = new TempDirectory("backups-parent");
        SaveTree.Populate(live);
        var service = new BackupService(
            live.Path, parent.Resolve("no-such-backup-root"), FakeGameDetector.NotRunning(), AppVersion);

        Assert.Empty(service.ListBackups());
    }

    [Fact]
    public void An_empty_backup_root_lists_as_empty()
    {
        using var world = new BackupWorld();

        Assert.Empty(world.Service.ListBackups());
    }

    [Fact]
    public void Verify_passes_on_a_fresh_backup()
    {
        using var world = new BackupWorld();
        var snapshot = world.Service.CreateBackup("first", null);

        var result = world.Service.Verify(snapshot);

        Assert.True(result.Ok, string.Join("; ", result.Problems));
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void Verify_fails_after_a_snapshot_file_is_altered()
    {
        using var world = new BackupWorld();
        var snapshot = world.Service.CreateBackup("first", null);
        var copied = SnapshotLayout.FindFile(snapshot, "sav2")!;
        var bytes = File.ReadAllBytes(copied);
        bytes[100] ^= 0xFF;
        File.WriteAllBytes(copied, bytes);

        var result = world.Service.Verify(world.Service.ListBackups().Single(s => s.Id == snapshot.Id));

        Assert.False(result.Ok);
        Assert.NotEmpty(result.Problems);
    }

    [Fact]
    public void Verify_fails_when_a_listed_file_has_been_deleted_from_the_snapshot()
    {
        using var world = new BackupWorld();
        var snapshot = world.Service.CreateBackup("first", null);
        File.Delete(SnapshotLayout.FindFile(snapshot, "online_sav")!);

        var result = world.Service.Verify(world.Service.ListBackups().Single(s => s.Id == snapshot.Id));

        Assert.False(result.Ok);
        Assert.NotEmpty(result.Problems);
    }

    [Fact]
    public void Verify_fails_on_a_snapshot_with_no_manifest()
    {
        using var world = new BackupWorld();
        world.BackupRoot.WriteText(@"broken-snapshot\sav", "not a real snapshot");
        var broken = Assert.Single(world.Service.ListBackups());

        var result = world.Service.Verify(broken);

        Assert.False(result.Ok);
        Assert.NotEmpty(result.Problems);
    }

    [Fact]
    public void DeleteBackup_removes_the_snapshot_directory()
    {
        using var world = new BackupWorld();
        var keep = world.Service.CreateBackup("keep", null);
        var drop = world.Service.CreateBackup("drop", null);

        world.Service.DeleteBackup(drop);

        Assert.False(Directory.Exists(drop.DirectoryPath));
        Assert.True(Directory.Exists(keep.DirectoryPath));
        Assert.Equal(keep.Id, Assert.Single(world.Service.ListBackups()).Id);
    }

    [Fact]
    public void DeleteBackup_refuses_a_snapshot_outside_its_own_backup_root()
    {
        using var world = new BackupWorld();
        using var otherRoot = new TempDirectory("other-backups");
        var otherService = new BackupService(
            world.Live.Path, otherRoot.Path, FakeGameDetector.NotRunning(), AppVersion);
        var foreign = otherService.CreateBackup("elsewhere", null);

        Assert.ThrowsAny<Exception>(() => world.Service.DeleteBackup(foreign));

        Assert.True(Directory.Exists(foreign.DirectoryPath));
        Assert.NotNull(SnapshotLayout.FindFile(foreign, "sav2"));
    }

    [Fact]
    public void ReadLiveSlots_describes_the_three_ui_slots()
    {
        using var world = new BackupWorld();

        var slots = world.Service.ReadLiveSlots();

        Assert.Contains(slots, s => s.Slot == 1 && s.FileName == "sav");
        Assert.Contains(slots, s => s.Slot == 2 && s.FileName == "sav2");
        Assert.Contains(slots, s => s.Slot == 3 && s.FileName == "sav3");
        Assert.Contains(slots.Single(s => s.Slot == 3).Campaigns, c => c.CycleNum == 9 && c.DevourmentStateCount == 4);
    }

    [Fact]
    public void ReadLiveSlots_ignores_the_stray_copies_beside_sav()
    {
        using var world = new BackupWorld();

        var slots = world.Service.ReadLiveSlots();

        Assert.DoesNotContain(slots, s => s.FileName == "sav - Copy");
        Assert.DoesNotContain(slots, s => s.FileName == "sav - Copy (2)");
        Assert.DoesNotContain(slots, s => s.FileName == "sav.bak");
    }

    [Fact]
    public void ReadLiveSlots_survives_a_save_folder_that_is_not_there()
    {
        using var parent = new TempDirectory();
        using var backupRoot = new TempDirectory("backups");
        var service = new BackupService(
            parent.Resolve("no-such-save-folder"), backupRoot.Path, FakeGameDetector.NotRunning(), AppVersion);

        var slots = service.ReadLiveSlots();

        Assert.NotNull(slots);
    }

    [Fact]
    public void ReadLiveSlots_reports_a_parse_error_rather_than_throwing_on_a_broken_slot()
    {
        using var world = new BackupWorld();
        world.Live.WriteBytes("sav2", SyntheticSave.GarbageBytes());

        var slots = world.Service.ReadLiveSlots();

        var broken = slots.Single(s => s.FileName == "sav2");
        Assert.False(string.IsNullOrWhiteSpace(broken.ParseError));
        Assert.Null(slots.Single(s => s.FileName == "sav3").ParseError);
    }
}

/// <summary>
/// A live tree, a separate backup root, and a service wired to a fake detector. Shared by the
/// backup and restore suites so both run against the same layout.
/// </summary>
internal sealed class BackupWorld : IDisposable
{
    public const string AppVersion = "1.0.0-test";

    public BackupWorld(FakeGameDetector? detector = null)
    {
        Live = new TempDirectory("live");
        BackupRoot = new TempDirectory("backups");
        SaveTree.Populate(Live);
        Detector = detector ?? FakeGameDetector.NotRunning();
        Service = new BackupService(Live.Path, BackupRoot.Path, Detector, AppVersion);
    }

    public TempDirectory Live { get; }

    public TempDirectory BackupRoot { get; }

    /// <summary>Flip <see cref="FakeGameDetector.RunningProcessName"/> to make the game appear mid-test.</summary>
    public FakeGameDetector Detector { get; }

    public BackupService Service { get; }

    public void Dispose()
    {
        Live.Dispose();
        BackupRoot.Dispose();
    }
}

/// <summary>
/// The contract does not fix where inside a snapshot directory the copies live, so tests locate
/// them by relative path instead of assuming a layout.
/// </summary>
internal static class SnapshotLayout
{
    public static string? FindFile(BackupSnapshot snapshot, string relativePath)
    {
        var normalized = relativePath.Replace('/', '\\');

        var direct = System.IO.Path.Combine(snapshot.DirectoryPath, normalized);
        if (File.Exists(direct))
        {
            return direct;
        }

        var suffix = '\\' + normalized;
        foreach (var candidate in Directory.GetFiles(snapshot.DirectoryPath, "*", SearchOption.AllDirectories))
        {
            if (string.Equals(System.IO.Path.GetFileName(candidate), "manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (candidate.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static void AssertBytesEqual(byte[] expected, byte[] actual, string label)
    {
        Assert.True(
            expected.Length == actual.Length,
            $"{label}: expected {expected.Length} bytes but found {actual.Length}");
        Assert.True(expected.AsSpan().SequenceEqual(actual), $"{label}: contents differ");
    }

    public static void AssertTreeUnchanged(Dictionary<string, byte[]> before, Dictionary<string, byte[]> after)
    {
        Assert.Equal(
            before.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray(),
            after.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray());

        foreach (var entry in before)
        {
            AssertBytesEqual(entry.Value, after[entry.Key], entry.Key);
        }
    }
}
