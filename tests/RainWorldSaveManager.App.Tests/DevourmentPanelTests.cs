using System.IO;
using RainWorldSaveManager.Core.Editing;
using RainWorldSaveManager.Core.Saves;
using RainWorldSaveManager.Core.Saves.Models;
using RainWorldSaveManager.ViewModels;

namespace RainWorldSaveManager.App.Tests;

/// <summary>
/// The Devourment editor, driven the way the window drives it.
///
/// Against sav3.bin, a real save with the mod installed: the player has swallowed three lizards,
/// one of those is holding a pink lizard, and three of the four creatures are tamed as well.
///
/// The shape is the thing most of these are about. What a save stores is a flat list of predator
/// and prey pairs, and the chains are implied by ids, so the editor has to both draw the chains and
/// turn a move back into an edit of one pair.
/// </summary>
public class DevourmentPanelTests : IDisposable
{
    private readonly TempDirectory _directory = new("devourment-panel");
    private readonly SaveEditSession _session;
    private readonly CampaignRecordRef _campaign;

    /// <summary>Swallowed by the player, holding the pink lizard, and tamed.</summary>
    private const string OuterLizard = "ID.5031.5490";

    /// <summary>Inside the outer lizard, and not tamed.</summary>
    private const string PinkInside = "ID.5030.5489";

    /// <summary>Swallowed by the player, tamed, holding nothing.</summary>
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

    private static DevourmentEditNode Node(DevourmentEditViewModel panel, string entityId)
        => panel.AllNodes.First(n => n.EntityId == entityId && !n.IsRoot);

    // ---- the shape ----

    [Fact]
    public void The_chains_are_drawn_as_the_save_describes_them()
    {
        var panel = Panel();

        DevourmentEditNode player = panel.Roots.Single();

        Assert.True(player.IsRoot);
        Assert.Equal(CreatureBlobBuilder.PlayerEntityId, player.EntityId);
        Assert.Equal(3, player.Children.Count);
    }

    [Fact]
    public void Something_inside_something_else_is_drawn_inside_it()
    {
        var panel = Panel();

        DevourmentEditNode outer = Node(panel, OuterLizard);

        Assert.Equal(PinkInside, outer.Children.Single().EntityId);
        Assert.Equal(0, outer.Depth == 0 ? 0 : outer.Depth - 1);
        Assert.Equal(outer.Depth + 1, outer.Children.Single().Depth);
    }

    [Fact]
    public void Every_swallowed_thing_reaches_the_tree_exactly_once()
    {
        var panel = Panel();

        Assert.Equal(4, panel.AllNodes.Count(n => !n.IsRoot));
        Assert.Equal(4, Stored().Entries.Count);
    }

    [Fact]
    public void A_root_is_not_swallowed_so_it_has_no_state_or_food()
    {
        DevourmentEditNode player = Panel().Roots.Single();

        Assert.False(player.IsSwallowed);
        Assert.Equal("", player.Status);
        Assert.Equal("", player.Food);
    }

    [Fact]
    public void A_row_says_how_much_is_below_it()
    {
        var panel = Panel();

        Assert.Equal("holds 4", panel.Roots.Single().ContentsSummary);
        Assert.Equal("holds 1", Node(panel, OuterLizard).ContentsSummary);
        Assert.Equal("", Node(panel, TamedLizard).ContentsSummary);
    }

    [Fact]
    public void A_row_opens_holding_what_the_save_holds()
    {
        DevourmentEditNode node = Node(Panel(), OuterLizard);

        Assert.Equal("Green Lizard", node.DisplayName);
        Assert.Equal("Held", node.Status);
        Assert.Equal("16", node.Food);
        Assert.Equal(1f, node.LikesValue);
        Assert.True(node.IsTamed);
    }

    [Fact]
    public void A_creature_that_is_not_on_the_friends_list_does_not_read_as_tamed()
        => Assert.False(Node(Panel(), PinkInside).IsTamed);

    // ---- the row's own editors ----

