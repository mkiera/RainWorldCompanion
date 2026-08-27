using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.Core.Saves.Models;

namespace RainWorldCompanion.Tests;

/// <summary>
/// Every number on it comes out of one SAVE STATE record, so these run the real fixtures and
/// assert the values that are actually stored in them.
/// </summary>
public class CampaignDetailTests
{
    [Fact]
    public void Sav2_reports_the_progress_counters_recorded_in_the_file()
    {
        var campaign = OnlyCampaign(FixtureFiles.Sav2, 2);

        Assert.Equal("White", campaign.SlugcatId);
        Assert.Equal("White", campaign.Timeline);
        Assert.Equal("SU_S04", campaign.LastDenPos);
        Assert.Equal(17, campaign.CycleNum);
        Assert.Equal(17, campaign.CyclesThisVersion);
        Assert.Equal(46, campaign.TotalFoodEaten);
        Assert.Equal(TimeSpan.FromSeconds(2627), campaign.PlayTime);
    }

    [Fact]
    public void Sav2_reports_the_karma_and_death_counters_from_its_death_persistent_data()
    {
        var campaign = OnlyCampaign(FixtureFiles.Sav2, 2);

        Assert.Equal(6, campaign.Karma);
        Assert.Equal(9, campaign.KarmaCap);
        Assert.Equal(0, campaign.ReinforcedKarma);
        Assert.Equal(2, campaign.Deaths);
        Assert.Equal(26, campaign.Survives);
        Assert.Equal(87, campaign.Quits);
    }

    [Fact]
    public void Sav2_reads_its_stored_karma_of_six_as_seven_of_ten()
    {
        var campaign = OnlyCampaign(FixtureFiles.Sav2, 2);

        // Stored karma is a 0-based sprite index and it is inside 0..cap here, so the game loads
        // it unchanged and the meter reads one higher than both numbers.
        Assert.Equal(6, campaign.EffectiveKarma);
        Assert.Equal(7, campaign.DisplayKarma);
        Assert.Equal(10, campaign.DisplayKarmaCap);
        Assert.False(campaign.KarmaStoredOutOfRange);
        Assert.Equal("7 / 10", campaign.KarmaText);
    }

    [Fact]
    public void Sav2_carries_the_bare_redsdeath_flag_and_none_of_the_others()
    {
        var campaign = OnlyCampaign(FixtureFiles.Sav2, 2);

        Assert.True(campaign.RedsDeathStored);

        // The token is on disk but the game does not keep it. SaveState.LoadGame clears redsDeath
        // while the cycle number is under the limit, and this campaign is on cycle 17 of 19.
        Assert.False(campaign.EffectiveRedsDeath);
        Assert.False(campaign.RedExtraCycles);

        Assert.False(campaign.HasTheMark);
        Assert.False(campaign.Ascended);
        Assert.False(campaign.HasRobo);
        Assert.False(campaign.JustBeatGame);
        Assert.False(campaign.HasGlow);
    }

    [Fact]
    public void Sav2_reports_its_one_kill_type_with_the_creature_name_split_off_the_id()
    {
        var campaign = OnlyCampaign(FixtureFiles.Sav2, 2);

        var kill = Assert.Single(campaign.Kills);
        Assert.Equal("Fly-Creature-0", kill.CreatureId);
        Assert.Equal("Fly", kill.DisplayName);
        Assert.Equal(3, kill.Count);
        Assert.Equal(3, campaign.TotalKills);
    }

    [Fact]
    public void Sav2_reports_the_two_bones_held_in_hand()
    {
        var campaign = OnlyCampaign(FixtureFiles.Sav2, 2);

        Assert.Equal(new[] { "Bone", "Bone" }, campaign.HeldItems.ToArray());
    }

