using RainWorldSaveManager.Core.Editing;
using RainWorldSaveManager.Core.Saves;
using RainWorldSaveManager.Core.Saves.Models;

namespace RainWorldSaveManager.Tests;

/// <summary>
/// Echoes, gates and karma all live inside one field value with its own grammar. Every test here
/// reads the result back with the production reader rather than comparing strings, and the ones
/// that matter most run against the blob out of a real save, unknown fields and all.
/// </summary>
public class DeathPersistentEditorTests
{
    /// <summary>The DEATHPERSISTENTSAVEDATA value of the first campaign in a real slot.</summary>
    private static string RealBlob()
    {
        var payload = FixtureFiles.ReadPayload(FixtureFiles.Sav2, "save");

        foreach (var record in SavePayloadReader.SplitRecords(payload))
        {
            if (record.Header != "SAVE STATE")
            {
                continue;
            }

            foreach (var field in SavePayloadReader.SplitFields(record.Body))
            {
                if (field.Key == "DEATHPERSISTENTSAVEDATA" && field.Value is { } value)
                {
                    return value;
                }
            }
        }

        throw new InvalidOperationException("The fixture has no death persistent blob to edit.");
    }

    [Fact]
    public void Setting_karma_leaves_every_other_field_of_a_real_blob_alone()
    {
        var blob = RealBlob();
        var before = DeathPersistentReader.Read(blob);

        var after = DeathPersistentReader.Read(DeathPersistentEditor.SetInt(blob, DeathPersistentEditor.KarmaField, 9));

        Assert.Equal(9, after.Karma);
        PropertyComparison.AssertSameExcept(before, after, nameof(DeathPersistentData.Karma));
    }

    [Fact]
    public void Setting_a_value_back_to_what_it_was_returns_the_original_characters()
    {
        var blob = RealBlob();
        var karma = DeathPersistentReader.Read(blob).Karma!.Value;

        var round = DeathPersistentEditor.SetInt(
            DeathPersistentEditor.SetInt(blob, DeathPersistentEditor.KarmaField, 9),
            DeathPersistentEditor.KarmaField,
            karma);

        Assert.Equal(blob, round);
    }

    [Fact]
    public void An_unknown_field_survives_an_edit_untouched()
    {
        // A mod writes a field this app has never heard of. Rebuilding the blob would drop it.
        var blob = "KARMA<dpB>3<dpA>SOMEMODFIELD<dpB>keep me<dpA>KARMACAP<dpB>4";

        var after = DeathPersistentEditor.SetInt(blob, DeathPersistentEditor.KarmaField, 8);

        Assert.Equal("KARMA<dpB>8<dpA>SOMEMODFIELD<dpB>keep me<dpA>KARMACAP<dpB>4", after);
    }

    [Theory]
    [InlineData(DeathPersistentEditor.EchoSensed)]
    [InlineData(DeathPersistentEditor.EchoTalkedTo)]
    public void An_echo_can_be_set_on_a_campaign_that_has_never_met_one(int state)
    {
        var after = DeathPersistentReader.Read(DeathPersistentEditor.SetEcho("KARMA<dpB>3", "SH", state));

        var echo = Assert.Single(after.Echoes);
        Assert.Equal("SH", echo.RegionCode);
        Assert.Equal(state, echo.State);
    }

    [Fact]
    public void An_echo_moves_from_sensed_to_talked_to_without_being_listed_twice()
    {
        var blob = DeathPersistentEditor.SetEcho("", "SH", DeathPersistentEditor.EchoSensed);

        var after = DeathPersistentReader.Read(
            DeathPersistentEditor.SetEcho(blob, "SH", DeathPersistentEditor.EchoTalkedTo));

        var echo = Assert.Single(after.Echoes);
        Assert.Equal(DeathPersistentEditor.EchoTalkedTo, echo.State);
    }

    [Fact]
    public void Setting_an_echo_to_never_seen_takes_it_out_of_the_list()
    {
        var blob = "KARMA<dpB>3<dpA>GHOSTS<dpB>SH:1,UW:2,CC:1";

        var after = DeathPersistentReader.Read(
            DeathPersistentEditor.SetEcho(blob, "UW", DeathPersistentEditor.EchoNeverSeen));

        Assert.Equal(new[] { "SH", "CC" }, after.Echoes.Select(e => e.RegionCode));
    }

