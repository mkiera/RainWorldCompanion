using RainWorldSaveManager.Core.Saves;
using RainWorldSaveManager.Core.Saves.Models;

namespace RainWorldSaveManager.Tests;

/// <summary>
/// DEATHPERSISTENTSAVEDATA is one field holding a second delimiter format inside it, and it is
/// where karma, the death counters, the echoes, the gates and the passages all live. These run
/// synthetic blobs through the extractor so every documented key is covered, including the ones
/// no fixture on this machine happens to carry.
/// </summary>
public class DeathPersistentReaderTests
{
    [Fact]
    public void A_blob_with_every_documented_key_yields_the_values_the_card_shows()
    {
        var campaign = Read(EveryDocumentedKey);

        Assert.Equal(7, campaign.Karma);
        Assert.Equal(9, campaign.KarmaCap);
        Assert.Equal(1, campaign.ReinforcedKarma);
        Assert.True(campaign.HasTheMark);
        Assert.True(campaign.Ascended);
        Assert.True(campaign.RedsDeathStored);
        Assert.Equal(206, campaign.Deaths);
        Assert.Equal(77, campaign.Survives);
        Assert.Equal(11, campaign.Quits);
    }

    [Fact]
    public void The_keys_with_no_place_on_the_card_do_not_disturb_the_ones_that_have_one()
    {
        // DEATHTIME, TIPS, TIPSEED, FRIENDSAVEBONUS, TUTMESSAGES, METERSSHOWN, SONGSPLAYRECORDS,
        // SESSIONRECORDS, CONSUMEDFLOWERS, DEATHPOSS, CHATLOGS and PREPEBCHATLOGS are all in the
        // blob below. Nothing reads them, and nothing may be knocked out by them either.
        var campaign = Read(EveryDocumentedKey);

        Assert.Equal(6, campaign.Echoes.Count);
        Assert.Equal(3, campaign.UnlockedGates.Count);
        Assert.NotEmpty(campaign.Passages);
    }

    [Fact]
    public void Karma_above_the_cap_is_reported_as_stored()
    {
        // Real data: the Monk save on this machine has KARMA 10 against KARMACAP 9. Clamping it
        // would show the player a karma they do not have.
        var campaign = Read(
            CampaignFixture.DeathEntry("KARMA", "10"),
            CampaignFixture.DeathEntry("KARMACAP", "9"));

        Assert.Equal(10, campaign.Karma);
        Assert.Equal(9, campaign.KarmaCap);
    }

    [Fact]
    public void Karma_is_reported_as_the_raw_stored_number()
    {
        var campaign = Read(CampaignFixture.DeathEntry("KARMA", "0"));

        Assert.Equal(0, campaign.Karma);
    }

    [Fact]
    public void Echoes_split_into_a_region_code_and_an_encounter_state()
    {
        var campaign = Read(CampaignFixture.DeathEntry("GHOSTS", "SH:1,UW:2,CC:2,SI:1,LF:1,SB:2"));

        Assert.Equal(6, campaign.Echoes.Count);
        Assert.Equal(
            new[] { "SH", "UW", "CC", "SI", "LF", "SB" },
            campaign.Echoes.Select(e => e.RegionCode).ToArray());

        // 1 is a hunch and 2 is an echo the player has spoken to. GhostHunch.Update stores the
        // one and SaveState.GhostEncounter stores the other, both as constants, so no entry can
        // ever hold anything else and none of them is a repeat count.
        Assert.Equal(EchoRecord.Hunch, Assert.Single(campaign.Echoes, e => e.RegionCode == "SH").State);
        Assert.Equal(EchoRecord.TalkedTo, Assert.Single(campaign.Echoes, e => e.RegionCode == "UW").State);
    }

    [Fact]
    public void An_empty_ghosts_value_means_no_echoes()
    {
        var campaign = Read(CampaignFixture.DeathEntry("GHOSTS", ""));

        Assert.Empty(campaign.Echoes);
    }

    [Fact]
    public void A_broken_echo_entry_does_not_take_the_sound_ones_with_it()
    {
        var campaign = Read(CampaignFixture.DeathEntry("GHOSTS", "SH:1,,BROKEN,UW:later,CC:2"));

        Assert.Contains(campaign.Echoes, e => e.RegionCode == "SH" && e.State == 1);
        Assert.Contains(campaign.Echoes, e => e.RegionCode == "CC" && e.State == 2);
    }

    [Fact]
    public void Unlocked_gates_split_on_their_own_separator()
    {
        var campaign = Read(CampaignFixture.DeathEntry(
            "UNLOCKEDGATES",
            "GATE_SU_HI" + CampaignFixture.DeathListSeparator +
            "GATE_HI_CC" + CampaignFixture.DeathListSeparator +
            "GATE_CC_UW"));

        Assert.Equal(new[] { "GATE_SU_HI", "GATE_HI_CC", "GATE_CC_UW" }, campaign.UnlockedGates.ToArray());
    }

