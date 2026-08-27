using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Editing;
using RainWorldCompanion.Core.Library;
using RainWorldCompanion.Core.Mods;
using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Tests;

/// <summary>
/// A library entry keeping the mod settings that were in the save folder when its bytes were taken.
/// SaveTree.Populate lays down the same ModConfigs tree every world gets, so what travels here is
/// what ModConfigs.Travelling names.
/// </summary>
public class ModConfigLibraryTests
{
    private static readonly SaveSlotRef Slot1 = new(SaveRealm.Local, 1);

    /// <summary>What SaveTree.Populate lays down that travels.</summary>
    private static readonly string[] FromTheSaveTree =
    {
        @"ModConfigs\DvrmentConfs\current.json",
        @"ModConfigs\devourment.txt",
        @"ModConfigs\moreslugcats.txt",
    };

    private static string[] Recorded(ModConfigSet set)
        => SaveTree.Sorted(set.Files.Select(file => file.RelativePath));

    // ---- storing ----

    [Fact]
    public void A_stored_slot_keeps_the_mod_settings_that_were_beside_it()
    {
        using var world = new LibraryWorld();

        LibraryEntry entry = world.Library.StoreSlot(Slot1, "a save", null);

        ModConfigSet configs = entry.Manifest!.Configs!;
        Assert.True(configs.ReadTheFolder);
        Assert.Equal(SaveTree.Sorted(FromTheSaveTree), Recorded(configs));
        Assert.True(entry.HasConfigs);
    }

    [Fact]
    public void The_files_land_under_the_entry_laid_out_the_way_they_sit_under_ModConfigs()
    {
        using var world = new LibraryWorld();

        LibraryEntry entry = world.Library.StoreSlot(Slot1, "a save", null);

        Assert.True(File.Exists(Path.Combine(entry.ConfigsPath, "devourment.txt")));
        Assert.True(File.Exists(Path.Combine(entry.ConfigsPath, "DvrmentConfs", "current.json")));
    }

    /// <summary>Full precision floats come off slider drags, so a settings file has to round trip
    /// byte for byte rather than nearly.</summary>
    [Fact]
    public void A_kept_settings_file_is_the_same_bytes_and_the_same_hash()
    {
        using var world = new LibraryWorld();
        world.Live.WriteText(@"ModConfigs\devourment.txt", ModConfigs.SampleConfig);

        LibraryEntry entry = world.Library.StoreSlot(Slot1, "a save", null);

        string kept = Path.Combine(entry.ConfigsPath, "devourment.txt");
        Assert.Equal(File.ReadAllBytes(world.Live.Resolve(@"ModConfigs\devourment.txt")), File.ReadAllBytes(kept));

        ModConfigFile recorded = entry.Manifest!.Configs!.Files
            .Single(file => file.RelativePath == @"ModConfigs\devourment.txt");
        Assert.Equal(Hashing.ComputeFileSha256(kept), recorded.Sha256);
        Assert.Equal(new FileInfo(kept).Length, recorded.SizeBytes);
    }

    /// <summary>The id the game builds the file name from, which is the id the mod list records.</summary>
    [Fact]
    public void Each_kept_file_names_the_mod_it_belongs_to()
    {
        using var world = new LibraryWorld();

        ModConfigSet configs = world.Library.StoreSlot(Slot1, "a save", null).Manifest!.Configs!;

        Assert.Equal(
            new[] { "devourment", "moreslugcats" },
            configs.ByMod().Select(group => group.ModId));
        Assert.Equal(2, configs.ByMod().Single(g => g.ModId == "devourment").Files.Count);
    }

    /// <summary>The scope backs these up because they are the player's own folder. They do not go
    /// into an entry, because an entry is a thing somebody hands to somebody else.</summary>
    [Fact]
    public void What_does_not_travel_is_not_kept()
    {
        using var world = new LibraryWorld();

        LibraryEntry entry = world.Library.StoreSlot(Slot1, "a save", null);

        Assert.False(File.Exists(Path.Combine(entry.ConfigsPath, "steam_autocloud.vdf")));
        Assert.False(Directory.Exists(Path.Combine(entry.ConfigsPath, "MapOptions")));
        Assert.False(File.Exists(Path.Combine(entry.ConfigsPath, "willowwisp.bellyplus.json")));
    }

    [Fact]
    public void A_save_folder_with_no_mod_settings_records_that_it_looked_and_found_none()
    {
        using var world = new LibraryWorld();
        Directory.Delete(world.Live.Resolve("ModConfigs"), recursive: true);

        ModConfigSet configs = world.Library.StoreSlot(Slot1, "a save", null).Manifest!.Configs!;

        Assert.True(configs.ReadTheFolder);
        Assert.Empty(configs.Files);
    }

