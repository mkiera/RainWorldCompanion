using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Mods;

namespace RainWorldCompanion.Tests;

public class ModListRecordingTests
{
    private static CurrentMods TwoMods() => ModLists.Current(
        "v1.11.8",
        ModLists.Mod("devourment", "0.1.11-ea"),
        ModLists.Mod("MapOptions", "2.3.3", workshopId: "2923374705"));

    [Fact]
    public void A_backup_records_the_mods_that_were_on()
    {
        using var world = new BackupWorld(modListSource: TwoMods);

        ModListSnapshot mods = world.Service.CreateBackup("today", null).Manifest!.Mods!;

        Assert.True(mods.ReadTheEnabledList);
        Assert.Equal("v1.11.8", mods.GameVersion);
        Assert.Equal(new[] { "devourment", "MapOptions" }, mods.Mods.Select(mod => mod.Id));
        Assert.Equal("2923374705", mods.Mods[1].WorkshopId);
    }

    /// <summary>Every snapshot goes through one method, so the safety copies record it too.</summary>
    [Fact]
    public void A_safety_snapshot_records_them_as_well()
    {
        using var world = new BackupWorld(modListSource: TwoMods);

        BackupSnapshot snapshot = world.Service.CreateBackup(
            "before something", null, BackupKind.PreRestoreSafety);

        Assert.Equal(2, snapshot.Manifest!.Mods!.Mods.Count);
    }

    [Fact]
    public void The_recorded_list_survives_a_round_trip_through_the_manifest_file()
    {
        using var world = new BackupWorld(modListSource: TwoMods);
        string id = world.Service.CreateBackup("today", null).Id;

        ModListSnapshot mods = world.Service.ListBackups().Single(s => s.Id == id).Manifest!.Mods!;

        Assert.Equal("0.1.11-ea", mods.Mods[0].Version);
        Assert.Equal(ModEntry.WorkshopOrigin, mods.Mods[1].Origin);
    }

    /// <summary>A mod list is something a snapshot carries, not something it depends on to do its job.</summary>
    [Fact]
    public void A_service_with_nowhere_to_read_mods_still_takes_backups()
    {
        using var world = new BackupWorld();

        BackupSnapshot snapshot = world.Service.CreateBackup("today", null);

        Assert.True(snapshot.IsComplete);
        Assert.Null(snapshot.Manifest!.Mods);
    }

    [Fact]
    public void A_mod_read_that_throws_costs_the_list_and_not_the_backup()
    {
        using var world = new BackupWorld(
            modListSource: () => throw new InvalidOperationException("the disk went away"));

        BackupSnapshot snapshot = world.Service.CreateBackup("today", null);

        Assert.True(snapshot.IsComplete);
        Assert.Null(snapshot.Manifest!.Mods);
    }

    /// <summary>
    /// A read that could not see the enabled list still records what it did learn, flagged.
    /// Recording nothing would lose the game version, and an empty list would falsely claim a
    /// vanilla install.
    /// </summary>
    [Fact]
    public void A_read_that_could_not_look_is_recorded_as_such()
    {
        using var world = new BackupWorld(modListSource: ModLists.CouldNotLook);

        ModListSnapshot mods = world.Service.CreateBackup("today", null).Manifest!.Mods!;

        Assert.False(mods.ReadTheEnabledList);
        Assert.Empty(mods.Mods);
        Assert.NotNull(mods.Note);
    }

    [Fact]
    public void A_restore_plan_says_how_the_machine_has_moved_since_the_backup()
    {
        var version = "1.0";
        using var world = new BackupWorld(
            modListSource: () => ModLists.Current("v1.11.8", ModLists.Mod("devourment", version)));

        BackupSnapshot snapshot = world.Service.CreateBackup("today", null);
        version = "2.0";

        ModListDiff mods = world.Service.PlanRestore(snapshot).Mods!;

        ModVersionChange change = Assert.Single(mods.Changed);
        Assert.Equal("1.0", change.Recorded);
        Assert.Equal("2.0", change.Now);
    }

    /// <summary>Compares against nothing, worded as such rather than claiming the machine has emptied out.</summary>
    [Fact]
    public void A_backup_from_before_mod_lists_plans_a_restore_that_says_so()
    {
        using var world = new BackupWorld(modListSource: TwoMods);
        BackupSnapshot snapshot = world.Service.CreateBackup("today", null);
        snapshot.Manifest!.Mods = null;

        ModListDiff mods = world.Service.PlanRestore(snapshot).Mods!;

        Assert.True(mods.NothingWasRecorded);
        Assert.Empty(mods.Missing);
    }

    [Fact]
    public void A_service_with_nowhere_to_read_mods_plans_restores_with_no_comparison()
    {
        using var world = new BackupWorld();

        Assert.Null(world.Service.PlanRestore(world.Service.CreateBackup("today", null)).Mods);
    }

    /// <summary>A mod difference is shown, never enforced, so a restore is offered either way.</summary>
    [Fact]
    public void A_mod_difference_leaves_the_restore_plan_alone()
    {
        using var world = new BackupWorld(modListSource: TwoMods);
        BackupSnapshot snapshot = world.Service.CreateBackup("today", null);

        RestorePlan withMods = world.Service.PlanRestore(snapshot);

        Assert.NotNull(withMods.Mods);
        Assert.Empty(withMods.Deleted);
        Assert.Equal(withMods.Unchanged.Count, snapshot.Manifest!.Files.Count);
    }
}
