using RainWorldCompanion.Core.Editing;
using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Tests;

/// <summary>
/// Building the strings the game writes for one creature.
///
/// The blobs quoted here are the game's own, taken from a played save rather than composed for the
/// test, because the only thing worth proving is that what this builds is what the game reads back.
/// The round trip through <see cref="EntityBlobReader"/> and <see cref="DevourmentReader"/> is the
/// other half of that: those were written against the game's reader, so a blob they agree with is
/// one the game agrees with.
/// </summary>
public class CreatureBlobBuilderTests
{
    /// <summary>The player, exactly as a real save stores it as a Devourment predator.</summary>
    private const string PlayerBlob = "Slugcat<cA>ID.-1.0<cB>0<cA>SL_S06.0<cA>";

    /// <summary>A swallowed lizard from the same save, carrying what it thinks of the player.</summary>
    private const string LizardBlob =
        "EelLizard<cA>ID.21022.8070<cB>0<cA>SL_S06.0<cA>Social<cC>REL<rA>ID.-1.0<rA>L<rB>1<rA>K<rB>0.925<smA>"
        + "REL<rA>ID.9033.7215<rA>L<rB>0.0045<rA>K<rB>0.0045<cB>";

    // ---- building ----

    [Fact]
    public void A_creature_with_nothing_to_remember_is_built_the_way_the_game_writes_the_player()
    {
        string blob = CreatureBlobBuilder.Build("Slugcat", "ID.-1.0", "SL_S06");

        Assert.Equal(PlayerBlob, blob);
    }

    [Fact]
    public void What_is_built_reads_back_as_the_creature_it_was_built_from()
    {
        string blob = CreatureBlobBuilder.Build("PinkLizard", "ID.-1.4210", "HI_S03", node: 2);

        Assert.Equal("PinkLizard", DevourmentReader.CreatureTypeOf(blob));
        Assert.Equal("ID.-1.4210", DevourmentReader.CreatureIdOf(blob));

        CreatureBlob parsed = CreatureBlobBuilder.Parse(blob)!;
        Assert.Equal("HI_S03", parsed.Room);
        Assert.Equal(2, parsed.Node);
        Assert.Equal("", parsed.State);
    }

    [Fact]
    public void A_real_blob_taken_apart_and_put_back_together_is_the_same_string()
    {
        Assert.Equal(PlayerBlob, CreatureBlobBuilder.ToBlob(CreatureBlobBuilder.Parse(PlayerBlob)!));
        Assert.Equal(LizardBlob, CreatureBlobBuilder.ToBlob(CreatureBlobBuilder.Parse(LizardBlob)!));
    }

    [Fact]
    public void A_real_blob_is_taken_apart_into_the_pieces_the_game_wrote()
    {
        CreatureBlob parsed = CreatureBlobBuilder.Parse(LizardBlob)!;

        Assert.Equal("EelLizard", parsed.Type);
        Assert.Equal("ID.21022.8070", parsed.EntityId);
        Assert.Equal(0, parsed.RippleLayer);
        Assert.Equal("SL_S06", parsed.Room);
        Assert.Equal(0, parsed.Node);
        Assert.StartsWith("Social<cC>REL", parsed.State);
    }

    [Fact]
    public void A_blob_too_short_for_the_games_own_reader_is_left_alone()
    {
        Assert.Null(CreatureBlobBuilder.Parse("PinkLizard<cA>ID.-1.4<cB>0"));
        Assert.Null(CreatureBlobBuilder.Parse(""));
        Assert.Null(CreatureBlobBuilder.Parse(null));

        // Nothing that takes a blob rewrites one it could not read.
        Assert.Equal("nonsense", CreatureBlobBuilder.WithRoom("nonsense", "HI_S03"));
    }

    [Fact]
    public void Moving_a_creature_leaves_everything_else_where_it_was()
    {
        string moved = CreatureBlobBuilder.WithRoom(LizardBlob, "HI_S01", 3);
        CreatureBlob parsed = CreatureBlobBuilder.Parse(moved)!;

        Assert.Equal("HI_S01", parsed.Room);
        Assert.Equal(3, parsed.Node);
        Assert.Equal("ID.21022.8070", parsed.EntityId);
        Assert.Equal(CreatureBlobBuilder.Parse(LizardBlob)!.State, parsed.State);
    }

    [Fact]
    public void A_room_name_with_a_dot_in_it_keeps_the_node_apart_from_the_name()
    {
        string blob = CreatureBlobBuilder.Build("Fly", "ID.-1.9", "SOME.ROOM", node: 4);
        CreatureBlob parsed = CreatureBlobBuilder.Parse(blob)!;

        Assert.Equal("SOME.ROOM", parsed.Room);
        Assert.Equal(4, parsed.Node);
    }

