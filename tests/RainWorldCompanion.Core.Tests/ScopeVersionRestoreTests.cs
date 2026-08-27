using System.Text;
using System.Text.Json.Nodes;
using RainWorldCompanion.Core.Backups;

namespace RainWorldCompanion.Tests;

public class ScopeVersionRestoreTests
{
    private const string SteamManifestInsideAScopeFolder = @"dvrmentSaveStates\steam_autocloud.vdf";

    [Fact]
    public void An_old_snapshot_holding_a_now_excluded_file_still_restores()
    {
        using var world = new WideWorld();
        var snapshot = TakeNarrowSnapshotHoldingTheSteamManifest(world);

        var result = world.Service.RestoreBackup(snapshot);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void The_now_excluded_file_is_not_written_back_and_the_restore_says_so()
    {
        using var world = new WideWorld();
        var snapshot = TakeNarrowSnapshotHoldingTheSteamManifest(world);

        var result = world.Service.RestoreBackup(snapshot);

        Assert.False(
            world.Live.FileExists(SteamManifestInsideAScopeFolder),
            "a stale Steam sync manifest was written back into the save folder");
        Assert.Contains(
            result.Warnings,
            warning => warning.Contains("steam_autocloud.vdf", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_deletion_step_still_runs_when_the_snapshot_holds_a_now_excluded_file()
    {
        // The cost of treating this entry as an error: every save file lands, the restore reports
        // failure, and the file written after the backup survives, merging two moments instead of
        // restoring the one the backup holds.
        const string Extra = @"dvrmentSaveStates\contents_9_Rivulet_story.txt";

        using var world = new WideWorld();
        var snapshot = TakeNarrowSnapshotHoldingTheSteamManifest(world);
        world.Live.WriteText(Extra, "Rivulet|Slugcat|9|stomach");

        var result = world.Service.RestoreBackup(snapshot);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.False(world.Live.FileExists(Extra), "the restore skipped the deletion step");
    }

    [Fact]
    public void The_plan_lists_the_now_excluded_file_as_one_that_will_not_be_written_back()
    {
        // The dialog is confirmed before the restore runs, so it has to name the file there rather
        // than promise to add it and then skip it.
        using var world = new WideWorld();
        var snapshot = TakeNarrowSnapshotHoldingTheSteamManifest(world);

        var plan = world.Service.PlanRestore(snapshot);

        Assert.Contains(SaveTree.Normalize(SteamManifestInsideAScopeFolder), SaveTree.Sorted(plan.NotRestored));
        Assert.DoesNotContain(SaveTree.Normalize(SteamManifestInsideAScopeFolder), SaveTree.Sorted(plan.Added));
        Assert.DoesNotContain(SaveTree.Normalize(SteamManifestInsideAScopeFolder), SaveTree.Sorted(plan.Overwritten));
    }

    [Fact]
    public void A_manifest_path_this_app_never_managed_is_still_an_error()
    {
        // The skip above must not become a general excuse. A manifest naming "options" is a broken
        // manifest, not an exclusion added later, and the restore has to refuse it as it always did.
        using var world = new WideWorld();
        var snapshot = world.Service.CreateBackup("current", null);
        AddFileToSnapshot(snapshot, "options", "resolution<optB>1920");
        var reloaded = BackupSnapshot.Load(snapshot.DirectoryPath);

        var result = world.Service.RestoreBackup(reloaded);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("options", StringComparison.Ordinal));
    }

    [Fact]
    public void A_restore_that_changes_nothing_leaves_an_already_empty_scope_folder_alone()
    {
        // Warp\Export is empty on the real install. A sweep for any empty folder under the scope
        // roots removes it on a restore that never touched Warp at all.
        using var world = new WideWorld();
        var snapshot = world.Service.CreateBackup("current", null);

        var result = world.Service.RestoreBackup(snapshot);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.True(
            Directory.Exists(world.Live.Resolve(@"Warp\Export")),
            "a restore that emptied nothing removed a folder that was already empty");
    }

    [Fact]
    public void Restoring_a_version_1_snapshot_does_not_reach_into_the_folders_version_1_never_covered()
    {
        using var world = new WideWorld();
        var snapshot = world.NarrowService.CreateBackup("narrow", null);
        WideSaveTree.AddTheWidenedFiles(world.Live);
        world.Live.CreateSubdirectory(@"RandomBuff\Cache");

        var result = world.Service.RestoreBackup(snapshot);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.True(
            Directory.Exists(world.Live.Resolve(@"Warp\Export")),
            "a version 1 restore removed an empty folder under Warp, which version 1 never covered");
        Assert.True(
            Directory.Exists(world.Live.Resolve(@"RandomBuff\Cache")),
            "a version 1 restore removed an empty folder under RandomBuff, which version 1 never covered");
    }

    [Fact]
    public void A_folder_this_restore_did_empty_is_still_removed()
    {
        // The mirror image. Without this, "never remove a folder" would also pass every test above.
        using var world = new WideWorld();
        var snapshot = world.Service.CreateBackup("current", null);
        world.Live.WriteText(@"dvrmentSaveStates\later\contents_9_Rivulet_story.txt", "Rivulet|Slugcat|9|stomach");

        var result = world.Service.RestoreBackup(snapshot);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.False(
            Directory.Exists(world.Live.Resolve(@"dvrmentSaveStates\later")),
            "the folder the restore emptied was left behind");
        Assert.True(
            Directory.Exists(world.Live.Resolve("dvrmentSaveStates")),
            "the scope folder itself was removed");
    }

    /// <summary>
    /// A version 1 snapshot holding steam_autocloud.vdf inside dvrmentSaveStates, which is what
    /// the shipped build wrote before the exclusion existed. Current rules cannot produce this
    /// state, so the file is added to the snapshot and its manifest directly.
    /// </summary>
    private static BackupSnapshot TakeNarrowSnapshotHoldingTheSteamManifest(WideWorld world)
    {
        // Deliberately not written into the live folder. Its absence there afterwards is what says
        // the restore did not put it back, and a copy left lying there would answer that for it.
        var snapshot = world.NarrowService.CreateBackup("narrow", null);
        AddFileToSnapshot(snapshot, SteamManifestInsideAScopeFolder, "\"RootPaths\"\n{\n}\n");

        return BackupSnapshot.Load(snapshot.DirectoryPath);
    }

    /// <summary>
    /// Puts one more file into a finished snapshot and records it in the manifest with a correct
    /// size and hash, so the snapshot still verifies and the restore reaches the copy loop.
    /// </summary>
    private static void AddFileToSnapshot(BackupSnapshot snapshot, string relativePath, string content)
    {
        var full = Path.Combine(snapshot.DirectoryPath, relativePath);
        var parent = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        File.WriteAllBytes(full, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content));

        var info = new FileInfo(full);
        var json = JsonNode.Parse(File.ReadAllText(snapshot.ManifestPath))!.AsObject();
        json["files"]!.AsArray().Add(new JsonObject
        {
            ["relativePath"] = relativePath,
            ["sizeBytes"] = info.Length,
            ["sha256"] = Hashing.ComputeFileSha256(full),
            ["lastWriteUtc"] = info.LastWriteTimeUtc,
        });

        File.WriteAllText(snapshot.ManifestPath, json.ToJsonString());
    }
}