    [Fact]
    public void A_stored_campaign_keeps_them_too()
    {
        using var world = new LibraryWorld();

        LibraryEntry entry = world.Library.StoreCampaign(Slot1, "White", "a campaign", null);

        Assert.Equal(SaveTree.Sorted(FromTheSaveTree), Recorded(entry.Manifest!.Configs!));
    }

    /// <summary>
    /// A campaign taken out of a backup carries that backup's settings. A backup folder holds a
    /// faithful copy of ModConfigs, so the same reader works on it with no special case.
    /// </summary>
    [Fact]
    public void A_campaign_taken_from_elsewhere_keeps_the_settings_of_wherever_it_came_from()
    {
        using var world = new LibraryWorld();
        using var backup = new TempDirectory("a-backup");
        backup.WriteText(@"ModConfigs\devourment.txt", "DvrmentPredatorMode = false\n");

        CampaignSlice slice = CampaignFile.ReadFrom(world.Live.Resolve("sav"), "White")!;
        LibraryEntry entry = world.Library.StoreCampaignFrom(
            slice, "backup sav", SaveRealm.Local, 1, "from a backup", null, mods: null, configsRoot: backup.Path);

        Assert.Equal(@"ModConfigs\devourment.txt", Assert.Single(entry.Manifest!.Configs!.Files).RelativePath);
        Assert.Equal(
            "DvrmentPredatorMode = false\n",
            File.ReadAllText(Path.Combine(entry.ConfigsPath, "devourment.txt")));
    }

    [Fact]
    public void A_campaign_kept_with_no_folder_to_read_records_nothing_at_all()
    {
        using var world = new LibraryWorld();
        CampaignSlice slice = CampaignFile.ReadFrom(world.Live.Resolve("sav"), "White")!;

        LibraryEntry entry = world.Library.StoreCampaignFrom(
            slice, "sav", SaveRealm.Local, 1, "no settings", null);

        Assert.Null(entry.Manifest!.Configs);
        Assert.False(entry.HasConfigs);
    }

    /// <summary>Storing is a read. The whole point of the library is that it never edits the game's
    /// own files, and reading mod settings is no exception.</summary>
    [Fact]
    public void Keeping_the_settings_never_touches_the_save_folder()
    {
        using var world = new LibraryWorld();
        var before = world.Live.ReadTree();

        world.Library.StoreSlot(Slot1, "a save", null);

        SnapshotLayout.AssertTreeUnchanged(before, world.Live.ReadTree());
    }

    // ---- updating and undoing ----

    /// <summary>An update replaces bytes and settings together and keeps the old pair, so undo puts
    /// both back. Settings that outlived the save they belonged to would be worse than none.</summary>
    [Fact]
    public void An_update_keeps_the_earlier_settings_and_an_undo_puts_them_back()
    {
        using var world = new LibraryWorld();
        world.Live.WriteText(@"ModConfigs\devourment.txt", "DvrmentPredatorMode = false\n");

        LibraryEntry entry = world.Library.StoreSlot(Slot1, "a save", null);
        world.PlayASlot(Slot1, "CYCLENUM", "40");
        world.Live.WriteText(@"ModConfigs\devourment.txt", "DvrmentPredatorMode = true\n");

        LibraryEntry updated = world.Library.UpdateEntry(entry, Slot1);

        Assert.Contains("true", File.ReadAllText(Path.Combine(updated.ConfigsPath, "devourment.txt")));
        Assert.Contains("false", File.ReadAllText(Path.Combine(updated.PreviousConfigsPath, "devourment.txt")));
        Assert.NotNull(updated.Manifest!.PreviousConfigs);

        LibraryEntry undone = world.Library.UndoUpdate(updated);

        Assert.Contains("false", File.ReadAllText(Path.Combine(undone.ConfigsPath, "devourment.txt")));
        Assert.Null(undone.Manifest!.PreviousConfigs);
        Assert.False(Directory.Exists(undone.PreviousConfigsPath));
    }

