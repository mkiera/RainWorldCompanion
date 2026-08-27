using RainWorldCompanion.Core.Mods;

namespace RainWorldCompanion.Tests;

public class ModSyncPlanTests
{
    [Fact]
    public void Build_sorts_each_mod_into_turn_on_install_turn_off_or_matches()
    {
        ModEntry offRecorded = ModLists.Mod("off.recorded");
        ModEntry missingRecorded = ModLists.Mod("missing.recorded");
        ModEntry matches = ModLists.Mod("matches");
        ModEntry extraNow = ModLists.Mod("extra.now");

        ModListSnapshot recorded = ModLists.Snapshot(null, offRecorded, missingRecorded, matches);
        var current = new CurrentMods(
            ModLists.Snapshot(null, matches, extraNow),
            new[] { offRecorded, matches });

        ModSyncPlan plan = ModSyncPlan.Build(recorded, current);

        Assert.Equal(ModSyncAction.TurnOn, RowFor(plan, "off.recorded").Action);
        Assert.Equal(ModSyncAction.Install, RowFor(plan, "missing.recorded").Action);
        Assert.Equal(ModSyncAction.TurnOff, RowFor(plan, "extra.now").Action);
        Assert.Equal(ModSyncAction.Matches, RowFor(plan, "matches").Action);
    }

    [Fact]
    public void Resolve_with_every_row_included_produces_the_recorded_enabled_set()
    {
        ModEntry a = ModLists.Mod("a");
        ModEntry b = ModLists.Mod("b");
        ModEntry extra = ModLists.Mod("extra");

        ModListSnapshot recorded = ModLists.Snapshot(null, a, b);
        var current = new CurrentMods(ModLists.Snapshot(null, b, extra), new[] { a, b });

        ModSyncPlan plan = ModSyncPlan.Build(recorded, current);
        ModSyncOutcome outcome = plan.Resolve(current);

        Assert.Equal(new[] { "a", "b" }, outcome.EnabledIds.OrderBy(id => id));
    }

    // The cosmetic-mod case: unticking a TurnOff row must leave the mod on.
    [Fact]
    public void Clearing_include_on_a_turn_off_row_leaves_that_mod_in_the_resolved_enabled_ids()
    {
        ModEntry cosmetic = ModLists.Mod("cosmetic");
        var current = new CurrentMods(ModLists.Snapshot(null, cosmetic), Array.Empty<ModEntry>());

        ModSyncPlan plan = ModSyncPlan.Build(ModLists.Snapshot(null), current);
        ModSyncRow row = RowFor(plan, "cosmetic");
        Assert.Equal(ModSyncAction.TurnOff, row.Action);

        row.Include = false;
        ModSyncOutcome outcome = plan.Resolve(current);

        Assert.Contains("cosmetic", outcome.EnabledIds);
    }

    [Fact]
    public void Clearing_include_on_a_turn_on_row_leaves_it_out()
    {
        ModEntry off = ModLists.Mod("off.mod");
        var current = new CurrentMods(ModLists.Snapshot(null), new[] { off });

        ModSyncPlan plan = ModSyncPlan.Build(ModLists.Snapshot(null, off), current);
        ModSyncRow row = RowFor(plan, "off.mod");
        Assert.Equal(ModSyncAction.TurnOn, row.Action);

        row.Include = false;
        ModSyncOutcome outcome = plan.Resolve(current);

        Assert.DoesNotContain("off.mod", outcome.EnabledIds);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_install_row_never_reaches_turn_on_or_turn_off_in_the_outcome(bool included)
    {
        ModEntry missing = ModLists.Mod("missing.mod");
        var current = new CurrentMods(ModLists.Snapshot(null), Array.Empty<ModEntry>());

        ModSyncPlan plan = ModSyncPlan.Build(ModLists.Snapshot(null, missing), current);
        ModSyncRow row = RowFor(plan, "missing.mod");
        Assert.Equal(ModSyncAction.Install, row.Action);
        row.Include = included;

        ModSyncOutcome outcome = plan.Resolve(current);

        Assert.Empty(outcome.TurnOn);
        Assert.Empty(outcome.TurnOff);
        Assert.DoesNotContain("missing.mod", outcome.EnabledIds);
    }

    [Fact]
    public void A_turn_on_row_carries_the_recorded_load_order_into_the_outcome()
    {
        ModEntry recordedMod = ModLists.Mod("ordered.mod");
        recordedMod.LoadOrder = 5;
        ModEntry installedMod = ModLists.Mod("ordered.mod");

        var current = new CurrentMods(ModLists.Snapshot(null), new[] { installedMod });
        ModSyncPlan plan = ModSyncPlan.Build(ModLists.Snapshot(null, recordedMod), current);

        Assert.Equal(5, RowFor(plan, "ordered.mod").RecordedLoadOrder);

        ModSyncOutcome outcome = plan.Resolve(current);

        Assert.Equal(5, outcome.LoadOrder["ordered.mod"]);
    }

    [Fact]
    public void Nothing_to_do_is_true_when_every_change_row_is_unticked()
    {
        ModEntry off = ModLists.Mod("off.mod");
        ModEntry extra = ModLists.Mod("extra.mod");
        var current = new CurrentMods(ModLists.Snapshot(null, extra), new[] { off });

        ModSyncPlan plan = ModSyncPlan.Build(ModLists.Snapshot(null, off), current);
        Assert.NotEmpty(plan.Changes);

        foreach (ModSyncRow row in plan.Changes)
        {
            row.Include = false;
        }

        Assert.True(plan.NothingToDo);
    }

    [Fact]
    public void Nothing_to_do_is_true_when_the_machine_already_matches()
    {
        CurrentMods current = ModLists.Current(null, ModLists.Mod("a"));

        ModSyncPlan plan = ModSyncPlan.Build(ModLists.Snapshot(null, ModLists.Mod("a")), current);

        Assert.Empty(plan.Changes);
        Assert.True(plan.NothingToDo);
    }

    [Fact]
    public void Build_against_a_null_recorded_snapshot_yields_only_matches_rows()
    {
        CurrentMods current = ModLists.Current(null, ModLists.Mod("a"), ModLists.Mod("b"));

        ModSyncPlan plan = ModSyncPlan.Build(null, current);

        Assert.NotEmpty(plan.Rows);
        Assert.All(plan.Rows, row => Assert.Equal(ModSyncAction.Matches, row.Action));
        Assert.True(plan.NothingToDo);
    }

    private static ModSyncRow RowFor(ModSyncPlan plan, string id)
        => plan.Rows.Single(row => string.Equals(row.Id, id, StringComparison.OrdinalIgnoreCase));
}
