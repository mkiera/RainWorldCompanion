using RainWorldCompanion.Core.Mods;

namespace RainWorldCompanion.Tests;

public class ModSyncPlanTests
{
    private static CurrentMods Machine(IEnumerable<ModEntry> on, params ModEntry[] installed)
        => new(ModLists.Snapshot(null, on.ToArray()), installed);

    private static ModSyncRow Row(ModSyncPlan plan, string id)
        => Assert.Single(plan.Rows.Where(row => row.Id == id));

    [Fact]
    public void Every_installed_mod_gets_a_row_whether_it_is_on_or_off()
    {
        ModEntry on = ModLists.Mod("on");
        ModEntry off = ModLists.Mod("off");

        ModSyncPlan plan = ModSyncPlan.Build(null, Machine(new[] { on }, on, off));

        Assert.Equal(new[] { "off", "on" }, plan.Rows.Select(row => row.Id));
        Assert.True(Row(plan, "on").IsOn);
        Assert.False(Row(plan, "off").IsOn);
    }

    [Fact]
    public void With_no_save_to_match_the_ticks_start_where_the_machine_is()
    {
        ModEntry on = ModLists.Mod("on");
        ModEntry off = ModLists.Mod("off");

        ModSyncPlan plan = ModSyncPlan.Build(null, Machine(new[] { on }, on, off));

        Assert.True(Row(plan, "on").Wanted);
        Assert.False(Row(plan, "off").Wanted);
        Assert.True(plan.NothingToDo);
    }

    [Fact]
    public void Turning_an_off_mod_on_by_hand_puts_it_in_the_list_and_in_the_loader()
    {
        ModEntry on = ModLists.Mod("on");
        ModEntry off = ModLists.Mod("off");
        CurrentMods machine = Machine(new[] { on }, on, off);

        ModSyncPlan plan = ModSyncPlan.Build(null, machine);
        Row(plan, "off").Wanted = true;

        Assert.True(Row(plan, "off").TurningOn);
        Assert.False(plan.NothingToDo);

        ModSyncOutcome outcome = plan.Resolve(machine);

        Assert.Equal(new[] { "on", "off" }, outcome.EnabledIds);
        Assert.Equal(new[] { "off" }, outcome.TurnOn.Select(mod => mod.Id));
        Assert.Empty(outcome.TurnOff);
    }

    [Fact]
    public void Turning_an_on_mod_off_by_hand_takes_it_out_of_both()
    {
        ModEntry first = ModLists.Mod("first");
        ModEntry second = ModLists.Mod("second");
        CurrentMods machine = Machine(new[] { first, second }, first, second);

        ModSyncPlan plan = ModSyncPlan.Build(null, machine);
        Row(plan, "first").Wanted = false;

        Assert.True(Row(plan, "first").TurningOff);

        ModSyncOutcome outcome = plan.Resolve(machine);

        Assert.Equal(new[] { "second" }, outcome.EnabledIds);
        Assert.Equal(new[] { "first" }, outcome.TurnOff.Select(mod => mod.Id));
        Assert.Empty(outcome.TurnOn);
    }

    [Fact]
    public void Matching_a_save_ticks_what_that_save_had()
    {
        ModEntry wanted = ModLists.Mod("wanted");
        ModEntry extra = ModLists.Mod("extra");
        CurrentMods machine = Machine(new[] { extra }, wanted, extra);

        ModSyncPlan plan = ModSyncPlan.Build(ModLists.Snapshot(null, wanted), machine);

        Assert.True(Row(plan, "wanted").Wanted);
        Assert.False(Row(plan, "extra").Wanted);
        Assert.False(plan.NothingToDo);

        ModSyncOutcome outcome = plan.Resolve(machine);

        Assert.Equal(new[] { "wanted" }, outcome.EnabledIds);
        Assert.Equal(new[] { "wanted" }, outcome.TurnOn.Select(mod => mod.Id));
        Assert.Equal(new[] { "extra" }, outcome.TurnOff.Select(mod => mod.Id));
    }