    /// <summary>One generation, following save.previous.bin exactly.</summary>
    [Fact]
    public void A_second_update_replaces_the_one_generation_that_is_kept()
    {
        using var world = new LibraryWorld();
        world.Live.WriteText(@"ModConfigs\devourment.txt", "generation = 1\n");
        LibraryEntry entry = world.Library.StoreSlot(Slot1, "a save", null);

        world.PlayASlot(Slot1, "CYCLENUM", "40");
        world.Live.WriteText(@"ModConfigs\devourment.txt", "generation = 2\n");
        entry = world.Library.UpdateEntry(entry, Slot1);

        world.PlayASlot(Slot1, "CYCLENUM", "41");
        world.Live.WriteText(@"ModConfigs\devourment.txt", "generation = 3\n");
        entry = world.Library.UpdateEntry(entry, Slot1);

        Assert.Contains("generation = 3", File.ReadAllText(Path.Combine(entry.ConfigsPath, "devourment.txt")));
        Assert.Contains("generation = 2", File.ReadAllText(Path.Combine(entry.PreviousConfigsPath, "devourment.txt")));
    }

    /// <summary>A settings file the newer generation does not have must not survive the update as a
    /// stray in the entry's configs folder.</summary>
    [Fact]
    public void An_update_does_not_leave_a_settings_file_behind_that_the_new_one_lacks()
    {
        using var world = new LibraryWorld();
        LibraryEntry entry = world.Library.StoreSlot(Slot1, "a save", null);

        world.PlayASlot(Slot1, "CYCLENUM", "40");
        File.Delete(world.Live.Resolve(@"ModConfigs\moreslugcats.txt"));

        LibraryEntry updated = world.Library.UpdateEntry(entry, Slot1);

        Assert.False(File.Exists(Path.Combine(updated.ConfigsPath, "moreslugcats.txt")));
        Assert.DoesNotContain(
            @"ModConfigs\moreslugcats.txt",
            updated.Manifest!.Configs!.Files.Select(file => file.RelativePath));
    }

    [Fact]
    public void Updating_a_campaign_keeps_the_settings_the_same_way()
    {
        using var world = new LibraryWorld();
        world.Live.WriteText(@"ModConfigs\devourment.txt", "generation = 1\n");
        LibraryEntry entry = world.Library.StoreCampaign(Slot1, "White", "a campaign", null);

        world.PlayASlot(Slot1, "CYCLENUM", "40");
        world.Live.WriteText(@"ModConfigs\devourment.txt", "generation = 2\n");

        LibraryEntry updated = world.Library.UpdateEntry(entry, Slot1);

        Assert.Contains("generation = 2", File.ReadAllText(Path.Combine(updated.ConfigsPath, "devourment.txt")));
        Assert.Contains("generation = 1", File.ReadAllText(Path.Combine(updated.PreviousConfigsPath, "devourment.txt")));
    }

    // ---- importing ----

    [Fact]
    public void A_bare_save_file_imports_with_nothing_recorded()
    {
        using var world = new LibraryWorld();
        string file = Path.Combine(world.LibraryRoot.Path, "loose_sav");
        File.Copy(world.Live.Resolve("sav"), file);

        Assert.Null(world.Library.ImportFile(file).Entry!.Manifest!.Configs);
    }

    // ---- what settings never cost ----

    /// <summary>
    /// VerifyEntry hashes the save alone, and its failure refuses a load. A settings file that
    /// rotted must not take the save down with it: that is checked where it is written instead.
    /// </summary>
    [Fact]
    public void A_settings_file_that_no_longer_matches_its_hash_does_not_fail_the_entry()
    {
        using var world = new LibraryWorld();
        LibraryEntry entry = world.Library.StoreSlot(Slot1, "a save", null);

        File.WriteAllText(Path.Combine(entry.ConfigsPath, "devourment.txt"), "someone edited this");

        Assert.True(world.Library.VerifyEntry(world.Reload(entry)).Ok);
        Assert.True(world.Library.PlanAnyLoad(world.Reload(entry), Slot1).CanLoad);
    }

    /// <summary>A settings file that cannot be copied is left out of the record and named, rather
    /// than costing the save it came with.</summary>
    [Fact]
    public void A_settings_file_that_will_not_copy_is_named_and_the_save_is_still_stored()
    {
        using var world = new LibraryWorld();
        string locked = world.Live.Resolve(@"ModConfigs\devourment.txt");

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            LibraryEntry entry = world.Library.StoreSlot(Slot1, "a save", null);

            Assert.True(entry.IsComplete);
            Assert.DoesNotContain(
                @"ModConfigs\devourment.txt",
                entry.Manifest!.Configs!.Files.Select(file => file.RelativePath));
            Assert.Contains("devourment.txt", entry.Manifest.Configs.Note);
        }
    }

    [Fact]
    public void Deleting_an_entry_takes_its_settings_with_it()
    {
        using var world = new LibraryWorld();
        LibraryEntry entry = world.Library.StoreSlot(Slot1, "a save", null);

        world.Library.DeleteEntry(entry);

        Assert.False(Directory.Exists(entry.ConfigsPath));
    }
}