    [Fact]
    public void Only_one_row_shows_its_editors_at_a_time()
    {
        var panel = Panel();

        Node(panel, OuterLizard).IsEditing = true;
        Node(panel, TamedLizard).IsEditing = true;

        Assert.False(Node(panel, OuterLizard).IsEditing);
        Assert.True(Node(panel, TamedLizard).IsEditing);
    }

    [Fact]
    public void The_row_being_edited_stays_open_across_an_edit()
    {
        var panel = Panel();
        Node(panel, OuterLizard).IsEditing = true;

        Node(panel, OuterLizard).Status = DevourmentStatus.Digesting;

        Assert.True(Node(panel, OuterLizard).IsEditing);
    }

    [Fact]
    public void Setting_a_status_writes_it_into_the_campaign()
    {
        var panel = Panel();
        Node(panel, OuterLizard).Status = DevourmentStatus.Digesting;

        Assert.Equal(
            DevourmentStatus.Digesting,
            Stored().Entries.Single(e => e.PreyId == OuterLizard).Status);
    }

    [Fact]
    public void Setting_food_writes_it_into_the_campaign()
    {
        var panel = Panel();
        Node(panel, OuterLizard).Food = "99";

        Assert.Equal("99", Stored().Entries.Single(e => e.PreyId == OuterLizard).Food);
    }

    [Fact]
    public void Setting_how_much_it_likes_you_writes_every_copy_of_that_creature()
    {
        var panel = Panel();
        Node(panel, OuterLizard).Likes = "-0.5";

        var stored = Stored();

        Assert.Equal(-0.5f, stored.FeelingTowardPlayer(OuterLizard)?.Like);
        Assert.Contains(stored.Friends, blob =>
            DevourmentReader.CreatureIdOf(blob) == OuterLizard && blob.Contains("L<rB>-0.5"));
    }

    [Fact]
    public void Untaming_from_a_row_takes_it_off_the_friends_list()
    {
        var panel = Panel();
        Node(panel, TamedLizard).IsTamed = false;

        Assert.False(Stored().IsTamed(TamedLizard));
    }

    [Fact]
    public void An_item_row_offers_no_feelings_and_no_taming()
        => Assert.All(Panel().AllNodes.Where(n => n.IsItem), node => Assert.False(node.IsCreature));

    // ---- moving one thing inside another ----

    [Fact]
    public void Dropping_a_row_onto_a_creature_puts_it_inside_that_creature()
    {
        var panel = Panel();

        panel.MoveOnto(Node(panel, TamedLizard), Node(panel, OuterLizard));

        Assert.Equal(OuterLizard, Stored().Entries.Single(e => e.PreyId == TamedLizard).PredatorId);
        Assert.Contains(Node(panel, OuterLizard).Children, c => c.EntityId == TamedLizard);
    }

    [Fact]
    public void What_was_moved_leaves_the_stomach_it_was_in()
    {
        var panel = Panel();
        panel.MoveOnto(Node(panel, TamedLizard), Node(panel, OuterLizard));

        Assert.DoesNotContain(panel.Roots.Single().Children, c => c.EntityId == TamedLizard);
        Assert.Equal(2, panel.Roots.Single().Children.Count);
    }

    [Fact]
    public void Something_moved_follows_its_new_predator_into_its_room()
    {
        var panel = Panel();
        string room = CreatureBlobBuilder.Parse(Node(panel, OuterLizard).Blob)!.Room;

        panel.MoveOnto(Node(panel, TamedLizard), Node(panel, OuterLizard));

        string moved = Stored().Entries.Single(e => e.PreyId == TamedLizard).Prey;

        Assert.Equal(room, CreatureBlobBuilder.Parse(moved)!.Room);
    }

    /// <summary>
    /// The one gesture that is refused rather than warned about. A thing inside itself is not a
    /// value somebody chose, it is a shape that cannot exist: the reader would follow it forever.
    /// </summary>
    [Fact]
    public void A_move_that_would_put_something_inside_itself_is_refused_and_says_why()
    {
        var panel = Panel();
        var before = Stored().Entries.Select(e => e.PredatorId).ToList();

        panel.MoveOnto(Node(panel, OuterLizard), Node(panel, PinkInside));

        Assert.Equal(before, Stored().Entries.Select(e => e.PredatorId));
        Assert.Contains(panel.Warnings, w => w.Contains("loop"));
    }

