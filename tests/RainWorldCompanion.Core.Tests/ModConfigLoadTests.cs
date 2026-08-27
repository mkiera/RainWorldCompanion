using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Library;
using RainWorldCompanion.Core.Mods;
using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Tests;

/// <summary>
/// Writing somebody else's mod settings into the live save folder. Nothing here happens unless it
/// was asked for by mod id, and everything that does happen is undone by the one safety copy the
/// load already takes.
/// </summary>
public class ModConfigLoadTests
{
    private static readonly SaveSlotRef LocalTwo = new(SaveRealm.Local, 2);
    private static readonly SaveSlotRef LocalThree = new(SaveRealm.Local, 3);

    private const string Devourment = "devourment";
    private const string LivePath = @"ModConfigs\devourment.txt";

    /// <summary>
    /// A world holding an entry whose settings differ from what is in the save folder now, which is
    /// what a save arriving from somebody else looks like.
    /// </summary>
    private static LibraryEntry StoredWith(LibraryWorld world, string theirs, string yours)
    {
        world.Live.WriteText(LivePath, theirs);
        LibraryEntry entry = world.Library.StoreSlot(LocalTwo, "their save", null);
        world.Live.WriteText(LivePath, yours);
        return entry;
    }

    private static string LiveText(LibraryWorld world) => File.ReadAllText(world.Live.Resolve(LivePath));

    // ---- nothing happens unless it is asked for ----

    /// <summary>
    /// The decision behind the whole feature: somebody else's settings are not what a player asked
    /// for by asking to load a save. This must never quietly change.
    /// </summary>
    [Fact]
    public void Loading_a_save_with_nothing_ticked_leaves_every_settings_file_alone()
    {
        using var world = new LibraryWorld();
        LibraryEntry entry = StoredWith(world, "theirs = 1\n", "yours = 1\n");
        var before = world.Live.ReadTree();

        LibraryLoadResult result = world.Library.LoadEntry(entry, LocalThree);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal(0, result.SettingsWritten);

        // Only the slot that was loaded into moved.
        before["sav3"] = world.Live.ReadBytes("sav3");
        SnapshotLayout.AssertTreeUnchanged(before, world.Live.ReadTree());
    }

    [Fact]
    public void The_plain_overload_takes_no_settings_at_all()
    {
        using var world = new LibraryWorld();
        LibraryEntry entry = StoredWith(world, "theirs = 1\n", "yours = 1\n");

        world.Library.LoadEntry(entry, LocalThree, progress: null);

        Assert.Equal("yours = 1\n", LiveText(world));
    }

    [Fact]
    public void Ticking_a_mod_writes_that_mods_settings()
    {
        using var world = new LibraryWorld();
        LibraryEntry entry = StoredWith(world, "theirs = 1\n", "yours = 1\n");

        LibraryLoadResult result = world.Library.LoadEntry(entry, LocalThree, new[] { Devourment });

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal("theirs = 1\n", LiveText(world));
    }

    /// <summary>Take their Devourment tuning without their camera settings.</summary>
    [Fact]
    public void Ticking_one_mod_leaves_every_other_mods_settings_alone()
    {
        using var world = new LibraryWorld();
        world.Live.WriteText(LivePath, "theirs = 1\n");
        world.Live.WriteText(@"ModConfigs\moreslugcats.txt", "theirs = 1\n");
        LibraryEntry entry = world.Library.StoreSlot(LocalTwo, "their save", null);

        world.Live.WriteText(LivePath, "yours = 1\n");
        world.Live.WriteText(@"ModConfigs\moreslugcats.txt", "yours = 1\n");

        world.Library.LoadEntry(entry, LocalThree, new[] { Devourment });

        Assert.Equal("theirs = 1\n", LiveText(world));
        Assert.Equal("yours = 1\n", File.ReadAllText(world.Live.Resolve(@"ModConfigs\moreslugcats.txt")));
    }

