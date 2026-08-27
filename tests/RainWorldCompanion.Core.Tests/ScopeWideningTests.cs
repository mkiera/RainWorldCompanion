using System.Text.Json.Nodes;
using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Tests;

/// <summary>
/// A snapshot records the scope rules it was written under, and a restore deletes a live file
/// only when the file is in scope under both the current rules and the rules the snapshot was
/// written under. A manifest with no recorded version is read as version 1.
/// </summary>
public class ScopeWideningTests
{
    [Fact]
    public void Enumerate_returns_exactly_the_in_scope_files_of_the_whole_save_folder()
    {
        using var live = new TempDirectory("live");
        WideSaveTree.Populate(live);

        var found = new BackupScope(live.Path).Enumerate().Select(e => e.RelativePath);

        Assert.Equal(SaveTree.Sorted(WideSaveTree.InScope), SaveTree.Sorted(found));
    }

    [Fact]
    public void IsInScope_agrees_with_Enumerate_for_every_path_in_the_mirror()
    {
        using var live = new TempDirectory("live");
        WideSaveTree.Populate(live);
        var scope = new BackupScope(live.Path);
        var enumerated = new HashSet<string>(
            scope.Enumerate().Select(e => SaveTree.Normalize(e.RelativePath)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var relativePath in WideSaveTree.Everything)
        {
            var expected = enumerated.Contains(SaveTree.Normalize(relativePath));

            Assert.Equal(expected, scope.IsInScope(relativePath));
        }
    }

    [Fact]
    public void IsInScope_gives_the_same_answer_for_both_separator_styles()
    {
        using var live = new TempDirectory("live");
        WideSaveTree.Populate(live);
        var scope = new BackupScope(live.Path);

        foreach (var relativePath in WideSaveTree.Everything)
        {
            var backslash = relativePath.Replace('/', '\\');
            var forwardSlash = relativePath.Replace('\\', '/');

            Assert.Equal(scope.IsInScope(backslash), scope.IsInScope(forwardSlash));
        }
    }

    [Theory]
    [InlineData("meadow.json")]
    [InlineData("buffMain100")]
    [InlineData("buffsave100")]
    [InlineData("online_sav")]
    [InlineData("online_sav2")]
    [InlineData("online_sav3")]
    [InlineData(@"ModConfigs\henpemaz_rainmeadow.txt")]
    [InlineData(@"ModConfigs\moreslugcats.txt")]
    [InlineData(@"ModConfigs\randombuff.txt")]
    [InlineData(@"dressmyslugcat\customization.dat")]
    [InlineData(@"RandomBuff\EnableBuffPlugins.txt")]
    [InlineData(@"Warp\Settings.txt")]
    public void The_widened_rules_take_the_save_data_and_mod_configuration(string relativePath)
    {
        using var live = new TempDirectory("live");
        WideSaveTree.Populate(live);

        var found = SaveTree.Sorted(new BackupScope(live.Path).Enumerate().Select(e => e.RelativePath));

        Assert.Contains(SaveTree.Normalize(relativePath), found);
    }

    [Theory]
    [InlineData("sav - Copy")]
    [InlineData("sav - Copy (2)")]
    [InlineData("sav.bak")]
    public void The_stray_manual_copies_are_still_excluded(string decoy)
    {
        using var live = new TempDirectory("live");
        WideSaveTree.Populate(live);

        var found = SaveTree.Sorted(new BackupScope(live.Path).Enumerate().Select(e => e.RelativePath));

        Assert.DoesNotContain(SaveTree.Normalize(decoy), found);
    }

    [Theory]
    [InlineData("steam_autocloud.vdf")]
    [InlineData(@"ModConfigs\steam_autocloud.vdf")]
    [InlineData(@"SJ_0\steam_autocloud.vdf")]
    [InlineData(@"cloud\steam_autocloud.vdf")]
    public void Every_copy_of_steam_autocloud_is_excluded_wherever_it_sits(string relativePath)
    {
        using var live = new TempDirectory("live");
        WideSaveTree.Populate(live);

        var found = SaveTree.Sorted(new BackupScope(live.Path).Enumerate().Select(e => e.RelativePath));

        Assert.DoesNotContain(SaveTree.Normalize(relativePath), found);
    }

    [Theory]
    [InlineData("options")]
    [InlineData("localoptions.txt")]
    [InlineData(@"SJ_0\karcap1.png")]
    [InlineData(@"SJ_1\karcap1.png")]
    [InlineData(@"SJ_2\karcap1.png")]
    [InlineData(@"backup\2026-08-24_120000\sav")]
    public void Game_settings_karma_screenshots_and_the_games_own_backups_stay_excluded(string relativePath)
    {
        using var live = new TempDirectory("live");
        WideSaveTree.Populate(live);

        var found = SaveTree.Sorted(new BackupScope(live.Path).Enumerate().Select(e => e.RelativePath));

        Assert.DoesNotContain(SaveTree.Normalize(relativePath), found);
    }

    [Theory]
    [InlineData("online_sav")]
    [InlineData("online_sav2")]
    [InlineData("online_sav0")]
    [InlineData("online_sav-1")]
    [InlineData("online_sav-2")]
    public void Every_name_the_Rain_Meadow_hook_can_write_is_backed_up(string fileName)
    {
        // The hook's guard is "saveSlot is not 0", and the base game uses a negative saveSlot for
        // Expedition, so joining a lobby from an Expedition slot writes "online_sav" +
        // (saveSlot + 1), which can be negative. Those are real saves the scope must still match.
        using var live = new TempDirectory("live");
        WideSaveTree.Populate(live);
        FixtureFiles.CopyTo(live, FixtureFiles.Sav3, fileName);

        var found = SaveTree.Sorted(new BackupScope(live.Path).Enumerate().Select(e => e.RelativePath));

        Assert.Contains(SaveTree.Normalize(fileName), found);
    }

    [Theory]
    [InlineData("online_sav-")]
    [InlineData("online_sav-x")]
    [InlineData("online_sav - Copy")]
    public void A_name_the_hook_cannot_write_is_still_left_alone(string fileName)
    {
        using var live = new TempDirectory("live");
        WideSaveTree.Populate(live);
        FixtureFiles.CopyTo(live, FixtureFiles.Sav3, fileName);

        var found = SaveTree.Sorted(new BackupScope(live.Path).Enumerate().Select(e => e.RelativePath));

        Assert.DoesNotContain(SaveTree.Normalize(fileName), found);
    }

    [Theory]
    [InlineData("online_sav0")]
    [InlineData("online_sav-1")]
    public void A_negative_online_name_is_backed_up_but_is_not_a_menu_slot(string fileName)
    {
        // The game's menu has only three slots, and these names are not among them.
        Assert.Null(SaveMetadataExtractor.SlotForFileName(fileName));
    }

    [Fact]
    public void The_current_scope_version_is_past_the_first_one()
    {
        // The whole fix rests on the two rule sets being distinguishable.
        Assert.True(BackupScope.CurrentScopeVersion > 1);
    }

    [Fact]
    public void A_new_snapshot_records_the_scope_version_it_was_written_under()
    {
        using var world = new WideWorld();

        var snapshot = world.Service.CreateBackup("current", null);

        Assert.Equal(BackupScope.CurrentScopeVersion, snapshot.Manifest!.ScopeVersion);
    }

    [Fact]
    public void An_old_snapshot_holds_only_the_files_the_old_scope_covered()
    {
        using var world = new WideWorld();

        var snapshot = world.NarrowService.CreateBackup("narrow", null);

        var files = SaveTree.Sorted(snapshot.Manifest!.Files.Select(f => f.RelativePath));
        Assert.Contains("sav2", files);
        Assert.Contains(@"ModConfigs\devourment.txt", files);
        Assert.DoesNotContain("meadow.json", files);
        Assert.DoesNotContain("buffsave100", files);
        Assert.Equal(1, snapshot.Manifest.ScopeVersion);
    }

    [Fact]
    public void Planning_an_old_snapshot_does_not_offer_to_delete_files_the_old_scope_never_covered()
    {
        using var world = new WideWorld();
        var snapshot = world.NarrowService.CreateBackup("narrow", null);
        WideSaveTree.AddTheWidenedFiles(world.Live);

        var plan = world.Service.PlanRestore(snapshot);

        // Nothing diverged except files the old rules never reached, so the confirmation dialog
        // has nothing to list as deleted.
        Assert.Empty(plan.Deleted);
    }

    [Fact]
    public void Restoring_an_old_snapshot_leaves_the_files_the_old_scope_never_covered()
    {
        using var world = new WideWorld();
        var snapshot = world.NarrowService.CreateBackup("narrow", null);
        WideSaveTree.AddTheWidenedFiles(world.Live);
        var meadowBefore = world.Live.ReadBytes("meadow.json");
        var buffBefore = world.Live.ReadBytes("buffsave100");

        var result = world.Service.RestoreBackup(snapshot);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.True(world.Live.FileExists("meadow.json"), "restoring a narrow-scope snapshot deleted meadow.json");
        Assert.True(world.Live.FileExists("buffsave100"), "restoring a narrow-scope snapshot deleted buffsave100");
        SnapshotLayout.AssertBytesEqual(meadowBefore, world.Live.ReadBytes("meadow.json"), "meadow.json");
        SnapshotLayout.AssertBytesEqual(buffBefore, world.Live.ReadBytes("buffsave100"), "buffsave100");

        Assert.True(world.Live.FileExists(@"ModConfigs\moreslugcats.txt"), "a mod config the old scope never covered was deleted");
        Assert.True(world.Live.FileExists(@"Warp\Settings.txt"), "a Warp setting the old scope never covered was deleted");
        Assert.True(world.Live.FileExists(@"dressmyslugcat\customization.dat"), "a slugcat appearance the old scope never covered was deleted");
    }

    [Fact]
    public void A_manifest_with_no_recorded_scope_version_is_read_as_the_first_scope()
    {
        // The real backups on disk were written before the field existed. Their manifests carry
        // no scopeVersion at all, which has to mean version 1 rather than "whatever is current".
        using var world = new WideWorld();
        var written = world.NarrowService.CreateBackup("legacy", null);
        StripScopeVersion(written);
        var snapshot = BackupSnapshot.Load(written.DirectoryPath);
        WideSaveTree.AddTheWidenedFiles(world.Live);

        var plan = world.Service.PlanRestore(snapshot);
        var result = world.Service.RestoreBackup(snapshot);

        Assert.DoesNotContain("meadow.json", SaveTree.Sorted(plan.Deleted));
        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.True(world.Live.FileExists("meadow.json"), "a manifest with no scope version deleted meadow.json");
        Assert.True(world.Live.FileExists("buffsave100"), "a manifest with no scope version deleted buffsave100");
    }

    [Fact]
    public void Planning_a_current_snapshot_still_offers_to_delete_an_in_scope_file_it_lacks()
    {
        using var world = new WideWorld();
        File.Delete(world.Live.Resolve("meadow.json"));
        var snapshot = world.Service.CreateBackup("current", null);
        WideSaveTree.AddTheWidenedFiles(world.Live);

        var plan = world.Service.PlanRestore(snapshot);

        Assert.Contains("meadow.json", SaveTree.Sorted(plan.Deleted));
    }

    [Fact]
    public void Restoring_a_current_snapshot_still_deletes_an_in_scope_file_it_lacks()
    {
        // The mirror image: without this, "never delete anything" would also pass every test here.
        using var world = new WideWorld();
        File.Delete(world.Live.Resolve("meadow.json"));
        var snapshot = world.Service.CreateBackup("current", null);
        WideSaveTree.AddTheWidenedFiles(world.Live);

        var result = world.Service.RestoreBackup(snapshot);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.False(world.Live.FileExists("meadow.json"), "a current-scope snapshot failed to delete a file it does not hold");
    }

    [Fact]
    public void An_old_snapshot_still_deletes_a_file_the_old_scope_did_cover()
    {
        // The narrowing is per rule set, not per file. dvrmentSaveStates was in scope in version
        // 1, so a version 1 snapshot that lacks one of its files still removes it.
        const string Extra = @"dvrmentSaveStates\contents_9_Rivulet_story.txt";

        using var world = new WideWorld();
        var snapshot = world.NarrowService.CreateBackup("narrow", null);
        world.Live.WriteText(Extra, "Rivulet|Slugcat|9|stomach");
        WideSaveTree.AddTheWidenedFiles(world.Live);

        var plan = world.Service.PlanRestore(snapshot);
        var result = world.Service.RestoreBackup(snapshot);

        Assert.Contains(SaveTree.Normalize(Extra), SaveTree.Sorted(plan.Deleted));
        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.False(world.Live.FileExists(Extra), "a file the old scope covered survived a restore");
        Assert.True(world.Live.FileExists("meadow.json"), "a file the old scope never covered was deleted");
    }

    [Fact]
    public void The_safety_snapshot_taken_before_an_old_restore_still_holds_the_widened_files()
    {
        // The safety copy is written under the current rules whatever the snapshot being
        // restored says, so it is the thing that can put the widened files back.
        using var world = new WideWorld();
        var snapshot = world.NarrowService.CreateBackup("narrow", null);
        WideSaveTree.AddTheWidenedFiles(world.Live);

        var result = world.Service.RestoreBackup(snapshot);

        var safety = result.SafetySnapshot;
        Assert.NotNull(safety);
        Assert.NotNull(SnapshotLayout.FindFile(safety!, "meadow.json"));
        Assert.NotNull(SnapshotLayout.FindFile(safety!, "buffsave100"));
    }

    [Fact]
    public void A_version_1_scope_enumerates_only_what_version_1_covered()
    {
        using var live = new TempDirectory("live");
        WideSaveTree.Populate(live);

        var found = SaveTree.Sorted(new BackupScope(live.Path, 1).Enumerate().Select(e => e.RelativePath));

        Assert.Contains(@"dvrmentSaveStates\contents_0_White_story.txt", found);
        Assert.DoesNotContain(@"Warp\Settings.txt", found);
        Assert.DoesNotContain(@"dressmyslugcat\customization.dat", found);
        Assert.DoesNotContain("meadow.json", found);
    }

    [JunctionFact]
    public void A_version_1_scope_reports_no_link_skip_for_a_folder_its_rules_never_covered()
    {
        // Enumerate walks the same folders IsInScope judges. Walking today's folder list under
        // version 1 rules still records every reparse point it meets, so a skip for a folder those
        // rules have no opinion about must not leak into a version 1 manifest as a warning.
        using var live = new TempDirectory("live");
        using var elsewhere = new TempDirectory("elsewhere");
        WideSaveTree.Populate(live);

        Directory.Delete(live.Resolve("dressmyslugcat"), recursive: true);
        Assert.True(
            Links.TryCreateDirectoryJunction(live.Resolve("dressmyslugcat"), elsewhere.Resolve("real")),
            "the junction could not be created");

        var narrow = new BackupScope(live.Path, 1).Scan();
        var current = new BackupScope(live.Path).Scan();

        Assert.DoesNotContain("dressmyslugcat", narrow.SkippedLinks, StringComparer.OrdinalIgnoreCase);

        // Under today's rules the same junction is a real skip, and saying so is the point of the
        // list. Only the version that never covered the folder stays quiet about it.
        Assert.Contains("dressmyslugcat", current.SkippedLinks, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Rewrites a manifest into the shape backups already on disk have: schema version 1, no
    /// scopeVersion property. The file list is untouched, so the snapshot still verifies.
    /// </summary>
    private static void StripScopeVersion(BackupSnapshot snapshot)
    {
        var json = JsonNode.Parse(File.ReadAllText(snapshot.ManifestPath))!.AsObject();
        json.Remove("scopeVersion");
        json["schemaVersion"] = 1;
        File.WriteAllText(snapshot.ManifestPath, json.ToJsonString());
    }
}

/// <summary>
/// A live folder holding the widened tree, with two services over the same backup root: one on
/// current rules and one pinned to the first rule set, so a pre-widening snapshot can be produced
/// without hand-building a manifest.
/// </summary>
internal sealed class WideWorld : IDisposable
{
    public WideWorld()
    {
        Live = new TempDirectory("live");
        BackupRoot = new TempDirectory("backups");
        WideSaveTree.Populate(Live);
        Detector = FakeGameDetector.NotRunning();
        Service = new BackupService(Live.Path, BackupRoot.Path, Detector, "1.0.0-test");
        NarrowService = new BackupService(
            Live.Path,
            BackupRoot.Path,
            Detector,
            "1.0.0-test",
            new BackupScope(Live.Path, 1));
    }

    public TempDirectory Live { get; }

    public TempDirectory BackupRoot { get; }

    public FakeGameDetector Detector { get; }

    /// <summary>The service the app uses, on the current scope rules.</summary>
    public BackupService Service { get; }

    /// <summary>A service pinned to scope version 1, standing in for the version already shipped.</summary>
    public BackupService NarrowService { get; }

    public void Dispose()
    {
        Live.Dispose();
        BackupRoot.Dispose();
    }
}

/// <summary>
/// A temp folder laid out like the real save folder, file for file, from a directory listing of
/// one live install rather than an invented structure.
/// </summary>
internal static class WideSaveTree
{
    /// <summary>Every path a backup must copy under the widened rules.</summary>
    public static readonly string[] InScope =
    {
        "buffMain100",
        "buffsave100",
        "buffsave101",
        "buffsave102",
        @"dressmyslugcat\customization.dat",
        @"dvrmentSaveStates\contents_0_White_story.txt",
        @"dvrmentSaveStates\contents_1_White_story.txt",
        @"dvrmentSaveStates\contents_2_White_story.txt",
        "exp1",
        "exp3",
        "expCore1",
        "expCore2",
        "expCore3",
        "meadow.json",
        @"ModConfigs\devourment.txt",
        @"ModConfigs\DvrmentConfs\current.json",
        @"ModConfigs\DvrmentConfs\preset_hungry.json",
        @"ModConfigs\habbit.karmacontrol.txt",
        @"ModConfigs\henpemaz_rainmeadow.txt",
        @"ModConfigs\MapOptions.txt",
        @"ModConfigs\moreslugcats.txt",
        @"ModConfigs\randombuff.txt",
        @"ModConfigs\SBCameraScroll.txt",
        @"ModConfigs\willowwisp.bellyplus.txt",
        "online_sav",
        "online_sav2",
        "online_sav3",
        @"RandomBuff\BuffPluginVersion.txt",
        @"RandomBuff\EnableBuffPlugins.txt",
        "sav",
        "sav2",
        "sav3",
        @"Warp\Colors.txt",
        @"Warp\Settings.txt",
    };

    /// <summary>Every path a backup must leave alone, and why it is there.</summary>
    public static readonly string[] OutOfScope =
    {
        // The user's own manual copies, sitting next to the files they were copied from.
        "sav - Copy",
        "sav - Copy (2)",
        "sav.bak",

        // Game settings: resolution, keybinds, arena setup. Rewritten constantly, not save data.
        "options",
        "localoptions.txt",

        // Steam's own sync manifests, one per folder it syncs.
        "steam_autocloud.vdf",
        @"ModConfigs\steam_autocloud.vdf",
        @"SJ_0\steam_autocloud.vdf",
        @"cloud\steam_autocloud.vdf",

        // Karma cap screenshots, several MB, written again the next time the cap changes.
        @"SJ_0\karcap1.png",
        @"SJ_1\karcap1.png",
        @"SJ_2\karcap1.png",

        // The game's own backup manager, 570 MB and 171 snapshots on the install this mirrors.
        @"backup\2026-08-24_120000\sav",
        @"backup\2026-08-24_120000\manifest.json",
    };

    public static IEnumerable<string> Everything => InScope.Concat(OutOfScope);

    public static void Populate(TempDirectory directory)
    {
        FixtureFiles.CopyTo(directory, FixtureFiles.Sav2, "sav");
        FixtureFiles.CopyTo(directory, FixtureFiles.Sav2, "sav2");
        FixtureFiles.CopyTo(directory, FixtureFiles.Sav3, "sav3");
        FixtureFiles.CopyTo(directory, FixtureFiles.Exp1, "exp1");
        FixtureFiles.CopyTo(directory, FixtureFiles.Exp1, "exp3");
        FixtureFiles.CopyTo(directory, FixtureFiles.ExpCore1, "expCore1");
        FixtureFiles.CopyTo(directory, FixtureFiles.Exp1, "expCore2");
        FixtureFiles.CopyTo(directory, FixtureFiles.ExpCore1, "expCore3");
        FixtureFiles.CopyTo(directory, FixtureFiles.OnlineSav, "online_sav");
        FixtureFiles.CopyTo(directory, FixtureFiles.Sav3, "online_sav2");
        FixtureFiles.CopyTo(directory, FixtureFiles.Exp1, "online_sav3");
        FixtureFiles.CopyTo(directory, FixtureFiles.Options, "options");

        FixtureFiles.CopyTo(directory, FixtureFiles.Sav2, "sav - Copy");
        FixtureFiles.CopyTo(directory, FixtureFiles.Sav2, "sav - Copy (2)");
        FixtureFiles.CopyTo(directory, FixtureFiles.Sav2, "sav.bak");

        directory.WriteText("localoptions.txt", "fullscreen<optB>true");
        directory.WriteText("steam_autocloud.vdf", "\"RootPaths\"\n{\n}\n");
        directory.WriteText(@"cloud\steam_autocloud.vdf", "\"RootPaths\"\n{\n}\n");
        directory.WriteText(@"ModConfigs\steam_autocloud.vdf", "\"RootPaths\"\n{\n}\n");
        directory.WriteText(@"SJ_0\steam_autocloud.vdf", "\"RootPaths\"\n{\n}\n");

        directory.WriteBytes(@"SJ_0\karcap1.png", PngBytes());
        directory.WriteBytes(@"SJ_1\karcap1.png", PngBytes());
        directory.WriteBytes(@"SJ_2\karcap1.png", PngBytes());

        directory.WriteText(@"ModConfigs\devourment.txt", "PredatorMode<optB>true<optA>Difficulty<optB>2");
        directory.WriteText(@"ModConfigs\habbit.karmacontrol.txt", "KarmaControl<optB>1");
        directory.WriteText(@"ModConfigs\henpemaz_rainmeadow.txt", "displayNames<optB>false");
        directory.WriteText(@"ModConfigs\MapOptions.txt", "MapOptions<optB>1");
        directory.WriteText(@"ModConfigs\moreslugcats.txt", "SomeOtherMod<optB>1");
        directory.WriteText(@"ModConfigs\randombuff.txt", "RandomBuff<optB>1");
        directory.WriteText(@"ModConfigs\SBCameraScroll.txt", "SBCameraScroll<optB>1");
        directory.WriteText(@"ModConfigs\willowwisp.bellyplus.txt", "BellyPlus<optB>1");
        directory.WriteText(@"ModConfigs\DvrmentConfs\current.json", "{\"preset\":\"default\",\"struggle\":0.5}");
        directory.WriteText(@"ModConfigs\DvrmentConfs\preset_hungry.json", "{\"preset\":\"hungry\"}");

        directory.WriteText(@"dvrmentSaveStates\contents_0_White_story.txt", "White|Slugcat|0|stomach");
        directory.WriteText(@"dvrmentSaveStates\contents_1_White_story.txt", "White|Slugcat|1|stomach");
        directory.WriteBytes(@"dvrmentSaveStates\contents_2_White_story.txt", Array.Empty<byte>());

        directory.WriteText(@"dressmyslugcat\customization.dat", "{\"slugcats\":[]}");
        directory.WriteText(@"RandomBuff\BuffPluginVersion.txt", "1.4.2");
        directory.WriteText(@"RandomBuff\EnableBuffPlugins.txt", "true");
        directory.WriteText(@"Warp\Colors.txt", "0,0,0");
        directory.WriteText(@"Warp\Settings.txt", "warp<optB>1");

        // Present on the real install and empty there too.
        directory.CreateSubdirectory(@"Warp\Export");

        FixtureFiles.CopyTo(directory, FixtureFiles.Sav2, @"backup\2026-08-24_120000\sav");
        directory.WriteText(@"backup\2026-08-24_120000\manifest.json", "{}");

        AddTheWidenedFiles(directory);
    }

    /// <summary>
    /// Writes the files the widened rules brought into scope. Called by Populate, and again after
    /// a narrow-scope snapshot to test that a restore of it leaves them alone.
    /// </summary>
    public static void AddTheWidenedFiles(TempDirectory directory)
    {
        directory.WriteText("meadow.json", MeadowJson.Live);
        directory.WriteText("buffMain100", "buffMain<bfA>1");
        directory.WriteText("buffsave100", "buffsave<bfA>100");
        directory.WriteText("buffsave101", "buffsave<bfA>101");
        directory.WriteText("buffsave102", "buffsave<bfA>102");
    }

    /// <summary>An eight byte PNG signature plus a little filler. Nothing reads these.</summary>
    private static byte[] PngBytes()
        => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D };
}
