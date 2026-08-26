using System.IO;
using RainWorldCompanion.Core.Editing;
using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Tests;

/// <summary>
/// sav3.bin is a real save from a played game with the mod installed, and it happens to hold
/// the awkward case: the player has swallowed three lizards, one of which has swallowed a
/// lizard of its own, and three of those four creatures are in the tamed list as well. So a
/// creature in it exists in the record several times over, which is the thing these tests are
/// mostly about.
/// </summary>
public class DevourmentEditorTests : IDisposable
{
    private readonly TempDirectory _directory = new("devourment");
    private readonly SaveEditSession _session;
    private readonly CampaignRecordRef _campaign;

    /// <summary>
    /// The lizard the player swallowed which has swallowed something itself, and which is tamed as
    /// well. The record holds it three times over, which is what most of these are about.
    /// </summary>
    private const string OuterLizard = "ID.5031.5490";

    /// <summary>The pink lizard inside that one, which is not tamed.</summary>
    private const string PinkInside = "ID.5030.5489";

    /// <summary>Another lizard the player swallowed, tamed, with nothing inside it.</summary>
    private const string TamedLizard = "ID.-1.3121";

    public DevourmentEditorTests()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sav3.bin");
        var path = Path.Combine(_directory.Path, "sav3");
        File.Copy(fixture, path);