    [Fact]
    public void Sav2_has_seen_passages_it_has_not_earned()
    {
        var campaign = OnlyCampaign(FixtureFiles.Sav2, 2);

        // WINSTATE stores every passage the run knows about with a consumed flag of 0 or 1. This
        // save has spent none of them, and the third part is the progress towards each. The
        // negatives are WinState.DeathModifyTracker subtracting on death.
        Assert.All(campaign.Passages, passage => Assert.False(passage.Consumed));
        Assert.Equal(1, PassageProgress(campaign, "Survivor"));
        Assert.Equal(-14, PassageProgress(campaign, "Hunter"));
        Assert.Equal(-14, PassageProgress(campaign, "Saint"));
        Assert.Equal(-14, PassageProgress(campaign, "Monk"));

        // None of them has reached its requirement, so the card offers none of them.
        Assert.All(campaign.Passages, passage => Assert.NotEqual(true, passage.Goal.Fulfilled));
    }

    [Fact]
    public void Sav2_has_no_echoes_no_gates_and_nothing_swallowed()
    {
        var campaign = OnlyCampaign(FixtureFiles.Sav2, 2);

        // GHOSTS is present but empty and there is no UNLOCKEDGATES field at all. Both have to
        // come back as empty lists rather than null, because the UI enumerates them unguarded.
        Assert.Empty(campaign.Echoes);
        Assert.Empty(campaign.UnlockedGates);
        Assert.Empty(campaign.SwallowedItems);
        Assert.Empty(campaign.DevourmentStates);
        Assert.Equal(0, campaign.DevourmentStateCount);
    }

    [Fact]
    public void Sav3_reports_its_own_counters_and_karma()
    {
        var campaign = OnlyCampaign(FixtureFiles.Sav3, 3);

        Assert.Equal(9, campaign.CycleNum);
        Assert.Equal(9, campaign.CyclesThisVersion);
        Assert.Equal(31, campaign.TotalFoodEaten);
        Assert.Equal(TimeSpan.FromSeconds(2794), campaign.PlayTime);
        Assert.Equal(3, campaign.Karma);
        Assert.Equal(4, campaign.KarmaCap);
        Assert.Equal(0, campaign.ReinforcedKarma);
        Assert.Equal(4, campaign.Deaths);
        Assert.Equal(15, campaign.Survives);
        Assert.Equal(50, campaign.Quits);
    }

    [Fact]
    public void Sav3_reads_its_stored_karma_of_three_as_four_of_five()
    {
        var campaign = OnlyCampaign(FixtureFiles.Sav3, 3);

        // KARMA 3 with KARMACAP 4, the cap a run starts on, which the game shows as 5.
        Assert.Equal(3, campaign.Karma);
        Assert.Equal(4, campaign.KarmaCap);
        Assert.Equal(3, campaign.EffectiveKarma);
        Assert.Equal(4, campaign.DisplayKarma);
        Assert.Equal(5, campaign.DisplayKarmaCap);
        Assert.False(campaign.KarmaStoredOutOfRange);
        Assert.Equal("4 / 5", campaign.KarmaText);
    }

    [Fact]
    public void Sav3_parses_all_four_devourment_relationships()
    {
        var campaign = OnlyCampaign(FixtureFiles.Sav3, 3);

        Assert.Equal(4, campaign.DevourmentStateCount);
        Assert.Equal(4, campaign.DevourmentStates.Count);

        // Three of the four are the slugcat holding a green lizard. The fourth is a lizard
        // holding another lizard, so the predator is not always the player.
        Assert.Equal(3, campaign.DevourmentStates.Count(d => d.PredatorType == "Slugcat"));
        Assert.Equal(1, campaign.DevourmentStates.Count(d => d.PredatorType == "GreenLizard"));
        Assert.All(campaign.DevourmentStates, d => Assert.False(d.PreyIsItem));
        Assert.All(campaign.DevourmentStates, d => Assert.Contains(d.Status, BellyStatusNames));

        Assert.Equal(
            new[] { 4, 8, 16, 18 },
            campaign.DevourmentStates.Select(d => d.FoodValue ?? int.MinValue).OrderBy(v => v).ToArray());

        var healing = Assert.Single(campaign.DevourmentStates, d => d.Status == "Healing");
        Assert.Equal("GreenLizard", healing.PredatorType);
        Assert.Equal("PinkLizard", healing.PreyType);
        Assert.Equal(4, healing.FoodValue);
    }