    /// <summary>Devourment owns both its settings file and its whole preset folder, and ticking
    /// Devourment means both.</summary>
    [Fact]
    public void Ticking_a_mod_writes_every_file_it_owns()
    {
        using var world = new LibraryWorld();
        world.Live.WriteText(@"ModConfigs\DvrmentConfs\current.json", "{\"theirs\":1}");
        LibraryEntry entry = world.Library.StoreSlot(LocalTwo, "their save", null);
        world.Live.WriteText(@"ModConfigs\DvrmentConfs\current.json", "{\"yours\":1}");

        LibraryLoadResult result = world.Library.LoadEntry(entry, LocalThree, new[] { Devourment });

        Assert.Equal(2, result.SettingsWritten);
        Assert.Equal(
            "{\"theirs\":1}",
            File.ReadAllText(world.Live.Resolve(@"ModConfigs\DvrmentConfs\current.json")));
    }

    [Fact]
    public void A_settings_file_the_save_folder_did_not_have_is_created()
    {
        using var world = new LibraryWorld();
        LibraryEntry entry = world.Library.StoreSlot(LocalTwo, "their save", null);
        File.Delete(world.Live.Resolve(LivePath));

        LibraryLoadResult result = world.Library.LoadEntry(entry, LocalThree, new[] { Devourment });

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.True(File.Exists(world.Live.Resolve(LivePath)));
    }

    /// <summary>A mod the entry carries no settings for takes nothing, rather than taking something
    /// near it.</summary>
    [Fact]
    public void Ticking_a_mod_the_save_carries_nothing_for_writes_nothing()
    {
        using var world = new LibraryWorld();
        LibraryEntry entry = StoredWith(world, "theirs = 1\n", "yours = 1\n");

        LibraryLoadResult result = world.Library.LoadEntry(entry, LocalThree, new[] { "a-mod-nobody-has" });

        Assert.Equal(0, result.SettingsWritten);
        Assert.Equal("yours = 1\n", LiveText(world));
    }

    // ---- one safety copy covers all of it ----

    /// <summary>
    /// The load already takes one safety copy, and the settings ride on it. A second copy, or a
    /// settings file outside the one that was taken, would be a write that could not be undone.
    /// </summary>
    [Fact]
    public void One_safety_copy_holds_the_save_and_every_settings_file_written()
    {
        using var world = new LibraryWorld();
        LibraryEntry entry = StoredWith(world, "theirs = 1\n", "yours = 1\n");

        LibraryLoadResult result = world.Library.LoadEntry(entry, LocalThree, new[] { Devourment });

        BackupSnapshot safety = Assert.Single(world.Backups.ListBackups());
        Assert.Equal(safety.Id, result.SafetySnapshot!.Id);

        var held = SaveTree.Sorted(safety.Manifest!.Files.Select(file => file.RelativePath!));
        Assert.Contains("sav3", held);
        Assert.Contains(LivePath, held);
    }

    [Fact]
    public void Restoring_the_safety_copy_puts_the_old_settings_back()
    {
        using var world = new LibraryWorld();
        LibraryEntry entry = StoredWith(world, "theirs = 1\n", "yours = 1\n");
        var before = world.Live.ReadTree();

        LibraryLoadResult loaded = world.Library.LoadEntry(entry, LocalThree, new[] { Devourment });
        RestoreResult restored = world.Backups.RestoreBackup(loaded.SafetySnapshot!);

        Assert.True(restored.Success, string.Join("; ", restored.Errors));
        SnapshotLayout.AssertTreeUnchanged(before, world.Live.ReadTree());
    }

    /// <summary>A settings file the load created was not in the safety copy, so restoring it takes
    /// the file away again rather than leaving it behind.</summary>
    [Fact]
    public void Restoring_the_safety_copy_removes_a_settings_file_the_load_created()
    {
        using var world = new LibraryWorld();
        LibraryEntry entry = world.Library.StoreSlot(LocalTwo, "their save", null);
        File.Delete(world.Live.Resolve(LivePath));
        var before = world.Live.ReadTree();

        LibraryLoadResult loaded = world.Library.LoadEntry(entry, LocalThree, new[] { Devourment });
        Assert.True(File.Exists(world.Live.Resolve(LivePath)));

        world.Backups.RestoreBackup(loaded.SafetySnapshot!);

        Assert.False(File.Exists(world.Live.Resolve(LivePath)));
        SnapshotLayout.AssertTreeUnchanged(before, world.Live.ReadTree());
    }

