using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Editing;
using RainWorldCompanion.Core.Library;
using RainWorldCompanion.Core.Mods;
using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Tests;

/// <summary>
/// A campaign carrying mod settings, which goes through the splicing writer rather than the whole
/// slot copy. Both end at the same rung, so what is proved here is the threading rather than the
/// writing.
/// </summary>
public class ModConfigCampaignTests
{
    private static readonly SaveSlotRef LocalOne = new(SaveRealm.Local, 1);
    private static readonly SaveSlotRef LocalTwo = new(SaveRealm.Local, 2);

    private const string Devourment = "devourment";
    private const string LivePath = @"ModConfigs\devourment.txt";

    private static LibraryEntry StoredWith(LibraryWorld world, string theirs, string yours)
    {
        SeedTheTarget(world);
        world.Live.WriteText(LivePath, theirs);
        LibraryEntry entry = world.Library.StoreCampaign(LocalOne, "White", "their campaign", null);
        world.Live.WriteText(LivePath, yours);
        return entry;
    }

    private static string LiveText(LibraryWorld world) => File.ReadAllText(world.Live.Resolve(LivePath));

    /// <summary>
    /// Every fixture slot holds the same White campaign, so splicing one into another would be a
    /// write with nothing to write. The target gets a White at a different cycle instead.
    /// </summary>
    private static void SeedTheTarget(LibraryWorld world) => world.Seed("sav2", "White", cycle: 99);

    // ---- loading a campaign entry ----

    [Fact]
    public void Loading_a_campaign_with_nothing_ticked_leaves_every_settings_file_alone()
    {
        using var world = new LibraryWorld();
        LibraryEntry entry = StoredWith(world, "theirs = 1\n", "yours = 1\n");

        LibraryLoadResult result = world.Library.LoadAny(entry, LocalTwo);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal(0, result.SettingsWritten);
        Assert.Equal("yours = 1\n", LiveText(world));
    }

    [Fact]
    public void Ticking_a_mod_writes_its_settings_beside_the_spliced_campaign()
    {
        using var world = new LibraryWorld();
        LibraryEntry entry = StoredWith(world, "theirs = 1\n", "yours = 1\n");

        LibraryLoadResult result = world.Library.LoadAny(entry, LocalTwo, new[] { Devourment });

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal("theirs = 1\n", LiveText(world));
    }

    /// <summary>The campaign write already takes one, and the settings ride on it rather than on a
    /// second copy that could disagree about what was there.</summary>
    [Fact]
    public void One_safety_copy_holds_the_slot_and_the_settings_written_beside_it()
    {
        using var world = new LibraryWorld();
        LibraryEntry entry = StoredWith(world, "theirs = 1\n", "yours = 1\n");

        LibraryLoadResult result = world.Library.LoadAny(entry, LocalTwo, new[] { Devourment });

        BackupSnapshot safety = Assert.Single(world.Backups.ListBackups());
        Assert.Equal(safety.Id, result.SafetySnapshot!.Id);

        var held = SaveTree.Sorted(safety.Manifest!.Files.Select(file => file.RelativePath!));
        Assert.Contains("sav2", held);
        Assert.Contains(LivePath, held);
    }

    [Fact]
    public void Restoring_the_safety_copy_puts_the_old_settings_back()
    {
        using var world = new LibraryWorld();
        LibraryEntry entry = StoredWith(world, "theirs = 1\n", "yours = 1\n");
        var before = world.Live.ReadTree();

        LibraryLoadResult loaded = world.Library.LoadAny(entry, LocalTwo, new[] { Devourment });
        RestoreResult restored = world.Backups.RestoreBackup(loaded.SafetySnapshot!);

        Assert.True(restored.Success, string.Join("; ", restored.Errors));
        SnapshotLayout.AssertTreeUnchanged(before, world.Live.ReadTree());
    }