        _session = SaveEditSession.Open(path);
        _campaign = _session.Campaigns.First(c =>
            DevourmentEditState.Read(_session.GetRecordBody(c)).Entries.Count > 0);
    }

    public void Dispose() => _directory.Dispose();

    private string Body => _session.GetRecordBody(_campaign);

    private DevourmentEditState Open() => DevourmentEditState.Read(Body);

    [Fact]
    public void Every_swallowed_thing_in_a_real_save_is_read()
    {
        var state = Open();

        Assert.Equal(4, state.Entries.Count);
        Assert.All(state.Entries, entry => Assert.True(entry.IsWellFormed));
    }

    [Fact]
    public void An_entry_is_read_into_the_four_parts_the_mod_wrote()
    {
        DevourmentEntry entry = Open().Entries.Single(e => e.PreyId == PinkInside);

        Assert.Equal("GreenLizard", entry.PredatorType);
        Assert.Equal("PinkLizard", entry.PreyType);
        Assert.False(entry.PreyIsItem);
        Assert.Equal(DevourmentStatus.Healing, entry.Status);
        Assert.Equal(4, DevourmentEditState.FoodOf(entry));
    }

    [Fact]
    public void The_nesting_a_real_save_holds_is_visible_in_the_entries()
    {
        var state = Open();

        // The player has the outer lizard, and the outer lizard has the pink one.
        Assert.Contains(state.Entries, e =>
            e.PredatorId == CreatureBlobBuilder.PlayerEntityId && e.PreyId == OuterLizard);

        Assert.Contains(state.Entries, e => e.PredatorId == OuterLizard && e.PreyId == PinkInside);
    }

    [Fact]
    public void The_tamed_list_is_read_as_whole_creatures()
    {
        var state = Open();

        Assert.Equal(3, state.Friends.Count);
        Assert.Contains(OuterLizard, state.TamedIds);
        Assert.Contains(TamedLizard, state.TamedIds);
        Assert.All(state.Friends, blob => Assert.NotNull(CreatureBlobBuilder.Parse(blob)));
    }

    [Fact]
    public void A_field_this_app_cannot_read_is_carried_as_the_text_it_arrived_as()
    {
        _session.SetField(_campaign, DevourmentEditState.EntryField, "something<dvD>newer", occurrence: 4);

        DevourmentEntry entry = Open().Entries[4];

        Assert.False(entry.IsWellFormed);
        Assert.Equal("something<dvD>newer", entry.ToFieldValue());
    }

    [Fact]
    public void Reading_and_applying_without_an_edit_gives_the_record_back_exactly()
    {
        var state = Open();

        Assert.False(state.IsDirty);
        Assert.Equal(Body, state.Apply(Body));
    }

    [Fact]
    public void Setting_a_value_to_what_it_already_holds_is_not_an_edit()
    {
        var state = Open();
        state.SetStatus(0, state.Entries[0].Status);
        state.SetFood(0, state.Entries[0].Food);

        Assert.False(state.IsDirty);
        Assert.Equal(Body, state.Apply(Body));
    }

    [Fact]
    public void Reordering_puts_the_entries_back_in_the_order_they_were_moved_into()
    {
        var state = Open();
        var before = state.Entries.Select(e => e.PreyId).ToList();

        state.Move(0, 2);

        Assert.Equal(
            new[] { before[1], before[2], before[0], before[3] },
            state.Entries.Select(e => e.PreyId));

        var reread = DevourmentEditState.Read(state.Apply(Body));
        Assert.Equal(state.Entries.Select(e => e.PreyId), reread.Entries.Select(e => e.PreyId));
    }

    [Fact]
    public void Moving_a_row_nowhere_changes_nothing()
    {
        var state = Open();
        state.Move(1, 1);
        state.Move(0, 99);
        state.Move(-1, 0);

        Assert.False(state.IsDirty);
    }

    [Fact]
    public void Removing_an_entry_takes_it_out_and_leaves_the_others()
    {
        var state = Open();
        string? gone = state.Entries[1].PreyId;

        state.RemoveAt(1);

        var reread = DevourmentEditState.Read(state.Apply(Body));

        Assert.Equal(3, reread.Entries.Count);
        Assert.DoesNotContain(reread.Entries, e => e.PreyId == gone);
    }

    /// <summary>
    /// Being in a stomach and being a friend are two different facts in two different fields, so
    /// pulling a creature out of one does not take it out of the other.
    /// </summary>
    [Fact]
    public void Removing_a_swallowed_creature_leaves_it_tamed()
    {
        var state = Open();
        state.RemoveAt(state.Entries.ToList().FindIndex(e => e.PreyId == TamedLizard));

        Assert.Contains(TamedLizard, DevourmentEditState.Read(state.Apply(Body)).TamedIds);
    }

    [Fact]
    public void The_status_of_one_entry_is_set_without_touching_the_rest()
    {
        var state = Open();
        string? other = state.Entries[1].Status;

        state.SetStatus(0, DevourmentStatus.Digesting);

        var reread = DevourmentEditState.Read(state.Apply(Body));

        Assert.Equal(DevourmentStatus.Digesting, reread.Entries[0].Status);
        Assert.Equal(other, reread.Entries[1].Status);
    }

    [Fact]
    public void Food_is_kept_as_text_so_a_value_the_mod_would_choke_on_still_goes_in()
    {
        var state = Open();
        state.SetFood(0, "not a number");

        var reread = DevourmentEditState.Read(state.Apply(Body));

        Assert.Equal("not a number", reread.Entries[0].Food);
        Assert.Null(DevourmentEditState.FoodOf(reread.Entries[0]));
    }

    [Fact]
    public void A_status_the_mod_does_not_know_is_written_and_reported_as_unknown()
    {
        var state = Open();
        state.SetStatus(0, "Marinating");

        Assert.False(DevourmentStatus.IsKnown("Marinating"));
        Assert.Equal("Marinating", DevourmentEditState.Read(state.Apply(Body)).Entries[0].Status);
    }

    [Fact]
    public void The_six_statuses_are_the_ones_the_mod_declares()
        => Assert.Equal(
            new[] { "Held", "Digesting", "EnergyTheft", "Healing", "Sedating", "Regurgitating" },
            DevourmentStatus.All);

    /// <summary>
    /// The nesting is not stored anywhere. It is implied by one entity id being prey in one field
    /// and predator in another, so moving something into something else is one field's predator
    /// being rewritten.
    /// </summary>
    [Fact]
    public void Moving_something_into_another_stomach_rewrites_one_predator()
    {
        var state = Open();
        string outer = state.Entries.Single(e => e.PreyId == OuterLizard).Prey;
        int index = state.Entries.ToList().FindIndex(e => e.PreyId == TamedLizard);

        Assert.True(state.SetPredator(index, outer));

        var reread = DevourmentEditState.Read(state.Apply(Body));

        Assert.Equal(OuterLizard, reread.Entries.Single(e => e.PreyId == TamedLizard).PredatorId);
        Assert.Equal(4, reread.Entries.Count);
    }

    [Fact]
    public void Something_moved_follows_its_new_predator_into_its_room()
    {
        var state = Open();
        string outer = state.Entries.Single(e => e.PreyId == OuterLizard).Prey;
        string room = CreatureBlobBuilder.Parse(outer)!.Room;
        int index = state.Entries.ToList().FindIndex(e => e.PreyId == TamedLizard);

        state.SetPredator(index, outer);

        var reread = DevourmentEditState.Read(state.Apply(Body));
        string moved = reread.Entries.Single(e => e.PreyId == TamedLizard).Prey;

        Assert.Equal(room, CreatureBlobBuilder.Parse(moved)!.Room);
    }

    [Fact]
    public void A_move_that_would_close_a_loop_is_refused_and_changes_nothing()
    {
        var state = Open();
        string inner = state.Entries.Single(e => e.PreyId == PinkInside).Prey;
        int index = state.Entries.ToList().FindIndex(e => e.PreyId == OuterLizard);

        Assert.False(state.SetPredator(index, inner));
        Assert.False(state.IsDirty);
        Assert.Equal(Body, state.Apply(Body));
    }

    [Fact]
    public void Something_cannot_be_put_inside_itself()
    {
        var state = Open();
        int index = state.Entries.ToList().FindIndex(e => e.PreyId == OuterLizard);

        Assert.False(state.SetPredator(index, state.Entries[index].Prey));
        Assert.False(state.IsDirty);
    }

    [Fact]
    public void Moving_something_where_it_already_is_changes_nothing()
    {
        var state = Open();
        int index = state.Entries.ToList().FindIndex(e => e.PreyId == PinkInside);

        Assert.False(state.SetPredator(index, state.Entries.Single(e => e.PreyId == OuterLizard).Prey));
        Assert.False(state.IsDirty);
    }

    [Fact]
    public void A_loop_is_seen_however_deep_it_is()
    {
        var state = Open();

        Assert.True(state.WouldLoop(OuterLizard, PinkInside));
        Assert.True(state.WouldLoop(CreatureBlobBuilder.PlayerEntityId, PinkInside));
        Assert.False(state.WouldLoop(PinkInside, OuterLizard));
    }

    [Fact]
    public void What_is_holding_something_is_read_off_the_entries()
    {
        var state = Open();

        Assert.Equal(OuterLizard, state.HolderOf(PinkInside));
        Assert.Equal(CreatureBlobBuilder.PlayerEntityId, state.HolderOf(OuterLizard));
        Assert.Null(state.HolderOf(CreatureBlobBuilder.PlayerEntityId));
    }

    [Fact]
    public void The_things_sharing_one_stomach_are_found_in_stored_order()
    {
        var state = Open();

        IReadOnlyList<int> inThePlayer = state.SiblingsOf(CreatureBlobBuilder.PlayerEntityId);

        Assert.Equal(3, inThePlayer.Count);
        Assert.Single(state.SiblingsOf(OuterLizard));
        Assert.Empty(state.SiblingsOf(PinkInside));
    }

    [Fact]
    public void Moving_one_thing_out_of_a_stomach_leaves_its_siblings_where_they_were()
    {
        var state = Open();
        var before = state.SiblingsOf(CreatureBlobBuilder.PlayerEntityId)
            .Select(i => state.Entries[i].PreyId)
            .ToList();

        int index = state.Entries.ToList().FindIndex(e => e.PreyId == TamedLizard);
        state.SetPredator(index, state.Entries.Single(e => e.PreyId == OuterLizard).Prey);

        var reread = DevourmentEditState.Read(state.Apply(Body));

        Assert.Equal(
            before.Where(id => id != TamedLizard),
            reread.SiblingsOf(CreatureBlobBuilder.PlayerEntityId).Select(i => reread.Entries[i].PreyId));
    }

    [Fact]
    public void A_creature_can_be_put_in_a_stomach()
    {
        var state = Open();
        string predator = state.Entries.First(e => e.PredatorId == CreatureBlobBuilder.PlayerEntityId).Predator;

        string id = state.AddCreature("PinkLizard", predator, DevourmentStatus.Digesting, "12");

        var reread = DevourmentEditState.Read(state.Apply(Body));
        DevourmentEntry added = reread.Entries.Single(e => e.PreyId == id);

        Assert.Equal("PinkLizard", added.PreyType);
        Assert.Equal(DevourmentStatus.Digesting, added.Status);
        Assert.Equal(12, DevourmentEditState.FoodOf(added));
    }

    [Fact]
    public void A_creature_added_is_put_in_the_room_its_predator_is_in()
    {
        var state = Open();
        string predator = state.Entries[0].Predator;
        string id = state.AddCreature("PinkLizard", predator);

        string prey = state.Entries.Single(e => e.PreyId == id).Prey;

        Assert.Equal(
            CreatureBlobBuilder.Parse(predator)!.Room,
            CreatureBlobBuilder.Parse(prey)!.Room);
    }

    /// <summary>
    /// The real reason the allocator sweeps the record rather than trusting the counter. This save
    /// has NEXTID at 3126 while holding creature ID.-1.8848, so counting from the counter alone
    /// would eventually hand out a number the campaign is already using.
    /// </summary>
    [Fact]
    public void A_new_creature_gets_an_id_above_every_id_the_record_already_holds()
    {
        var allocator = EntityIdAllocator.ForRecord(Body);
        Assert.True(allocator.StoredNextId < allocator.HighestSeen, "the fixture no longer shows this case");

        var state = Open();
        string id = state.AddCreature("PinkLizard", state.Entries[0].Predator);

        Assert.True(CreatureBlobBuilder.NumberOf(id) > allocator.HighestSeen);
    }

    [Fact]
    public void Adding_a_creature_moves_the_counter_on_so_the_game_carries_on_from_there()
    {
        var state = Open();
        string id = state.AddCreature("PinkLizard", state.Entries[0].Predator);

        string body = state.Apply(Body);

        Assert.Equal(
            CreatureBlobBuilder.NumberOf(id).ToString(),
            DelimitedFields.Record.GetValue(body, EntityIdAllocator.NextIdField));
    }

    [Fact]
    public void Two_creatures_added_do_not_share_an_id()
    {
        var state = Open();
        string predator = state.Entries[0].Predator;

        string first = state.AddCreature("PinkLizard", predator);
        string second = state.AddCreature("PinkLizard", predator);

        Assert.NotEqual(first, second);
    }

    /// <summary>
    /// The middle lizard is written into the record five times: as prey of the player, as the
    /// predator of two entries, and in the tamed list. All of them have to move together.
    /// </summary>
    [Fact]
    public void What_a_creature_thinks_of_the_player_is_set_in_every_copy_of_it()
    {
        var state = Open();

        state.SetFeelingTowardPlayer(OuterLizard, like: -1f, know: 0.5f);

        var reread = DevourmentEditState.Read(state.Apply(Body));

        foreach (string blob in CopiesOf(reread, OuterLizard))
        {
            var relation = CreatureBlobBuilder
                .ReadRelations(CreatureBlobBuilder.Parse(blob)!.State)
                .Single(r => r.SubjectId == CreatureBlobBuilder.PlayerEntityId);

            Assert.Equal(-1f, relation.Like);
            Assert.Equal(0.5f, relation.Know);
        }
    }

    [Fact]
    public void More_than_one_copy_of_that_creature_really_is_in_the_record()
        => Assert.True(CopiesOf(Open(), OuterLizard).Count >= 3);

    [Fact]
    public void Setting_one_creatures_feelings_leaves_the_others_alone()
    {
        var state = Open();
        var before = state.FeelingTowardPlayer(TamedLizard);

        state.SetFeelingTowardPlayer(OuterLizard, like: -1f, know: 0.5f);

        Assert.Equal(before?.Like, DevourmentEditState.Read(state.Apply(Body)).FeelingTowardPlayer(TamedLizard)?.Like);
    }

    [Fact]
    public void What_a_creature_thinks_of_the_player_is_read_back_off_it()
    {
        var relation = Open().FeelingTowardPlayer(OuterLizard);

        Assert.NotNull(relation);
        Assert.Equal(1f, relation!.Like);
    }

    /// <summary>
    /// The game's own rule reaching into this: a relationship with no like and no fear is not
    /// written, so a creature set to feel nothing about the player forgets them entirely.
    /// </summary>
    [Fact]
    public void A_creature_set_to_feel_nothing_forgets_the_player()
    {
        var state = Open();
        state.SetFeelingTowardPlayer(OuterLizard, like: 0f, know: 1f);

        Assert.Null(DevourmentEditState.Read(state.Apply(Body)).FeelingTowardPlayer(OuterLizard));
    }

    [Fact]
    public void Other_relationships_survive_setting_the_one_about_the_player()
    {
        var state = Open();
        string blob = CopiesOf(state, OuterLizard)[0];
        var before = CreatureBlobBuilder.ReadRelations(CreatureBlobBuilder.Parse(blob)!.State)
            .Where(r => r.SubjectId != CreatureBlobBuilder.PlayerEntityId)
            .ToList();

        Assert.NotEmpty(before);

        state.SetFeelingTowardPlayer(OuterLizard, like: -1f, know: 0.5f);

        var after = CreatureBlobBuilder
            .ReadRelations(CreatureBlobBuilder.Parse(CopiesOf(state, OuterLizard)[0])!.State)
            .Where(r => r.SubjectId != CreatureBlobBuilder.PlayerEntityId)
            .ToList();

        Assert.Equal(before.Select(r => r.SubjectId), after.Select(r => r.SubjectId));
        Assert.Equal(before.Select(r => r.Like), after.Select(r => r.Like));
    }

    [Fact]
    public void A_creature_can_be_untamed()
    {
        var state = Open();
        Assert.True(state.IsTamed(TamedLizard));

        state.SetTamed(TamedLizard, false);

        var reread = DevourmentEditState.Read(state.Apply(Body));

        Assert.False(reread.IsTamed(TamedLizard));
        Assert.Equal(2, reread.Friends.Count);
        Assert.True(reread.IsTamed(OuterLizard));
    }

    [Fact]
    public void Untaming_a_creature_leaves_it_in_the_stomach_it_is_in()
    {
        var state = Open();
        state.SetTamed(TamedLizard, false);

        Assert.Contains(DevourmentEditState.Read(state.Apply(Body)).Entries, e => e.PreyId == TamedLizard);
    }

    [Fact]
    public void A_creature_added_to_a_stomach_can_then_be_tamed()
    {
        var state = Open();
        string id = state.AddCreature("PinkLizard", state.Entries[0].Predator);

        state.SetTamed(id, true);

        var reread = DevourmentEditState.Read(state.Apply(Body));

        Assert.True(reread.IsTamed(id));
        Assert.Equal(4, reread.Friends.Count);
    }

    /// <summary>
    /// The list stores whole creatures rather than names, so there is nothing to add for a creature
    /// the record does not hold anywhere.
    /// </summary>
    [Fact]
    public void A_creature_the_record_does_not_hold_cannot_be_tamed()
    {
        var state = Open();

        state.SetTamed("ID.-1.999999", true);

        Assert.False(state.IsDirty);
        Assert.False(state.IsTamed("ID.-1.999999"));
    }

    [Fact]
    public void Taming_something_already_tamed_changes_nothing()
    {
        var state = Open();
        state.SetTamed(OuterLizard, true);

        Assert.False(state.IsDirty);
    }

    [Fact]
    public void Untaming_everything_takes_the_field_out_rather_than_leaving_it_empty()
    {
        var state = Open();

        foreach (string id in state.TamedIds.ToList())
        {
            state.SetTamed(id, false);
        }

        string body = state.Apply(Body);

        Assert.Null(DelimitedFields.Record.GetValue(body, DevourmentEditState.FriendsField));
        Assert.Empty(DevourmentEditState.Read(body).Friends);
    }

    [Fact]
    public void An_edited_campaign_still_writes_a_save_the_game_would_accept()
    {
        var state = Open();
        state.Move(0, 2);
        state.SetStatus(1, DevourmentStatus.Sedating);
        state.SetFeelingTowardPlayer(OuterLizard, -1f, 0.5f);
        state.AddCreature("PinkLizard", state.Entries[0].Predator);

        _session.ReplaceRecordBody(_campaign, state.Apply(Body), "Devourment");

        SaveWritePlan plan = _session.BuildWritePlan();

        Assert.Empty(plan.Problems);
        Assert.False(plan.IsNoOp);
    }

    [Fact]
    public void Everything_else_in_the_record_survives_a_devourment_edit()
    {
        string denPos = DelimitedFields.Record.GetValue(Body, "DENPOS") ?? "";
        string seed = DelimitedFields.Record.GetValue(Body, "SEED") ?? "";

        var state = Open();
        state.Move(0, 2);
        state.SetTamed(TamedLizard, false);

        string body = state.Apply(Body);

        Assert.Equal(denPos, DelimitedFields.Record.GetValue(body, "DENPOS"));
        Assert.Equal(seed, DelimitedFields.Record.GetValue(body, "SEED"));
    }

    private static List<string> CopiesOf(DevourmentEditState state, string entityId)
    {
        var copies = new List<string>();

        foreach (DevourmentEntry entry in state.Entries)
        {
            if (DevourmentReader.CreatureIdOf(entry.Predator) == entityId)
            {
                copies.Add(entry.Predator);
            }

            if (!entry.PreyIsItem && DevourmentReader.CreatureIdOf(entry.Prey) == entityId)
            {
                copies.Add(entry.Prey);
            }
        }

        copies.AddRange(state.Friends.Where(blob => DevourmentReader.CreatureIdOf(blob) == entityId));

        return copies;
    }
}