    [Fact]
    public void The_ripple_layer_survives_the_round_trip()
    {
        string blob = CreatureBlobBuilder.Build("Rattler", "ID.-1.7", "WAUA_TOYS", rippleLayer: 2);

        Assert.Contains("ID.-1.7<cB>2<cA>", blob);
        Assert.Equal(2, CreatureBlobBuilder.Parse(blob)!.RippleLayer);
    }

    // ---- ids ----

    [Theory]
    [InlineData("ID.-1.0", 0)]
    [InlineData("ID.21022.8070", 8070)]
    [InlineData("ID.-2588.11856", 11856)]
    public void The_number_half_of_an_id_is_the_half_that_has_to_be_unique(string id, int expected)
        => Assert.Equal(expected, CreatureBlobBuilder.NumberOf(id));

    [Fact]
    public void An_id_that_is_not_one_has_no_number()
    {
        Assert.Null(CreatureBlobBuilder.NumberOf("ID.4"));
        Assert.Null(CreatureBlobBuilder.NumberOf(""));
        Assert.Null(CreatureBlobBuilder.NumberOf(null));
    }

    [Fact]
    public void An_id_is_written_the_way_the_game_writes_one_it_issued()
        => Assert.Equal("ID.-1.4211", CreatureBlobBuilder.EntityId(-1, 4211));

    // ---- state blocks ----

    [Fact]
    public void A_state_block_is_read_out_of_a_real_creature()
    {
        string state = CreatureBlobBuilder.Parse(LizardBlob)!.State;

        Assert.NotNull(CreatureBlobBuilder.GetStateBlock(state, CreatureBlobBuilder.SocialTag));
        Assert.Null(CreatureBlobBuilder.GetStateBlock(state, CreatureBlobBuilder.MeatLeftTag));
    }

    [Fact]
    public void A_block_written_bare_counts_as_there_without_having_a_value()
    {
        string state = CreatureBlobBuilder.SetStateBlock("", CreatureBlobBuilder.MeatLeftTag, "3");
        state = "Dead<cB>" + state;

        Assert.True(CreatureBlobBuilder.HasStateBlock(state, CreatureBlobBuilder.DeadTag));
        Assert.Null(CreatureBlobBuilder.GetStateBlock(state, CreatureBlobBuilder.DeadTag));
        Assert.Equal("3", CreatureBlobBuilder.GetStateBlock(state, CreatureBlobBuilder.MeatLeftTag));
    }

    /// <summary>
    /// The game ends every state block with a separator, including the last one, which is why a
    /// real save reads "...&lt;cB&gt;&lt;dvD&gt;Held".
    /// </summary>
    [Fact]
    public void Every_state_block_written_ends_with_a_separator()
    {
        string state = CreatureBlobBuilder.SetStateBlock("", CreatureBlobBuilder.MeatLeftTag, "3");

        Assert.Equal("MeatLeft<cC>3<cB>", state);
        Assert.EndsWith("<cB>", CreatureBlobBuilder.Parse(LizardBlob)!.State);
    }

    [Fact]
    public void Writing_a_block_that_is_already_there_replaces_it_where_it_stands()
    {
        string state = "Social<cC>REL<rA>ID.-1.0<rA>L<rB>1<cB>MeatLeft<cC>3<cB>";

        string updated = CreatureBlobBuilder.SetStateBlock(state, CreatureBlobBuilder.MeatLeftTag, "1");

        Assert.Equal("Social<cC>REL<rA>ID.-1.0<rA>L<rB>1<cB>MeatLeft<cC>1<cB>", updated);
    }

    [Fact]
    public void Writing_null_takes_a_block_out()
    {
        string state = "Social<cC>REL<rA>ID.-1.0<rA>L<rB>1<cB>MeatLeft<cC>3<cB>";

        Assert.Equal(
            "Social<cC>REL<rA>ID.-1.0<rA>L<rB>1<cB>",
            CreatureBlobBuilder.SetStateBlock(state, CreatureBlobBuilder.MeatLeftTag, null));
    }

    [Fact]
    public void Taking_the_last_block_out_leaves_the_empty_state_a_fresh_creature_has()
        => Assert.Equal("", CreatureBlobBuilder.SetStateBlock("MeatLeft<cC>3<cB>", "MeatLeft", null));

