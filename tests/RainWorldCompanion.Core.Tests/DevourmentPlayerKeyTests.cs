using RainWorldCompanion.Core.Editing;
using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Tests;

/// <summary>
/// A save written during a Rain Meadow session puts two more parts after the food value, naming
/// the player who owns the predator and the one who owns the prey. The values here are the shape
/// a real online save arrived in.
/// </summary>
public class DevourmentPlayerKeyTests
{
    private const string PlayerKey = "903948047";

    private const string Separator = "<dvD>";

    private const string Slugcat = "Slugcat<cA>ID.-1.0<cB>0<cA>SU_S04.-1<cA>";

    private const string Lizard = "PinkLizard<cA>ID.5030.3226<cB>0<cA>SU_S04.0<cA>";

    private const string OtherLizard = "GreenLizard<cA>ID.5038.2846<cB>0<cA>SU_S04.0<cA>";

    private const string Fly = "Fly<cA>ID.5.6<cB>0<cA>SU_S04.0<cA>";

    [Fact]
    public void An_entry_with_the_player_keys_is_read_rather_than_counted_as_unreadable()
    {
        DevourmentEntry entry = Single(Read(Online(Slugcat, Lizard, DevourmentStatus.Healing, "4")));

        Assert.True(entry.IsWellFormed);
        Assert.Equal("Slugcat", entry.PredatorType);
        Assert.Equal("PinkLizard", entry.PreyType);
        Assert.Equal(DevourmentStatus.Healing, entry.Status);
        Assert.Equal(4, DevourmentEditState.FoodOf(entry));
        Assert.Equal(PlayerKey, entry.PredatorPlayerKey);
    }

    [Fact]
    public void The_reader_the_campaign_list_uses_reads_it_too()
    {
        Assert.True(DevourmentReader.TryRead(
            Online(Slugcat, Lizard, DevourmentStatus.Healing, "4"),
            out var relationship));

        Assert.Equal("Slugcat", relationship!.PredatorType);
        Assert.Equal("PinkLizard", relationship.PreyType);
        Assert.Equal(4, relationship.FoodValue);
    }

    [Fact]
    public void An_edit_writes_the_player_keys_back_where_they_were()
    {
        string body = Body(Online(Slugcat, Lizard, DevourmentStatus.Healing, "4"));

        var state = DevourmentEditState.Read(body);
        state.SetStatus(0, DevourmentStatus.Digesting);
        state.SetFood(0, "9");

        Assert.Equal(
            string.Join(Separator, Slugcat, Lizard, DevourmentStatus.Digesting, "9", PlayerKey, ""),
            Single(DevourmentEditState.Read(state.Apply(body))).ToFieldValue());
    }

    [Fact]
    public void A_creature_added_under_a_predator_takes_the_player_key_that_predator_already_has()
    {
        var state = Read(Online(Slugcat, Lizard, DevourmentStatus.Healing, "4"));
        state.AddCreature("Fly", Slugcat);

        Assert.Equal(PlayerKey, state.Entries[1].PredatorPlayerKey);
    }

    [Fact]
    public void A_creature_added_under_a_predator_no_key_names_gets_the_four_parts_it_always_had()
    {
        var state = Read(Online(Slugcat, Lizard, DevourmentStatus.Healing, "4"));
        state.AddCreature("Fly", Lizard);

        DevourmentEntry added = state.Entries[1];

        Assert.Equal("", added.PredatorPlayerKey);
        Assert.Equal(
            string.Join(Separator, Lizard, added.Prey, DevourmentStatus.Held, "0"),
            added.ToFieldValue());
    }

    [Fact]
    public void Moving_a_row_off_a_player_does_not_leave_that_players_key_on_it()
    {
        var state = Read(Online(Slugcat, Lizard, DevourmentStatus.Healing, "4"));

        Assert.True(state.SetPredator(0, OtherLizard));
        Assert.Equal("", state.Entries[0].PredatorPlayerKey);
    }

    [Fact]
    public void Moving_a_row_onto_a_player_gives_it_that_players_key()
    {
        var state = Read(
            Online(Slugcat, Lizard, DevourmentStatus.Healing, "4"),
            string.Join(Separator, OtherLizard, Fly, DevourmentStatus.Held, "0"));

        Assert.True(state.SetPredator(1, Slugcat));
        Assert.Equal(PlayerKey, state.Entries[1].PredatorPlayerKey);
    }

    /// <summary>The prey key sits after the predator key, so writing one must not tread on it.</summary>
    [Fact]
    public void A_prey_that_is_itself_a_player_keeps_its_own_key()
    {
        var state = Read(string.Join(
            Separator, Slugcat, Fly, DevourmentStatus.Held, "0", PlayerKey, "22"));

        state.SetStatus(0, DevourmentStatus.Digesting);

        Assert.Equal(new[] { PlayerKey, "22" }, state.Entries[0].Extra);
    }

    [Fact]
    public void A_value_with_fewer_than_four_parts_is_carried_whole_and_written_back_untouched()
    {
        string value = Slugcat + Separator + Lizard;

        DevourmentEntry entry = Single(Read(value));

        Assert.False(entry.IsWellFormed);
        Assert.Equal(value, entry.ToFieldValue());
    }

    private static string Online(string predator, string prey, string status, string food)
        => string.Join(Separator, predator, prey, status, food, PlayerKey, "");

    private static string Body(params string[] values)
    {
        var fields = DelimitedFields.Record;
        string body = "";

        foreach (string value in values)
        {
            body = fields.Append(body, fields.Field(DevourmentEditState.EntryField, value));
        }

        return body;
    }

    private static DevourmentEditState Read(params string[] values)
        => DevourmentEditState.Read(Body(values));

    private static DevourmentEntry Single(DevourmentEditState state) => Assert.Single(state.Entries);
}
