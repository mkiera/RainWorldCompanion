using System.IO;
using RainWorldSaveManager.Core.Editing;
using RainWorldSaveManager.Core.Saves;
using RainWorldSaveManager.Core.Saves.Models;
using RainWorldSaveManager.ViewModels;

namespace RainWorldSaveManager.App.Tests;

/// <summary>
/// The Devourment editor, driven the way the window drives it.
///
/// Against sav3.bin, which is a real save with the mod installed: the player has swallowed three
/// lizards, one of those is holding a pink lizard, and three of them are tamed as well.
/// </summary>
public class DevourmentPanelTests : IDisposable
{
    private readonly TempDirectory _directory = new("devourment-panel");
    private readonly SaveEditSession _session;
    private readonly CampaignRecordRef _campaign;

    private const string OuterLizard = "ID.5031.5490";
    private const string TamedLizard = "ID.-1.3121";

    public DevourmentPanelTests()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sav3.bin");
        var path = Path.Combine(_directory.Path, "sav3");
        File.Copy(fixture, path);

        _session = SaveEditSession.Open(path);
        _campaign = _session.Campaigns.First(c =>
            DevourmentEditState.Read(_session.GetRecordBody(c)).Entries.Count > 0);
    }

    public void Dispose() => _directory.Dispose();

    private CampaignEditViewModel Editor() => new(
        _session,
        _campaign,
        new CampaignSummary { SlugcatId = _campaign.SlugcatId });

    private DevourmentEditViewModel Panel() => Editor().Devourment;

    private DevourmentEditState Stored()
        => DevourmentEditState.Read(_session.GetRecordBody(_campaign));

    // ---- what it shows ----

    [Fact]
    public void Every_swallowed_thing_gets_a_row_in_stored_order()
    {
        var panel = Panel();

        Assert.Equal(4, panel.Rows.Count);
        Assert.Equal(
            Stored().Entries.Select(e => e.PreyId),
            panel.Rows.Select(r => r.EntityId));
    }

    [Fact]
    public void A_row_says_what_it_is_and_what_is_holding_it()
    {
        DevourmentRowViewModel row = Panel().Rows.Single(r => r.EntityId == OuterLizard);

        Assert.Equal("Green Lizard", row.DisplayName);
        Assert.Equal("Slugcat", row.PredatorName);
        Assert.Equal("1", row.Position);
        Assert.True(row.IsCreature);
    }

    [Fact]
    public void A_row_opens_holding_what_the_save_holds()
    {
        DevourmentRowViewModel row = Panel().Rows.Single(r => r.EntityId == OuterLizard);

        Assert.Equal("Held", row.Status);
        Assert.Equal("16", row.Food);
        Assert.Equal(1f, row.LikesValue);
        Assert.True(row.IsTamed);
    }

    [Fact]
    public void A_creature_that_is_not_on_the_friends_list_does_not_read_as_tamed()
        => Assert.False(Panel().Rows.Single(r => r.EntityId == "ID.5030.5489").IsTamed);

    [Fact]
    public void The_six_statuses_are_offered_on_every_row()
        => Assert.Equal(DevourmentStatus.All, Panel().Rows[0].StatusChoices);

    // ---- editing a row ----

    [Fact]
    public void Setting_a_status_writes_it_into_the_campaign()
    {
        var panel = Panel();
        panel.Rows[0].Status = DevourmentStatus.Digesting;

        Assert.Equal(DevourmentStatus.Digesting, Stored().Entries[0].Status);
    }

    [Fact]
    public void Setting_food_writes_it_into_the_campaign()
    {
        var panel = Panel();
        panel.Rows[0].Food = "99";

        Assert.Equal("99", Stored().Entries[0].Food);
    }

    [Fact]
    public void Setting_how_much_it_likes_you_writes_every_copy_of_that_creature()
    {
        var panel = Panel();
        panel.Rows.Single(r => r.EntityId == OuterLizard).Likes = "-0.5";

        var stored = Stored();

        Assert.Equal(-0.5f, stored.FeelingTowardPlayer(OuterLizard)?.Like);
        Assert.Contains(stored.Friends, blob =>
            DevourmentReader.CreatureIdOf(blob) == OuterLizard && blob.Contains("L<rB>-0.5"));
    }

    [Fact]
    public void Untaming_from_a_row_takes_it_off_the_friends_list()
    {
        var panel = Panel();
        panel.Rows.Single(r => r.EntityId == TamedLizard).IsTamed = false;

        Assert.False(Stored().IsTamed(TamedLizard));
    }

    [Fact]
    public void An_item_row_offers_no_feelings_and_no_taming()
    {
        var panel = Panel();

        foreach (DevourmentRowViewModel row in panel.Rows.Where(r => r.IsItem))
        {
            Assert.False(row.IsCreature);
        }
    }

    // ---- order ----

    [Fact]
    public void Dragging_a_row_onto_another_moves_it_there()
    {
        var panel = Panel();
        var before = panel.Rows.Select(r => r.EntityId).ToList();

        panel.MoveTo(0, 2);

        Assert.Equal(
            new[] { before[1], before[2], before[0], before[3] },
            Stored().Entries.Select(e => e.PreyId));
    }

    [Fact]
    public void The_arrows_move_a_row_one_place()
    {
        var panel = Panel();
        string? second = panel.Rows[1].EntityId;

        panel.MoveUpCommand.Execute(panel.Rows[1]);

        Assert.Equal(second, Stored().Entries[0].PreyId);

        panel.MoveDownCommand.Execute(panel.Rows.Single(r => r.EntityId == second));

        Assert.Equal(second, Stored().Entries[1].PreyId);
    }

    [Fact]
    public void Moving_off_the_end_of_the_list_leaves_it_alone()
    {
        var panel = Panel();
        var before = Stored().Entries.Select(e => e.PreyId).ToList();

        panel.MoveUpCommand.Execute(panel.Rows[0]);
        panel.MoveDownCommand.Execute(panel.Rows[^1]);

        Assert.Equal(before, Stored().Entries.Select(e => e.PreyId));
    }

    [Fact]
    public void The_rows_are_renumbered_after_a_move()
    {
        var panel = Panel();
        panel.MoveTo(0, 2);

        Assert.Equal(new[] { "1", "2", "3", "4" }, panel.Rows.Select(r => r.Position));
    }

    // ---- adding and removing ----

    [Fact]
    public void A_creature_can_be_put_inside_something()
    {
        var panel = Panel();
        panel.NewCreatureSearch = "PinkLizard";
        panel.AddCreatureCommand.Execute(null);

        Assert.Equal(5, panel.Rows.Count);
        Assert.Equal(5, Stored().Entries.Count);
        Assert.Contains(panel.Rows, r => r.DisplayName == "Pink Lizard" && r.Position == "5");
    }

    [Fact]
    public void Adding_goes_into_the_chosen_stomach()
    {
        var panel = Panel();
        panel.NewCreaturePredator = panel.Predators.Single(p => p.EntityId == OuterLizard);
        panel.NewCreatureSearch = "Fly";
        panel.AddCreatureCommand.Execute(null);

        Assert.Equal(OuterLizard, Stored().Entries[^1].PredatorId);
    }

    [Fact]
    public void Clicking_a_suggested_creature_adds_that_one()
    {
        var panel = Panel();
        panel.AddCreatureCommand.Execute("KingVulture");

        Assert.Equal("KingVulture", Stored().Entries[^1].PreyType);
    }

    [Fact]
    public void Adding_nothing_does_nothing()
    {
        var panel = Panel();
        panel.NewCreatureSearch = "   ";
        panel.AddCreatureCommand.Execute(null);

        Assert.Equal(4, Stored().Entries.Count);
    }

    [Fact]
    public void You_are_offered_as_a_stomach_and_chosen_by_default()
    {
        var panel = Panel();

        Assert.Equal("You", panel.NewCreaturePredator?.DisplayName);
        Assert.Equal(CreatureBlobBuilder.PlayerEntityId, panel.NewCreaturePredator?.EntityId);
    }

    [Fact]
    public void Everything_already_in_the_campaign_is_offered_as_a_stomach()
    {
        var panel = Panel();

        Assert.Contains(panel.Predators, p => p.EntityId == OuterLizard);
        Assert.Contains(panel.Predators, p => p.EntityId == TamedLizard);
        Assert.Equal(panel.Predators.Count, panel.Predators.Select(p => p.EntityId).Distinct().Count());
    }

    [Fact]
    public void Removing_a_row_takes_it_out()
    {
        var panel = Panel();
        string? gone = panel.Rows[1].EntityId;

        panel.RemoveRowCommand.Execute(panel.Rows[1]);

        Assert.Equal(3, panel.Rows.Count);
        Assert.DoesNotContain(Stored().Entries, e => e.PreyId == gone);
    }

    [Fact]
    public void A_creature_added_can_be_tamed_straight_away()
    {
        var panel = Panel();
        panel.AddCreatureCommand.Execute("PinkLizard");

        DevourmentRowViewModel added = panel.Rows[^1];
        added.IsTamed = true;

        Assert.True(Stored().IsTamed(added.EntityId));
    }

    // ---- searching ----

    [Fact]
    public void The_creature_box_offers_what_matches_what_is_typed()
    {
        var panel = Panel();
        panel.NewCreatureSearch = "vult";

        Assert.Contains(panel.CreatureMatches, kind => kind.Name == "KingVulture");
        Assert.DoesNotContain(panel.CreatureMatches, kind => kind.Name == "PinkLizard");
    }

    // ---- advice ----

    [Fact]
    public void A_status_the_mod_cannot_read_is_written_and_warned_about()
    {
        var panel = Panel();
        panel.Rows[0].Status = "Marinating";

        Assert.Equal("Marinating", Stored().Entries[0].Status);
        Assert.Contains(panel.Warnings, w => w.Contains("does not know"));
    }

    [Fact]
    public void Food_that_is_not_a_number_is_written_and_warned_about()
    {
        var panel = Panel();
        panel.Rows[0].Food = "lots";

        Assert.Equal("lots", Stored().Entries[0].Food);
        Assert.Contains(panel.Warnings, w => w.Contains("reads as a number"));
    }

    [Fact]
    public void Liking_you_zero_says_what_it_costs()
    {
        var panel = Panel();
        DevourmentRowViewModel row = panel.Rows.Single(r => r.EntityId == OuterLizard);

        row.Likes = "0";

        Assert.Contains(panel.Warnings, w => w.Contains("goes with it"));
        Assert.Null(Stored().FeelingTowardPlayer(OuterLizard));
    }

    [Fact]
    public void The_panels_advice_reaches_the_campaign_editor()
    {
        var editor = Editor();
        editor.Devourment.Rows[0].Status = "Marinating";

        Assert.True(editor.HasWarnings);
        Assert.Contains(editor.Warnings, w => w.Contains("does not know"));
    }

    // ---- the change log ----

    [Fact]
    public void An_edit_reads_as_something_a_person_did()
    {
        var editor = Editor();
        editor.Devourment.Rows.Single(r => r.EntityId == OuterLizard).Status = DevourmentStatus.Digesting;

        Assert.Contains(editor.Changes, c => c.Contains("set the Green Lizard to Digesting"));
    }

    [Fact]
    public void Dragging_rows_about_reads_as_one_change()
    {
        var editor = Editor();

        editor.Devourment.MoveTo(0, 2);
        editor.Devourment.MoveTo(2, 1);
        editor.Devourment.MoveTo(1, 3);

        Assert.Single(editor.Changes);
        Assert.Contains(editor.Changes, c => c.Contains("changed the order"));
    }

    [Fact]
    public void Retyping_one_box_reads_as_one_change()
    {
        var editor = Editor();
        DevourmentRowViewModel row = editor.Devourment.Rows[0];

        row.Food = "1";
        row.Food = "12";
        row.Food = "123";

        Assert.Single(editor.Changes);
        Assert.Contains(editor.Changes, c => c.Contains("123 food"));
    }

    [Fact]
    public void A_devourment_edit_makes_the_editor_dirty_and_saveable()
    {
        var editor = Editor();
        editor.Devourment.Rows[0].Status = DevourmentStatus.Sedating;

        Assert.True(editor.IsDirty);

        SaveWritePlan plan = editor.BuildWritePlan();

        Assert.Empty(plan.Problems);
        Assert.False(plan.IsNoOp);
    }

    [Fact]
    public void The_raw_field_list_shows_what_the_devourment_editor_wrote()
    {
        var editor = Editor();
        editor.Devourment.AddCreatureCommand.Execute("PinkLizard");

        Assert.Equal(
            5,
            editor.RawFields.Count(row => row.Key == DevourmentEditState.EntryField));
    }
}