    [Fact]
    public void A_campaign_load_plan_offers_the_settings_the_entry_carries()
    {
        using var world = new LibraryWorld();
        LibraryEntry entry = world.Library.StoreCampaign(LocalOne, "White", "their campaign", null);

        ModConfigOffer offer = world.Library.PlanAnyLoad(entry, LocalTwo).Settings!;

        Assert.Contains(Devourment, offer.ByMod().Select(group => group.ModId));
    }

    // ---- sending a campaign out of a backup ----

    /// <summary>
    /// A snapshot is a faithful copy of the save folder, so its ModConfigs sits at the path the save
    /// folder had and the same rule decides which of it travels.
    /// </summary>
    [Fact]
    public void A_backup_offers_the_settings_it_took()
    {
        using var world = new LibraryWorld();
        BackupSnapshot snapshot = world.Backups.CreateBackup("before", null);

        ModConfigOffer offer = world.Backups.SettingsFor(snapshot)!;

        Assert.Equal(
            new[] { Devourment, "moreslugcats" },
            offer.ByMod().Select(group => group.ModId));
    }

    /// <summary>A backup takes everything under ModConfigs, which is wider than what travels.</summary>
    [Fact]
    public void A_backup_offers_only_what_travels_not_everything_it_took()
    {
        using var world = new LibraryWorld();
        BackupSnapshot snapshot = world.Backups.CreateBackup("before", null);

        var offered = SaveTree.Sorted(
            world.Backups.SettingsFor(snapshot)!.Recorded.Files.Select(file => file.RelativePath));

        Assert.DoesNotContain(@"ModConfigs\MapOptions\cache.json", offered);
        Assert.DoesNotContain(@"ModConfigs\willowwisp.bellyplus.json", offered);
        Assert.Contains(@"ModConfigs\devourment.txt", offered);
    }

    [Fact]
    public void Nothing_ticked_takes_nothing_out_of_a_backup()
    {
        using var world = new LibraryWorld();
        BackupSnapshot snapshot = world.Backups.CreateBackup("before", null);

        Assert.Empty(world.Backups.SettingsToWrite(snapshot, Array.Empty<string>()));
    }

    [Fact]
    public void Ticking_a_mod_names_that_mods_files_inside_the_snapshot()
    {
        using var world = new LibraryWorld();
        BackupSnapshot snapshot = world.Backups.CreateBackup("before", null);

        var extras = world.Backups.SettingsToWrite(snapshot, new[] { Devourment });

        Assert.Equal(2, extras.Count);
        Assert.All(extras, extra => Assert.True(File.Exists(extra.SourcePath)));
        Assert.All(extras, extra => Assert.StartsWith(snapshot.DirectoryPath, extra.SourcePath));
    }

    /// <summary>
    /// A settings file goes over the live one and is undone by the same safety copy the campaign
    /// write takes, which is the whole reason the two travel together.
    /// </summary>
    [Fact]
    public void Settings_out_of_a_backup_are_written_and_can_be_restored_away()
    {
        using var world = new LibraryWorld();
        SeedTheTarget(world);
        world.Live.WriteText(LivePath, "theirs = 1\n");
        BackupSnapshot snapshot = world.Backups.CreateBackup("before", null);
        world.Live.WriteText(LivePath, "yours = 1\n");

        var before = world.Live.ReadTree();
        var move = world.Backups.SlotWriter.PlanPutCampaign(
            LocalTwo,
            CampaignFile.ReadFrom(world.Live.Resolve("sav"), "White")!);

        SaveWriteResult result = world.Backups.SlotWriter.Write(
            move,
            progress: null,
            ct: CancellationToken.None,
            extras: world.Backups.SettingsToWrite(snapshot, new[] { Devourment }));

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal("theirs = 1\n", LiveText(world));

        world.Backups.RestoreBackup(result.SafetySnapshot!);
        SnapshotLayout.AssertTreeUnchanged(before, world.Live.ReadTree());
    }

    [Fact]
    public void A_backup_with_no_settings_in_it_offers_nothing()
    {
        using var world = new LibraryWorld();
        Directory.Delete(world.Live.Resolve("ModConfigs"), recursive: true);

        Assert.Null(world.Backups.SettingsFor(world.Backups.CreateBackup("before", null)));
    }
}