    [Fact]
    public void Passages_report_whether_each_one_has_been_spent()
    {
        var campaign = Read(CampaignFixture.DeathEntry(
            "WINSTATE",
            "Survivor<egA>1<egA>5<wsA>Traveller<egA>0<egA>0.0.0.1.0<wsA>Saint<egA>1<egA>3<wsA>"));

        // The middle part is WinState.EndgameTracker.consumed, so a 1 means the passage has been
        // used to travel and the game no longer offers it.
        var survivor = Assert.Single(campaign.Passages, p => p.Name == "Survivor");
        Assert.True(survivor.Consumed);
        Assert.Equal(5, survivor.Goal.Done);

        var saint = Assert.Single(campaign.Passages, p => p.Name == "Saint");
        Assert.True(saint.Consumed);
        Assert.Equal(3, saint.Goal.Done);

        // The third part is a flag array rather than a number on this one, which is the shape
        // every real save stores Traveller in. The entry still has to be listed.
        var traveller = Assert.Single(campaign.Passages, p => p.Name == "Traveller");
        Assert.False(traveller.Consumed);
        Assert.Equal(1, traveller.Goal.Done);
        Assert.Equal(5, traveller.Goal.Needed);
    }

    /// <summary>
    /// The passage tracker is not always an int. The live sav stores a float for Rivulet's
    /// Scholar and a dotted string for Spearmaster's. The raw text is kept whatever the shape, so
    /// a passage with real progress is never drawn like one with none.
    /// </summary>
    [Theory]
    [InlineData("30.29")]
    [InlineData("25.18.20")]
    [InlineData("0.65")]
    [InlineData("0.01875")]
    [InlineData("1.1.1.1.1.1.1.1.1.1.")]
    public void A_passage_tracker_that_is_not_an_integer_is_kept_as_the_save_wrote_it(string stored)
    {
        var passage = Assert.Single(
            DeathPersistentReader.ParseWinState("Scholar<egA>0<egA>" + stored));

        Assert.Equal("Scholar", passage.Name);
        Assert.Equal(stored, passage.Progress);
    }

    [Fact]
    public void A_tracker_reaches_the_record_as_the_stored_text()
    {
        var passage = Assert.Single(DeathPersistentReader.ParseWinState("Scholar<egA>0<egA>17"));

        Assert.Equal("17", passage.Progress);
    }

    [Fact]
    public void A_passage_entry_with_no_tracker_at_all_has_an_empty_stored_text()
    {
        var passage = Assert.Single(DeathPersistentReader.ParseWinState("Scholar<egA>1"));

        Assert.True(passage.Consumed);
        Assert.Equal("", passage.Progress);
        Assert.Null(passage.Goal.Done);
        Assert.Null(passage.Goal.Fulfilled);
    }

    [Fact]
    public void Progress_is_never_null_even_when_a_manifest_says_it_is()
    {
        var passage = new PassageRecord("Scholar", false) { Progress = null! };

        Assert.Equal("", passage.Progress);
    }

    [Fact]
    public void A_malformed_blob_leaves_the_broken_fields_null_and_keeps_the_rest()
    {
        var campaign = Read(
            CampaignFixture.DeathEntry("KARMA", "quite a lot"),
            "",
            "NOSEPARATORHERE",
            "DEATHS<dpB",
            CampaignFixture.DeathEntry("SURVIVES", ""),
            CampaignFixture.DeathEntry("KARMACAP", "9"),
            CampaignFixture.DeathEntry("QUITS", "11"));

        Assert.Null(campaign.Karma);
        Assert.Null(campaign.Deaths);
        Assert.Null(campaign.Survives);
        Assert.Equal(9, campaign.KarmaCap);
        Assert.Equal(11, campaign.Quits);
    }

    [Fact]
    public void An_empty_blob_leaves_every_field_null_and_every_collection_empty()
    {
        var campaign = CampaignFixture.Campaign(
            CampaignFixture.Field("SAV STATE NUMBER", "White"),
            CampaignFixture.Field("DEATHPERSISTENTSAVEDATA", ""));

        AssertNothingWasRead(campaign);
    }

    [Fact]
    public void A_record_with_no_death_data_at_all_leaves_every_field_null()
    {
        var campaign = CampaignFixture.Campaign(CampaignFixture.Field("SAV STATE NUMBER", "White"));

        AssertNothingWasRead(campaign);
    }