    [Fact]
    public void A_mod_left_ticked_on_purpose_stays_on_through_a_match()
    {
        ModEntry wanted = ModLists.Mod("wanted");
        ModEntry cosmetic = ModLists.Mod("cosmetic");
        CurrentMods machine = Machine(new[] { cosmetic }, wanted, cosmetic);

        ModSyncPlan plan = ModSyncPlan.Build(ModLists.Snapshot(null, wanted), machine);
        Row(plan, "cosmetic").Wanted = true;

        ModSyncOutcome outcome = plan.Resolve(machine);

        Assert.Equal(new[] { "cosmetic", "wanted" }, outcome.EnabledIds);
        Assert.Empty(outcome.TurnOff);
    }

    [Fact]
    public void A_mod_the_save_wanted_that_is_nowhere_here_is_listed_and_never_written()
    {
        ModEntry here = ModLists.Mod("here");
        ModEntry gone = ModLists.Mod("gone", workshopId: "12345");
        CurrentMods machine = Machine(new[] { here }, here);

        ModSyncPlan plan = ModSyncPlan.Build(ModLists.Snapshot(null, here, gone), machine);
        ModSyncRow row = Row(plan, "gone");

        Assert.False(row.Installed);
        Assert.Equal("12345", row.WorkshopId);

        row.Wanted = true;

        Assert.False(row.Changes);
        Assert.DoesNotContain("gone", plan.Resolve(machine).EnabledIds);
    }

    [Fact]
    public void A_mod_the_game_has_on_that_is_not_on_disk_can_still_be_taken_out()
    {
        ModEntry real = ModLists.Mod("real");
        var machine = new CurrentMods(
            ModLists.Snapshot(null, real, ModLists.Mod("ghost")),
            new[] { real });

        ModSyncPlan plan = ModSyncPlan.Build(null, machine);

        Assert.False(Row(plan, "ghost").Installed);
        Assert.Equal(new[] { "real" }, plan.Resolve(machine).EnabledIds);
    }

    [Fact]
    public void Turning_a_mod_on_restores_the_place_the_save_had_it_in()
    {
        ModEntry off = ModLists.Mod("off");
        CurrentMods machine = Machine(Array.Empty<ModEntry>(), off);

        var recorded = ModLists.Snapshot(null, ModLists.Mod("off"));
        recorded.Mods[0].LoadOrder = 4;

        ModSyncPlan plan = ModSyncPlan.Build(recorded, machine);

        Assert.Equal(4, plan.Resolve(machine).LoadOrder["off"]);
    }

    [Fact]
    public void Undoing_the_changes_puts_every_tick_back_where_the_machine_has_it()
    {
        ModEntry on = ModLists.Mod("on");
        ModEntry off = ModLists.Mod("off");
        CurrentMods machine = Machine(new[] { on }, on, off);

        ModSyncPlan plan = ModSyncPlan.Build(null, machine);
        Row(plan, "off").Wanted = true;
        Row(plan, "on").Wanted = false;

        plan.WantEverythingOnNow();

        Assert.True(plan.NothingToDo);
        Assert.True(Row(plan, "on").Wanted);
        Assert.False(Row(plan, "off").Wanted);
    }

    [Fact]
    public void Matching_again_after_hand_edits_goes_back_to_the_saves_list()
    {
        ModEntry wanted = ModLists.Mod("wanted");
        ModEntry extra = ModLists.Mod("extra");
        CurrentMods machine = Machine(new[] { extra }, wanted, extra);

        ModSyncPlan plan = ModSyncPlan.Build(ModLists.Snapshot(null, wanted), machine);
        Row(plan, "extra").Wanted = true;
        Row(plan, "wanted").Wanted = false;

        plan.WantWhatTheSaveHad();

        Assert.True(Row(plan, "wanted").Wanted);
        Assert.False(Row(plan, "extra").Wanted);
    }

    [Fact]
    public void A_machine_that_already_matches_has_nothing_to_apply()
    {
        ModEntry mod = ModLists.Mod("a");
        CurrentMods machine = Machine(new[] { mod }, mod);

        Assert.True(ModSyncPlan.Build(ModLists.Snapshot(null, mod), machine).NothingToDo);
    }
}