    [Fact]
    public void Sav3_has_no_kills_and_nothing_in_hand()
    {
        var campaign = OnlyCampaign(FixtureFiles.Sav3, 3);

        Assert.Empty(campaign.Kills);
        Assert.Equal(0, campaign.TotalKills);
        Assert.Empty(campaign.HeldItems);
        Assert.Empty(campaign.SwallowedItems);
    }

    [Fact]
    public void The_display_name_is_the_in_game_name_for_the_slugcat_id()
    {
        Assert.Equal("Survivor", OnlyCampaign(FixtureFiles.Sav2, 2).DisplayName);
    }

    [Fact]
    public void An_unknown_slugcat_id_is_its_own_display_name()
    {
        var campaign = CampaignFixture.Campaign(CampaignFixture.Field("SAV STATE NUMBER", "Bubbles"));

        Assert.Equal("Bubbles", campaign.SlugcatId);
        Assert.Equal("Bubbles", campaign.DisplayName);
    }

    [Fact]
    public void The_bare_flags_a_finished_run_carries_are_read_off_the_record()
    {
        var campaign = CampaignFixture.Campaign(
            CampaignFixture.Field("SAV STATE NUMBER", "Artificer"),
            "HASTHEGLOW",
            "HASROBO",
            "JUSTBEATGAME",
            CampaignFixture.DeathData("HASTHEMARK", "ASCENDED"));

        Assert.True(campaign.HasGlow);
        Assert.True(campaign.HasRobo);
        Assert.True(campaign.JustBeatGame);
        Assert.True(campaign.HasTheMark);
        Assert.True(campaign.Ascended);
    }

    [Fact]
    public void A_karma_value_above_the_stored_cap_survives_the_read_and_is_interpreted_on_the_way_out()
    {
        // Two live campaigns store a karma exactly one above their cap. The raw number has to
        // reach the model intact, because that is what a save editor writes back, and the
        // interpretation has to report what the game loads instead.
        var campaign = CampaignFixture.Campaign(
            CampaignFixture.Field("SAV STATE NUMBER", "Yellow"),
            CampaignFixture.DeathData(
                CampaignFixture.DeathEntry("KARMA", "10"),
                CampaignFixture.DeathEntry("KARMACAP", "9"),
                CampaignFixture.DeathEntry("REINFORCEDKARMA", "1")));

        Assert.Equal(10, campaign.Karma);
        Assert.Equal(9, campaign.KarmaCap);
        Assert.Equal(1, campaign.ReinforcedKarma);
        Assert.Equal(9, campaign.EffectiveKarma);
        Assert.Equal(10, campaign.DisplayKarma);
        Assert.Equal(10, campaign.DisplayKarmaCap);
        Assert.True(campaign.KarmaStoredOutOfRange);
        Assert.Equal("10 / 10", campaign.KarmaText);
    }

    [Fact]
    public void The_minus_one_karma_the_ascension_sequence_writes_reads_as_the_lowest_level()
    {
        // VoidSea.VoidWorm.MainWormBehavior.Update sets karma to -1, and Saint reaches disk that
        // way. Printing the raw -1 is what this interpretation exists to stop.
        var campaign = CampaignFixture.Campaign(
            CampaignFixture.Field("SAV STATE NUMBER", "Saint"),
            CampaignFixture.DeathData(
                CampaignFixture.DeathEntry("KARMA", "-1"),
                CampaignFixture.DeathEntry("KARMACAP", "9")));

        Assert.Equal(-1, campaign.Karma);
        Assert.Equal(0, campaign.EffectiveKarma);
        Assert.Equal(1, campaign.DisplayKarma);
        Assert.Equal(10, campaign.DisplayKarmaCap);
        Assert.True(campaign.KarmaStoredOutOfRange);
        Assert.Equal("1 / 10", campaign.KarmaText);
    }