    // ---- what a settings file never costs ----

    /// <summary>
    /// A settings file that will not write is a warning, never an error. Reporting it as a failure
    /// would say the save did not arrive when it did. What one file's rot costs is that one file:
    /// the rest of the same mod's settings still land.
    /// </summary>
    [Fact]
    public void A_settings_file_that_no_longer_matches_its_checksum_is_skipped_and_the_save_lands()
    {
        using var world = new LibraryWorld();
        world.Live.WriteText(@"ModConfigs\DvrmentConfs\current.json", "{\"theirs\":1}");
        LibraryEntry entry = StoredWith(world, "theirs = 1\n", "yours = 1\n");
        world.Live.WriteText(@"ModConfigs\DvrmentConfs\current.json", "{\"yours\":1}");

        File.WriteAllText(Path.Combine(entry.ConfigsPath, "devourment.txt"), "somebody edited this");

        LibraryLoadResult result = world.Library.LoadEntry(
            world.Reload(entry), LocalThree, new[] { Devourment });

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Empty(result.Errors);
        Assert.Contains(result.Warnings, warning => warning.Contains("devourment.txt"));

        Assert.Equal("yours = 1\n", LiveText(world));
        Assert.Equal(
            "{\"theirs\":1}",
            File.ReadAllText(world.Live.Resolve(@"ModConfigs\DvrmentConfs\current.json")));
    }

    [Fact]
    public void A_settings_file_that_went_missing_from_the_entry_is_skipped_and_named()
    {
        using var world = new LibraryWorld();
        LibraryEntry entry = StoredWith(world, "theirs = 1\n", "yours = 1\n");
        File.Delete(Path.Combine(entry.ConfigsPath, "devourment.txt"));

        LibraryLoadResult result = world.Library.LoadEntry(
            world.Reload(entry), LocalThree, new[] { Devourment });

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Contains(result.Warnings, warning => warning.Contains("devourment.txt"));
    }

    /// <summary>
    /// A recorded path outside the scope has no entry in the safety copy, so writing it could not be
    /// undone. It is skipped rather than written and hoped for.
    /// </summary>
    [Fact]
    public void A_recorded_path_outside_what_this_app_manages_is_never_written()
    {
        using var world = new LibraryWorld();
        LibraryEntry entry = StoredWith(world, "theirs = 1\n", "yours = 1\n");

        entry.Manifest!.Configs!.Files.Add(new ModConfigFile
        {
            RelativePath = "options",
            ModId = Devourment,
            Sha256 = entry.Manifest.Configs.Files[0].Sha256,
        });

        var optionsBefore = world.Live.ReadBytes("options");

        LibraryLoadResult result = world.Library.LoadEntry(entry, LocalThree, new[] { Devourment });

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal(optionsBefore, world.Live.ReadBytes("options"));
    }

    /// <summary>
    /// Resolving a path is textual, and text cannot see a junction. A copy onto one writes through
    /// it, over a file the safety copy never took.
    /// </summary>
    [JunctionFact]
    public void A_settings_folder_that_is_a_link_is_never_written_through()
    {
        using var world = new LibraryWorld();
        using var elsewhere = new TempDirectory("elsewhere");
        elsewhere.WriteText("current.json", "{\"not\":\"the players\"}");

        LibraryEntry entry = world.Library.StoreSlot(LocalTwo, "their save", null);

        Directory.Delete(world.Live.Resolve(@"ModConfigs\DvrmentConfs"), recursive: true);
        Links.TryCreateDirectoryJunction(world.Live.Resolve(@"ModConfigs\DvrmentConfs"), elsewhere.Path);

        LibraryLoadResult result = world.Library.LoadEntry(entry, LocalThree, new[] { Devourment });

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal("{\"not\":\"the players\"}", File.ReadAllText(elsewhere.Resolve("current.json")));
        Assert.Contains(result.Warnings, warning => warning.Contains("DvrmentConfs"));
    }