    [Fact]
    public void A_move_that_works_clears_the_refusal_from_before_it()
    {
        var panel = Panel();
        panel.MoveOnto(Node(panel, OuterLizard), Node(panel, PinkInside));
        Assert.Contains(panel.Warnings, w => w.Contains("loop"));

        panel.MoveOnto(Node(panel, TamedLizard), Node(panel, OuterLizard));

        Assert.DoesNotContain(panel.Warnings, w => w.Contains("loop"));
    }

    [Fact]
    public void Nothing_can_be_put_inside_an_item()
        => Assert.All(Panel().AllNodes.Where(n => n.IsItem), node => Assert.False(node.CanHoldThings));

    [Fact]
    public void Dropping_something_onto_what_already_holds_it_changes_nothing()
    {
        var panel = Panel();
        var before = Stored().Entries.Select(e => e.PredatorId).ToList();

        panel.MoveOnto(Node(panel, PinkInside), Node(panel, OuterLizard));

        Assert.Equal(before, Stored().Entries.Select(e => e.PredatorId));
    }

    // ---- order among the things sharing a stomach ----

    [Fact]
    public void The_arrows_move_a_row_past_the_one_beside_it()
    {
        var panel = Panel();
        var before = panel.Roots.Single().Children.Select(c => c.EntityId).ToList();

        panel.MoveDownCommand.Execute(Node(panel, before[0]));

        Assert.Equal(
            new[] { before[1], before[0], before[2] },
            panel.Roots.Single().Children.Select(c => c.EntityId));
    }

    [Fact]
    public void The_arrows_do_not_change_what_is_holding_what()
    {
        var panel = Panel();
        panel.MoveDownCommand.Execute(panel.Roots.Single().Children[0]);

        Assert.Equal(OuterLizard, Stored().Entries.Single(e => e.PreyId == PinkInside).PredatorId);
    }

    [Fact]
    public void Moving_off_the_end_of_a_stomach_leaves_it_alone()
    {
        var panel = Panel();
        var before = Stored().Entries.Select(e => e.PreyId).ToList();

        panel.MoveUpCommand.Execute(panel.Roots.Single().Children[0]);
        panel.MoveDownCommand.Execute(panel.Roots.Single().Children[^1]);

        Assert.Equal(before, Stored().Entries.Select(e => e.PreyId));
    }

    /// <summary>
    /// The only thing sharing the outer lizard's stomach is the pink lizard, so there is nothing
    /// for it to move past even though the list as a whole has four entries.
    /// </summary>
    [Fact]
    public void An_only_child_has_nowhere_to_move()
    {
        var panel = Panel();
        var before = Stored().Entries.Select(e => e.PreyId).ToList();

        panel.MoveUpCommand.Execute(Node(panel, PinkInside));
        panel.MoveDownCommand.Execute(Node(panel, PinkInside));

        Assert.Equal(before, Stored().Entries.Select(e => e.PreyId));
    }

    // ---- adding and removing ----

    [Fact]
    public void A_creature_can_be_put_inside_something()
    {
        var panel = Panel();
        panel.NewCreatureSearch = "PinkLizard";
        panel.AddCreatureCommand.Execute(null);

        Assert.Equal(5, Stored().Entries.Count);
        Assert.Contains(panel.Roots.Single().Children, c => c.DisplayName == "Pink Lizard");
    }

    [Fact]
    public void Adding_goes_into_the_chosen_stomach()
    {
        var panel = Panel();
        panel.NewCreaturePredator = panel.Predators.Single(p => p.EntityId == OuterLizard);
        panel.NewCreatureSearch = "Fly";
        panel.AddCreatureCommand.Execute(null);

        Assert.Contains(Node(panel, OuterLizard).Children, c => c.DisplayName == "Fly");
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

        panel.RemoveNodeCommand.Execute(Node(panel, TamedLizard));

        Assert.Equal(3, Stored().Entries.Count);
        Assert.DoesNotContain(Stored().Entries, e => e.PreyId == TamedLizard);
    }