    [Fact]
    public void Blocks_this_app_knows_nothing_about_survive_a_write()
    {
        string state = "SpawnData<cC>whatever<cB>SomeModBlock<cC>17<cB>";

        string updated = CreatureBlobBuilder.SetStateBlock(state, CreatureBlobBuilder.MeatLeftTag, "2");

        Assert.Contains("SpawnData<cC>whatever<cB>", updated);
        Assert.Contains("SomeModBlock<cC>17<cB>", updated);
        Assert.Contains("MeatLeft<cC>2<cB>", updated);
    }

    // ---- social memory ----

    [Fact]
    public void What_a_real_creature_thinks_of_the_player_is_read_off_it()
    {
        string state = CreatureBlobBuilder.Parse(LizardBlob)!.State;

        var relation = CreatureBlobBuilder.ReadRelations(state)
            .Single(r => r.SubjectId == CreatureBlobBuilder.PlayerEntityId);

        Assert.Equal(1f, relation.Like);
        Assert.Equal(0.925f, relation.Know);
        Assert.Null(relation.Fear);
    }

    [Fact]
    public void Every_relationship_a_real_creature_carries_is_read_in_stored_order()
    {
        var relations = CreatureBlobBuilder.ReadRelations(CreatureBlobBuilder.Parse(LizardBlob)!.State);

        Assert.Equal(
            new[] { "ID.-1.0", "ID.9033.7215" },
            relations.Select(r => r.SubjectId));
    }

    [Fact]
    public void Setting_what_a_creature_feels_leaves_its_other_relationships_alone()
    {
        string state = CreatureBlobBuilder.Parse(LizardBlob)!.State;

        string updated = CreatureBlobBuilder.SetRelation(
            state,
            CreatureBlobBuilder.PlayerEntityId,
            like: 0.5f,
            fear: null,
            know: 1f);

        var relations = CreatureBlobBuilder.ReadRelations(updated);

        Assert.Equal(0.5f, relations.Single(r => r.SubjectId == "ID.-1.0").Like);
        Assert.Equal(0.0045f, relations.Single(r => r.SubjectId == "ID.9033.7215").Like);
    }

    [Fact]
    public void A_creature_that_has_never_met_the_player_can_be_given_feelings()
    {
        string state = CreatureBlobBuilder.SetRelation("", CreatureBlobBuilder.PlayerEntityId, 1f, null, 1f);

        Assert.Equal("Social<cC>REL<rA>ID.-1.0<rA>L<rB>1<rA>K<rB>1<cB>", state);
    }

    /// <summary>
    /// The game's own rule, kept rather than worked around: Relationship.ToString writes nothing
    /// when like and fear are both zero, so a creature set to feel nothing carries no entry, and
    /// what it knew goes with it.
    /// </summary>
    [Fact]
    public void Setting_a_creature_to_feel_nothing_takes_the_whole_entry_out()
    {
        string state = CreatureBlobBuilder.SetRelation("", CreatureBlobBuilder.PlayerEntityId, 1f, null, 1f);

        string cleared = CreatureBlobBuilder.SetRelation(
            state,
            CreatureBlobBuilder.PlayerEntityId,
            like: 0f,
            fear: 0f,
            know: 1f);

        Assert.Equal("", cleared);
        Assert.Empty(CreatureBlobBuilder.ReadRelations(cleared));
    }

    [Fact]
    public void Fear_alone_is_enough_to_keep_a_relationship_written()
    {
        string state = CreatureBlobBuilder.SetRelation("", "ID.4.9", like: 0f, fear: -0.5f, know: 0.25f);

        Assert.Equal("Social<cC>REL<rA>ID.4.9<rA>F<rB>-0.5<rA>K<rB>0.25<cB>", state);
    }

    [Fact]
    public void Relationships_are_written_where_the_reader_finds_them_again()
    {
        string state = CreatureBlobBuilder.SetRelation("", "ID.4.9", 1f, null, null);
        state = CreatureBlobBuilder.SetRelation(state, "ID.5.10", -1f, 0.5f, null);

        string blob = CreatureBlobBuilder.Build("PinkLizard", "ID.-1.4", "HI_S03", state: state);

        Assert.Equal(
            new[] { "ID.4.9", "ID.5.10" },
            EntityBlobReader.ReadSocial(blob).Select(r => r.SubjectId));
    }

    [Fact]
    public void Writing_social_memory_leaves_the_other_state_blocks_where_they_were()
    {
        string state = "MeatLeft<cC>3<cB>SomeModBlock<cC>17<cB>";

        string updated = CreatureBlobBuilder.SetRelation(state, "ID.4.9", 1f, null, null);

        Assert.StartsWith("MeatLeft<cC>3<cB>SomeModBlock<cC>17<cB>", updated);
        Assert.Contains("Social<cC>REL<rA>ID.4.9<rA>L<rB>1<cB>", updated);
    }
}