    [Fact]
    public void A_read_only_settings_file_is_still_written_over()
    {
        using var world = new LibraryWorld();
        LibraryEntry entry = StoredWith(world, "theirs = 1\n", "yours = 1\n");
        File.SetAttributes(world.Live.Resolve(LivePath), FileAttributes.ReadOnly);

        try
        {
            LibraryLoadResult result = world.Library.LoadEntry(entry, LocalThree, new[] { Devourment });

            Assert.Empty(result.Warnings);
            Assert.Equal("theirs = 1\n", LiveText(world));
        }
        finally
        {
            File.SetAttributes(world.Live.Resolve(LivePath), FileAttributes.Normal);
        }
    }

    /// <summary>Settings are written only once the save itself has landed and been proved.</summary>
    [Fact]
    public void A_load_that_refuses_writes_no_settings_either()
    {
        using var world = new LibraryWorld();
        LibraryEntry entry = StoredWith(world, "theirs = 1\n", "yours = 1\n");
        world.Detector.RunningProcessName = "RainWorld";

        Assert.Throws<GameRunningException>(
            () => world.Library.LoadEntry(entry, LocalThree, new[] { Devourment }));

        Assert.Equal("yours = 1\n", LiveText(world));
    }

    // ---- what the plan offers ----

    [Fact]
    public void A_plan_offers_the_settings_the_entry_carries_grouped_by_mod()
    {
        using var world = new LibraryWorld();
        LibraryEntry entry = world.Library.StoreSlot(LocalTwo, "their save", null);

        ModConfigOffer offer = world.Library.PlanAnyLoad(entry, LocalThree).Settings!;

        Assert.Equal(new[] { Devourment, "moreslugcats" }, offer.ByMod().Select(group => group.ModId));
        Assert.Equal(2, offer.ByMod().Single(group => group.ModId == Devourment).Files.Count);
    }

    [Fact]
    public void A_plan_says_what_the_save_folder_holds_now_so_a_replacement_can_be_named()
    {
        using var world = new LibraryWorld();
        LibraryEntry entry = world.Library.StoreSlot(LocalTwo, "their save", null);

        ModConfigOffer offer = world.Library.PlanAnyLoad(entry, LocalThree).Settings!;

        Assert.True(offer.Live!.ReadTheFolder);
        Assert.Contains(LivePath, offer.Live.Files.Select(file => file.RelativePath));
    }

    [Fact]
    public void An_entry_carrying_no_settings_offers_nothing()
    {
        using var world = new LibraryWorld();
        Directory.Delete(world.Live.Resolve("ModConfigs"), recursive: true);
        LibraryEntry entry = world.Library.StoreSlot(LocalTwo, "their save", null);

        Assert.Null(world.Library.PlanAnyLoad(entry, LocalThree).Settings);
    }

    [Fact]
    public void A_plan_carries_the_mods_the_save_was_played_with_so_a_row_can_be_named()
    {
        using var world = new LibraryWorld(
            modListSource: () => ModLists.Current("v1.11.8", ModLists.Mod(Devourment, "0.1.11-ea")));

        LibraryEntry entry = world.Library.StoreSlot(LocalTwo, "their save", null);
        ModConfigOffer offer = world.Library.PlanAnyLoad(entry, LocalThree).Settings!;

        Assert.Equal("0.1.11-ea", offer.RecordedMods!.Mods.Single().Version);
        Assert.NotNull(offer.Current);
    }

    /// <summary>Said in the dialog rather than after the user has agreed, and never as a problem: a
    /// settings file that will not land is no reason to refuse the save.</summary>
    [Fact]
    public void A_recorded_path_that_cannot_be_written_is_a_warning_on_the_plan_not_a_problem()
    {
        using var world = new LibraryWorld();
        LibraryEntry entry = world.Library.StoreSlot(LocalTwo, "their save", null);

        entry.Manifest!.Configs!.Files.Add(new ModConfigFile { RelativePath = "options", ModId = Devourment });

        LibraryLoadPlan plan = world.Library.PlanAnyLoad(entry, LocalThree);

        Assert.True(plan.CanLoad);
        Assert.Empty(plan.Problems);
        Assert.Contains(plan.Warnings, warning => warning.Contains("options"));
    }
}