    [Fact]
    public void Swallowed_items_report_the_item_type_out_of_the_serialised_object()
    {
        var campaign = CampaignFixture.Campaign(
            CampaignFixture.Field("SAV STATE NUMBER", "White"),
            CampaignFixture.Field(
                "SWALLOWEDITEMS",
                "ID.-1.16440<oB>0<oA>PebblesPearl<oA>LF_S04.26.18.0<oA>-1<oA>-1<oA>PebblesPearl<oA>0<oA>1"),
            CampaignFixture.Field(
                "SWALLOWEDITEMS",
                "ID.-1.16441<oB>0<oA>Rock<oA>LF_S04.26.18.0<oA>-1<oA>-1<oA>Rock<oA>0<oA>1"));

        Assert.Equal(new[] { "PebblesPearl", "Rock" }, campaign.SwallowedItems.ToArray());
    }

    [Fact]
    public void Kills_split_into_one_record_per_creature_and_sum_to_the_total()
    {
        var campaign = CampaignFixture.Campaign(
            CampaignFixture.Field("SAV STATE NUMBER", "White"),
            CampaignFixture.Field("KILLS", "Snail-Creature-0<svD>1<svC>Fly-Creature-0<svD>3<svC>Vulture<svD>2"));

        Assert.Equal(3, campaign.Kills.Count);
        Assert.Equal(new[] { "Snail", "Fly", "Vulture" }, campaign.Kills.Select(k => k.DisplayName).ToArray());
        Assert.Equal(new[] { "Snail-Creature-0", "Fly-Creature-0", "Vulture" }, campaign.Kills.Select(k => k.CreatureId).ToArray());
        Assert.Equal(6, campaign.TotalKills);
    }

    [Fact]
    public void A_malformed_kill_entry_leaves_the_rest_of_the_list_intact()
    {
        var campaign = CampaignFixture.Campaign(
            CampaignFixture.Field("SAV STATE NUMBER", "White"),
            CampaignFixture.Field("KILLS", "Fly-Creature-0<svD>3<svC><svC>Broken<svC>Snail-Creature-0<svD>notanumber"));

        Assert.Contains(campaign.Kills, k => k.DisplayName == "Fly" && k.Count == 3);
        Assert.Equal(3, campaign.TotalKills);
    }

    [Fact]
    public void A_field_that_is_not_a_number_leaves_that_one_property_null()
    {
        var campaign = CampaignFixture.Campaign(
            CampaignFixture.Field("SAV STATE NUMBER", "White"),
            CampaignFixture.Field("TOTTIME", "later"),
            CampaignFixture.Field("TOTFOOD", ""),
            CampaignFixture.Field("CURRVERCYCLES", "9"));

        Assert.Null(campaign.PlayTime);
        Assert.Null(campaign.TotalFoodEaten);
        Assert.Equal(9, campaign.CyclesThisVersion);
    }

    [Fact]
    public void Playtime_comes_from_tottime_in_seconds()
    {
        var campaign = CampaignFixture.Campaign(
            CampaignFixture.Field("SAV STATE NUMBER", "White"),
            CampaignFixture.Field("TOTTIME", "52132"));

        Assert.Equal(TimeSpan.FromSeconds(52132), campaign.PlayTime);
        Assert.Equal(14, (int)campaign.PlayTime!.Value.TotalHours);
    }

    // A file the reader cannot parse at all is covered by SaveMetadataExtractorTests, which
    // asserts the slot number survives the failure as well.

    [Fact]
    public void A_record_with_nothing_but_a_slugcat_id_leaves_every_collection_empty()
    {
        var campaign = CampaignFixture.Campaign(CampaignFixture.Field("SAV STATE NUMBER", "Rivulet"));

        AssertCollectionsAreEmptyNotNull(campaign);
        Assert.Null(campaign.Karma);
        Assert.Null(campaign.KarmaCap);
        Assert.Null(campaign.ReinforcedKarma);
        Assert.Null(campaign.Deaths);
        Assert.Null(campaign.Survives);
        Assert.Null(campaign.Quits);
        Assert.Null(campaign.PlayTime);
        Assert.Null(campaign.TotalFoodEaten);
        Assert.Null(campaign.CyclesThisVersion);
        Assert.Null(campaign.Timeline);
        Assert.Null(campaign.LastDenPos);
        Assert.Equal(0, campaign.TotalKills);

        // Nothing to interpret, so the card shows a dash rather than a 1 invented out of a null.
        Assert.Null(campaign.EffectiveKarma);
        Assert.Null(campaign.DisplayKarma);
        Assert.Null(campaign.DisplayKarmaCap);
        Assert.False(campaign.KarmaStoredOutOfRange);
        Assert.Equal("-", campaign.KarmaText);
    }