    /// <summary>
    /// Taking out something that is holding things would otherwise take everything below it with
    /// it, which is not what removing one row is meant to do.
    /// </summary>
    [Fact]
    public void What_was_inside_a_removed_row_moves_up_to_whatever_was_holding_it()
    {
        var panel = Panel();

        panel.RemoveNodeCommand.Execute(Node(panel, OuterLizard));

        Assert.DoesNotContain(Stored().Entries, e => e.PreyId == OuterLizard);
        Assert.Equal(
            CreatureBlobBuilder.PlayerEntityId,
            Stored().Entries.Single(e => e.PreyId == PinkInside).PredatorId);
        Assert.Contains(panel.Roots.Single().Children, c => c.EntityId == PinkInside);
    }

    [Fact]
    public void A_creature_added_can_be_tamed_straight_away()
    {
        var panel = Panel();
        panel.AddCreatureCommand.Execute("PinkLizard");

        DevourmentEditNode added = panel.Roots.Single().Children[^1];
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
        Node(panel, OuterLizard).Status = "Marinating";

        Assert.Equal("Marinating", Stored().Entries.Single(e => e.PreyId == OuterLizard).Status);
        Assert.Contains(panel.Warnings, w => w.Contains("does not know"));
    }

    [Fact]
    public void Food_that_is_not_a_number_is_written_and_warned_about()
    {
        var panel = Panel();
        Node(panel, OuterLizard).Food = "lots";

        Assert.Equal("lots", Stored().Entries.Single(e => e.PreyId == OuterLizard).Food);
        Assert.Contains(panel.Warnings, w => w.Contains("reads as a number"));
    }

    [Fact]
    public void Liking_you_zero_says_what_it_costs()
    {
        var panel = Panel();
        Node(panel, OuterLizard).Likes = "0";

        Assert.Contains(panel.Warnings, w => w.Contains("goes with it"));
        Assert.Null(Stored().FeelingTowardPlayer(OuterLizard));
    }

    [Fact]
    public void The_panels_advice_reaches_the_campaign_editor()
    {
        var editor = Editor();
        Node(editor.Devourment, OuterLizard).Status = "Marinating";

        Assert.True(editor.HasWarnings);
        Assert.Contains(editor.Warnings, w => w.Contains("does not know"));
    }

    // ---- the change log ----

    [Fact]
    public void An_edit_reads_as_something_a_person_did()
    {
        var editor = Editor();
        Node(editor.Devourment, OuterLizard).Status = DevourmentStatus.Digesting;

        Assert.Contains(editor.Changes, c => c.Contains("set the Green Lizard to Digesting"));
    }

    [Fact]
    public void A_move_reads_as_one_thing_going_inside_another()
    {
        var editor = Editor();

        editor.Devourment.MoveOnto(
            Node(editor.Devourment, TamedLizard),
            Node(editor.Devourment, OuterLizard));

        Assert.Contains(editor.Changes, c => c.Contains("moved the Green Lizard inside"));
    }

    [Fact]
    public void Shuffling_rows_about_reads_as_one_change()
    {
        var editor = Editor();
        var panel = editor.Devourment;

        panel.MoveDownCommand.Execute(panel.Roots.Single().Children[0]);
        panel.MoveUpCommand.Execute(panel.Roots.Single().Children[1]);
        panel.MoveDownCommand.Execute(panel.Roots.Single().Children[0]);

        Assert.Single(editor.Changes);
        Assert.Contains(editor.Changes, c => c.Contains("changed the order"));
    }

    [Fact]
    public void Retyping_one_box_reads_as_one_change()
    {
        var editor = Editor();
        DevourmentEditNode node = Node(editor.Devourment, OuterLizard);

        node.Food = "1";
        node.Food = "12";
        node.Food = "123";

        Assert.Single(editor.Changes);
        Assert.Contains(editor.Changes, c => c.Contains("123 food"));
    }

    [Fact]
    public void A_devourment_edit_makes_the_editor_dirty_and_saveable()
    {
        var editor = Editor();
        Node(editor.Devourment, OuterLizard).Status = DevourmentStatus.Sedating;

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