    [Fact]
    public void Clearing_the_last_echo_removes_the_field_rather_than_leaving_it_empty()
    {
        var blob = "KARMA<dpB>3<dpA>GHOSTS<dpB>SH:1";

        var after = DeathPersistentEditor.SetEcho(blob, "SH", DeathPersistentEditor.EchoNeverSeen);

        // A save that has met no echoes has no GHOSTS field, not an empty one.
        Assert.Equal("KARMA<dpB>3", after);
        Assert.Empty(DeathPersistentReader.Read(after).Echoes);
    }

    [Fact]
    public void The_order_the_game_wrote_the_echoes_in_is_kept()
    {
        var blob = "GHOSTS<dpB>UW:2,SH:1,CC:1";

        var after = DeathPersistentEditor.SetEcho(blob, "SH", DeathPersistentEditor.EchoTalkedTo);

        Assert.Equal("GHOSTS<dpB>UW:2,SH:2,CC:1", after);
    }

    [Fact]
    public void Echoes_of_a_real_save_can_be_changed_without_disturbing_its_gates()
    {
        var blob = RealBlob();
        var before = DeathPersistentReader.Read(blob);

        var after = DeathPersistentReader.Read(
            DeathPersistentEditor.SetEcho(blob, "SB", DeathPersistentEditor.EchoTalkedTo));

        Assert.Equal(before.UnlockedGates, after.UnlockedGates);
        Assert.Contains(after.Echoes, e => e.RegionCode == "SB" && e.State == DeathPersistentEditor.EchoTalkedTo);
    }

    [Fact]
    public void A_gate_can_be_opened_and_closed_again()
    {
        var blob = "KARMA<dpB>3";

        var opened = DeathPersistentEditor.SetGate(blob, "GATE_SU_HI", true);
        Assert.Equal(new[] { "GATE_SU_HI" }, DeathPersistentReader.Read(opened).UnlockedGates);

        var closed = DeathPersistentEditor.SetGate(opened, "GATE_SU_HI", false);
        Assert.Equal(blob, closed);
    }

    [Fact]
    public void Opening_a_gate_that_is_already_open_changes_nothing()
    {
        var blob = "UNLOCKEDGATES<dpB>GATE_SU_HI<dpC>GATE_HI_CC";

        Assert.Equal(blob, DeathPersistentEditor.SetGate(blob, "GATE_SU_HI", true));
    }

    [Fact]
    public void Gates_are_joined_with_the_separator_the_reader_expects()
    {
        var after = DeathPersistentEditor.SetGates("", new[] { "GATE_SU_HI", "GATE_HI_CC" });

        Assert.Equal("UNLOCKEDGATES<dpB>GATE_SU_HI<dpC>GATE_HI_CC", after);
        Assert.Equal(new[] { "GATE_SU_HI", "GATE_HI_CC" }, DeathPersistentReader.Read(after).UnlockedGates);
    }

    [Fact]
    public void Closing_the_last_gate_removes_the_field()
    {
        var blob = "KARMA<dpB>3<dpA>UNLOCKEDGATES<dpB>GATE_SU_HI";

        Assert.Equal("KARMA<dpB>3", DeathPersistentEditor.SetGate(blob, "GATE_SU_HI", false));
    }

    [Fact]
    public void Gates_of_a_real_save_keep_their_order_when_one_is_added()
    {
        var blob = RealBlob();
        var before = DeathPersistentReader.Read(blob).UnlockedGates;

        var after = DeathPersistentReader.Read(DeathPersistentEditor.SetGate(blob, "GATE_TEST_ONE", true));

        Assert.Equal(before.Concat(new[] { "GATE_TEST_ONE" }), after.UnlockedGates);
    }

    [Fact]
    public void A_flag_can_be_set_and_cleared()
    {
        var withMark = DeathPersistentEditor.SetFlag("KARMA<dpB>3", DeathPersistentEditor.HasTheMarkField, true);
        Assert.True(DeathPersistentReader.Read(withMark).HasTheMark);

        var without = DeathPersistentEditor.SetFlag(withMark, DeathPersistentEditor.HasTheMarkField, false);
        Assert.False(DeathPersistentReader.Read(without).HasTheMark);
        Assert.Equal("KARMA<dpB>3", without);
    }

    [Fact]
    public void Editing_an_empty_blob_produces_one_the_reader_understands()
    {
        var blob = DeathPersistentEditor.SetInt("", DeathPersistentEditor.KarmaField, 5);

        Assert.Equal("KARMA<dpB>5", blob);
        Assert.Equal(5, DeathPersistentReader.Read(blob).Karma);
    }
}