    /// <summary>The five status names the Devourment mod writes.</summary>
    internal static readonly string[] BellyStatusNames =
    {
        "Held", "Digesting", "Healing", "EnergyTheft", "Sedating",
    };

    internal static void AssertCollectionsAreEmptyNotNull(CampaignSummary campaign)
    {
        Assert.NotNull(campaign.Echoes);
        Assert.NotNull(campaign.UnlockedGates);
        Assert.NotNull(campaign.Passages);
        Assert.NotNull(campaign.Kills);
        Assert.NotNull(campaign.DevourmentStates);
        Assert.NotNull(campaign.SwallowedItems);
        Assert.NotNull(campaign.HeldItems);

        Assert.Empty(campaign.Echoes);
        Assert.Empty(campaign.UnlockedGates);
        Assert.Empty(campaign.Passages);
        Assert.Empty(campaign.Kills);
        Assert.Empty(campaign.DevourmentStates);
        Assert.Empty(campaign.SwallowedItems);
        Assert.Empty(campaign.HeldItems);
    }

    private static int? PassageProgress(CampaignSummary campaign, string name)
        => Assert.Single(campaign.Passages, p => p.Name == name).Goal.Done;

    private static CampaignSummary OnlyCampaign(string fixtureName, int slot)
    {
        var metadata = SaveMetadataExtractor.Extract(FixtureFiles.PathTo(fixtureName), slot);
        Assert.Null(metadata.ParseError);
        return Assert.Single(metadata.Campaigns);
    }
}

/// <summary>
/// Builds a save container around a chosen set of SAVE STATE fields and runs the real extractor
/// over it. Going through <see cref="SaveMetadataExtractor"/> rather than calling a sub-reader
/// directly keeps these tests on the published surface, and still exercises every sub-reader.
/// </summary>
internal static class CampaignFixture
{
    public const string ValueSeparator = "<svB>";
    public const string DeathFieldSeparator = "<dpA>";
    public const string DeathValueSeparator = "<dpB>";
    public const string DeathListSeparator = "<dpC>";
    public const string DevourmentSeparator = "<dvD>";

    /// <summary>"KEY&lt;svB&gt;VALUE". Pass the key alone for a bare flag.</summary>
    public static string Field(string key, string value) => key + ValueSeparator + value;

    public static string DeathData(params string[] entries)
        => Field("DEATHPERSISTENTSAVEDATA", string.Join(DeathFieldSeparator, entries) + DeathFieldSeparator);

    /// <summary>"KEY&lt;dpB&gt;VALUE". Pass the key alone for a bare flag.</summary>
    public static string DeathEntry(string key, string value) => key + DeathValueSeparator + value;

    /// <summary>A serialised creature, whose type is the text before the first &lt;cA&gt;.</summary>
    public static string Creature(string type, string id)
        => type + "<cA>" + id + "<cB>0<cA>SU_S04.0<cA>";

    /// <summary>A serialised item, whose type is the element at index 1 after splitting on &lt;oA&gt;.</summary>
    public static string Item(string type, string id)
        => id + "<oB>0<oA>" + type + "<oA>WRFA_S01.22.4.0";

    public static string Devourment(string predator, string prey, string status, string foodValue)
        => Field("DEVOURMENTSTATE", string.Join(DevourmentSeparator, predator, prey, status, foodValue));

    public static SlotMetadata Extract(params string[] fields)
    {
        using var temp = new TempDirectory("campaign");
        var body = string.Join(SyntheticSave.FieldSeparator, fields);
        var payload = SyntheticSave.Progression(new[] { ("SAVE STATE", body) });
        var path = temp.WriteBytes("sav", SyntheticSave.SaveFile(payload));

        return SaveMetadataExtractor.Extract(path, 1);
    }

    public static CampaignSummary Campaign(params string[] fields)
    {
        var metadata = Extract(fields);
        Assert.Null(metadata.ParseError);
        return Assert.Single(metadata.Campaigns);
    }
}
