using System.Text;
using System.Text.Json.Nodes;
using RainWorldCompanion.Core.Backups;

namespace RainWorldCompanion.Tests;

/// <summary>
/// What a restore is allowed to touch on behalf of a snapshot written under older rules.
///
/// ScopeWideningTests covers the deletion side: a version 1 snapshot does not delete the files
/// version 2 brought into scope. Three more things follow from the same versioning and are checked
/// here.
///
/// The first is the other direction of an exclusion. steam_autocloud.vdf is left out at every
/// version, because leaving a file out can only make a restore delete fewer files. It also makes a
/// restore put back fewer files, and a snapshot taken by the shipped build over a folder whose
/// dvrmentSaveStates held one has it in its manifest. Treating that entry as a broken manifest
/// fails the whole restore and, worse, skips the deletion step, turning a return to one moment into
/// a merge that reports failure.
///
/// The second is the folders a restore may tidy. The empty-folder sweep has to be gated by the
/// snapshot's version the same way the file deletion is, or restoring a version 1 snapshot reaches
/// into trees version 1 never covered and the confirmation dialog never listed.
///
/// The third is that the sweep may only remove folders this restore emptied. Warp\Export ships
/// empty, so a sweep for any empty folder takes it away on a restore that changed nothing in there.
/// </summary>
public class ScopeVersionRestoreTests
{
    private const string SteamManifestInsideAScopeFolder = @"dvrmentSaveStates\steam_autocloud.vdf";

    // ---- an exclusion added after the snapshot was taken ----

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
        // This is the real cost of treating the entry as an error. Every save file lands, the
        // restore reports failure, and the file written after the backup survives, so the folder
        // is a merge of two moments rather than the one the backup holds.
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

    // ---- the empty-folder sweep ----

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
        // The mirror image. Without this the fix could be "never remove a folder" and everything
        // above would still pass.
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
    /// A version 1 snapshot that holds a steam_autocloud.vdf inside dvrmentSaveStates, which is
    /// what the shipped build wrote: its rules took that folder whole and had no exclusion. The
    /// current rules cannot produce one, so the file is put into the snapshot and into its manifest
    /// directly.
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