    [Fact]
    public void A_blob_of_nothing_but_separators_reads_as_empty()
    {
        var campaign = CampaignFixture.Campaign(
            CampaignFixture.Field("SAV STATE NUMBER", "White"),
            CampaignFixture.Field("DEATHPERSISTENTSAVEDATA", "<dpA><dpA><dpA>"));

        AssertNothingWasRead(campaign);
    }

    [Fact]
    public void A_blob_of_bare_flags_sets_the_flags_and_nothing_else()
    {
        var campaign = Read("HASTHEMARK", "ASCENDED", "REDSDEATH");

        Assert.True(campaign.HasTheMark);
        Assert.True(campaign.Ascended);
        Assert.True(campaign.RedsDeathStored);
        Assert.Null(campaign.Karma);
        Assert.Null(campaign.KarmaCap);
        Assert.Null(campaign.Deaths);
        Assert.Empty(campaign.Echoes);
        Assert.Empty(campaign.Passages);
    }

    [Fact]
    public void An_absent_flag_is_false_rather_than_unknown()
    {
        var campaign = Read(CampaignFixture.DeathEntry("KARMA", "7"));

        Assert.False(campaign.HasTheMark);
        Assert.False(campaign.Ascended);
        Assert.False(campaign.RedsDeathStored);
    }

    /// <summary>Every key the format is documented to carry, with the real values measured off this machine.</summary>
    private static readonly string[] EveryDocumentedKey =
    {
        "HASTHEMARK",
        "ASCENDED",
        "REDSDEATH",
        CampaignFixture.DeathEntry("KARMA", "7"),
        CampaignFixture.DeathEntry("KARMACAP", "9"),
        CampaignFixture.DeathEntry("REINFORCEDKARMA", "1"),
        CampaignFixture.DeathEntry("DEATHS", "206"),
        CampaignFixture.DeathEntry("SURVIVES", "77"),
        CampaignFixture.DeathEntry("QUITS", "11"),
        CampaignFixture.DeathEntry("DEATHTIME", "13"),
        CampaignFixture.DeathEntry("TIPS", "0"),
        CampaignFixture.DeathEntry("TIPSEED", "87"),
        CampaignFixture.DeathEntry("FRIENDSAVEBONUS", "4"),
        CampaignFixture.DeathEntry("GHOSTS", "SH:1,UW:2,CC:2,SI:1,LF:1,SB:2"),
        CampaignFixture.DeathEntry(
            "UNLOCKEDGATES",
            "GATE_SU_HI" + CampaignFixture.DeathListSeparator +
            "GATE_HI_CC" + CampaignFixture.DeathListSeparator +
            "GATE_CC_UW"),
        CampaignFixture.DeathEntry(
            "WINSTATE",
            "Survivor<egA>1<egA>5<wsA>Traveller<egA>0<egA>0.0.0.1.0<wsA>Saint<egA>1<egA>3<wsA>"),
        CampaignFixture.DeathEntry("METERSSHOWN", "Survivor,Traveller,Chieftain,Monk,Saint"),
        CampaignFixture.DeathEntry("TUTMESSAGES", "GoExplore,KarmaFlower"),
        CampaignFixture.DeathEntry("SONGSPLAYRECORDS", "NA_40 - Unseen Lands<dpD>1<dpC>NA_30 - Distance<dpD>4"),
        CampaignFixture.DeathEntry("SESSIONRECORDS", "10<dpC>10<dpC>00<dpC>10"),
        CampaignFixture.DeathEntry("CONSUMEDFLOWERS", "SU_A39.0.5"),
        CampaignFixture.DeathEntry("DEATHPOSS", "SU_A37.29.-2.0"),
        CampaignFixture.DeathEntry("CHATLOGS", ""),
        CampaignFixture.DeathEntry("PREPEBCHATLOGS", ""),
    };

    private static void AssertNothingWasRead(CampaignSummary campaign)
    {
        Assert.Null(campaign.Karma);
        Assert.Null(campaign.KarmaCap);
        Assert.Null(campaign.ReinforcedKarma);
        Assert.Null(campaign.Deaths);
        Assert.Null(campaign.Survives);
        Assert.Null(campaign.Quits);
        Assert.False(campaign.HasTheMark);
        Assert.False(campaign.Ascended);
        Assert.False(campaign.RedsDeathStored);
        Assert.Empty(campaign.Echoes);
        Assert.Empty(campaign.UnlockedGates);
        Assert.Empty(campaign.Passages);
    }

    /// <summary>Runs a death persistent blob through the extractor inside an otherwise bare record.</summary>
    private static CampaignSummary Read(params string[] entries)
        => CampaignFixture.Campaign(
            CampaignFixture.Field("SAV STATE NUMBER", "White"),
            CampaignFixture.DeathData(entries));
}
