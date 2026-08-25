using RainWorldCompanion.Core.Editing;
using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Tests;

/// <summary>
/// Moving one campaign between payloads.
///
/// The identity tests are the ones that matter. A payload is a megabyte of records this app does not
/// read, and taking a campaign out and putting it back has to give the same characters back, or
/// something in between them was rewritten by accident.
/// </summary>
public class CampaignSplicerTests
{
    private const string Separator = SyntheticSave.RecordSeparator;

    // ---- taking one out ----

    [Fact]
    public void A_campaign_comes_out_with_the_map_of_the_slugcat_it_belongs_to()
    {
        string payload = FixtureFiles.ReadPayload(FixtureFiles.Sav2, "save");

        CampaignSlice slice = CampaignSplicer.Extract(payload, "White")!;

        Assert.Equal("White", slice.SlugcatId);
        Assert.StartsWith("SAVE STATE" + SyntheticSave.HeaderSeparator, slice.SaveStateRecord, StringComparison.Ordinal);
        Assert.Equal(7, slice.MapRecords.Count);
        Assert.All(slice.MapRecords, record => Assert.Equal("White", CampaignSplicer.MapOwnerOf(record)));
    }

    /// <summary>
    /// sav3 carries three MAP_Watcher records and no Watcher campaign, which is what a slot looks
    /// like after the game has wiped one: WipeSaveState drops the SAVE STATE and keeps the maps.
    /// </summary>
    [Fact]
    public void Map_records_left_behind_by_a_wiped_campaign_are_not_a_campaign()
    {
        string payload = FixtureFiles.ReadPayload(FixtureFiles.Sav3, "save");

        Assert.Contains(
            SavePayloadReader.SplitRecords(payload),
            record => record.Header == "MAP_Watcher");

        Assert.Null(CampaignSplicer.Extract(payload, "Watcher"));
        Assert.False(CampaignSplicer.Contains(payload, "Watcher"));
    }

    [Fact]
    public void The_campaigns_of_a_payload_are_listed_in_the_order_it_stores_them()
    {
        string payload = SyntheticSave.Progression(new[]
        {
            ("MAP_Gourmand", "SU<progDivB>x"),
            ("SAVE STATE", SyntheticSave.SaveStateBody("Gourmand")),
            ("MISCPROG", "CYCLES<misA>1"),
            ("SAVE STATE", SyntheticSave.SaveStateBody("White")),
        });

        Assert.Equal(new[] { "Gourmand", "White" }, CampaignSplicer.Campaigns(payload));
    }

    // ---- putting it back ----

    [Theory]
    [InlineData(FixtureFiles.Sav2)]
    [InlineData(FixtureFiles.Sav3)]
    public void A_campaign_put_back_where_it_came_from_leaves_the_payload_character_for_character(string fixture)
    {
        string payload = FixtureFiles.ReadPayload(fixture, "save");
        CampaignSlice slice = CampaignSplicer.Extract(payload, "White")!;

        string result = CampaignSplicer.InsertCampaign(payload, slice, out CampaignSpliceReport report);

        Assert.Equal(payload, result);
        Assert.Equal(CampaignSpliceOutcome.Replaced, report.Outcome);
        Assert.Equal(slice.MapRecords.Count, report.MapsReplaced);
        Assert.Equal(0, report.MapsAdded);
        Assert.Equal(0, report.MapsRemoved);
        Assert.Empty(report.Warnings);
    }

    /// <summary>
    /// sav3 keeps its campaign at record four with map records both before and after it, so a splice
    /// that rebuilt the payload around the campaign rather than in place would show up here.
    /// </summary>
    [Fact]
    public void Putting_a_campaign_back_leaves_every_other_record_where_it_was()
    {
        string payload = FixtureFiles.ReadPayload(FixtureFiles.Sav3, "save");
        CampaignSlice slice = CampaignSplicer.Extract(payload, "White")!;

        IReadOnlyList<SaveRecord> before = SavePayloadReader.SplitRecords(payload);
        IReadOnlyList<SaveRecord> after = SavePayloadReader.SplitRecords(
            CampaignSplicer.InsertCampaign(payload, slice, out _));

        Assert.Equal(4, before.Select(record => record.Header).ToList().IndexOf("SAVE STATE"));
        Assert.Equal(before.Select(record => record.Header), after.Select(record => record.Header));
        Assert.Equal(before.Select(record => record.Body), after.Select(record => record.Body));
    }

    [Fact]
    public void A_campaign_the_slot_does_not_have_is_added_and_nothing_else_moves()
    {
        string target = FixtureFiles.ReadPayload(FixtureFiles.Sav2, "save");
        CampaignSlice slice = SliceFor("Gourmand", "SU", "HI");

        string result = CampaignSplicer.InsertCampaign(target, slice, out CampaignSpliceReport report);

        Assert.Equal(CampaignSpliceOutcome.Added, report.Outcome);
        Assert.Equal(2, report.MapsAdded);
        Assert.Equal(0, report.MapsRemoved);

        // Everything the slot had is still there, in order, with the new records after it.
        IReadOnlyList<SaveRecord> before = SavePayloadReader.SplitRecords(target);
        IReadOnlyList<SaveRecord> after = SavePayloadReader.SplitRecords(result);

        Assert.Equal(before.Count + 3, after.Count);
        Assert.Equal(
            before.Select(record => record.Header).Take(before.Count - 1),
            after.Select(record => record.Header).Take(before.Count - 1));

        Assert.Equal("Gourmand", CampaignSplicer.Extract(result, "Gourmand")!.SlugcatId);
        Assert.Equal("White", CampaignSplicer.Extract(result, "White")!.SlugcatId);
    }

    /// <summary>
    /// SaveToDisk writes a separator after every record it keeps, so a payload ends with one and a
    /// new campaign goes before the empty record that trailing separator leaves behind.
    /// </summary>
    [Fact]
    public void An_added_campaign_goes_where_the_game_puts_a_fresh_one()
    {
        string target = FixtureFiles.ReadPayload(FixtureFiles.Sav2, "save");

        string result = CampaignSplicer.InsertCampaign(target, SliceFor("Saint"), out _);

        Assert.EndsWith(Separator, result, StringComparison.Ordinal);

        IReadOnlyList<SaveRecord> after = SavePayloadReader.SplitRecords(result);
        Assert.Equal("SAVE STATE", after[^2].Header);
        Assert.Equal("", after[^1].Header);
    }

    [Fact]
    public void A_payload_that_does_not_end_with_a_separator_still_does_not()
    {
        string target = SyntheticSave.Progression(
            new[] { ("MISCPROG", "CYCLES<misA>1") },
            trailingEmptyRecord: false);

        string result = CampaignSplicer.InsertCampaign(target, SliceFor("White"), out _);

        Assert.Equal("MISCPROG<progDivB>CYCLES<misA>1" + Separator + SliceFor("White").SaveStateRecord, result);
    }

    [Fact]
    public void A_campaign_can_be_put_into_a_slot_that_holds_nothing_at_all()
    {
        string result = CampaignSplicer.InsertCampaign("", SliceFor("White"), out CampaignSpliceReport report);

        Assert.Equal(CampaignSpliceOutcome.Added, report.Outcome);
        Assert.Equal(SliceFor("White").SaveStateRecord + Separator, result);
    }

    // ---- one slot to another ----

    [Fact]
    public void Loading_a_campaign_over_another_leaves_the_rest_of_the_slot_alone()
    {
        string source = FixtureFiles.ReadPayload(FixtureFiles.Sav3, "save");
        string target = FixtureFiles.ReadPayload(FixtureFiles.Sav2, "save");
        CampaignSlice slice = CampaignSplicer.Extract(source, "White")!;

        string result = CampaignSplicer.InsertCampaign(target, slice, out CampaignSpliceReport report);

        Assert.Equal(CampaignSpliceOutcome.Replaced, report.Outcome);
        Assert.Equal(slice.SaveStateRecord, CampaignSplicer.Extract(result, "White")!.SaveStateRecord);

        // Same text, and still the fourth record. The character offset moves, because the campaign
        // that landed in front of it is a different length from the one it replaced.
        Assert.Equal(MiscProg(target), MiscProg(result));
        Assert.Equal(IndexOfMiscProg(target), IndexOfMiscProg(result));
    }

    /// <summary>
    /// The campaign arriving has only been to Sunken Pier, and the one it replaces had been to six
    /// regions. Keeping the other five would show map the arriving campaign has never seen.
    /// </summary>
    [Fact]
    public void Map_the_arriving_campaign_has_not_seen_does_not_stay_behind()
    {
        string source = FixtureFiles.ReadPayload(FixtureFiles.Sav3, "save");
        string target = FixtureFiles.ReadPayload(FixtureFiles.Sav2, "save");
        CampaignSlice slice = CampaignSplicer.Extract(source, "White")!;

        string result = CampaignSplicer.InsertCampaign(target, slice, out CampaignSpliceReport report);

        Assert.Equal(2, report.MapsReplaced);
        Assert.Equal(0, report.MapsAdded);
        Assert.Equal(5, report.MapsRemoved);

        Assert.Equal(slice.MapRecords.Count, CampaignSplicer.Extract(result, "White")!.MapRecords.Count);
    }

    [Fact]
    public void The_map_of_another_slugcat_is_not_touched()
    {
        string target = FixtureFiles.ReadPayload(FixtureFiles.Sav3, "save");
        CampaignSlice slice = SliceFor("White", "SU");

        string result = CampaignSplicer.InsertCampaign(target, slice, out _);

        Assert.Equal(
            SavePayloadReader.SplitRecords(target).Where(record => record.Header == "MAP_Watcher"),
            SavePayloadReader.SplitRecords(result).Where(record => record.Header == "MAP_Watcher"));
    }

    /// <summary>
    /// Without Downpour or Watcher the game writes bare MAP records, which every campaign in the
    /// slot reads. They belong to no campaign, so they never travel with one and never go with one.
    /// </summary>
    [Fact]
    public void A_map_the_whole_slot_shares_belongs_to_no_campaign()
    {
        string target = SyntheticSave.Progression(new[]
        {
            ("SAVE STATE", SyntheticSave.SaveStateBody("White")),
            ("MAP", "SU<progDivB>shared"),
            ("MAPUPDATE", "SU<progDivB>17"),
            ("MAP_White", "SU<progDivB>owned"),
        });

        CampaignSlice slice = CampaignSplicer.Extract(target, "White")!;
        Assert.Single(slice.MapRecords);
        Assert.True(CampaignSplicer.IsSharedMap("MAP<progDivB>SU<progDivB>shared"));
        Assert.Null(CampaignSplicer.MapOwnerOf("MAPUPDATE<progDivB>SU<progDivB>17"));

        string result = CampaignSplicer.RemoveCampaign(target, "White", includeMaps: true, out _);

        Assert.Contains("MAP<progDivB>SU<progDivB>shared", result, StringComparison.Ordinal);
        Assert.Contains("MAPUPDATE<progDivB>SU<progDivB>17", result, StringComparison.Ordinal);
        Assert.DoesNotContain("MAP_White", result, StringComparison.Ordinal);
    }

    // ---- taking one away ----

    /// <summary>
    /// The game's own WipeSaveState drops the SAVE STATE record and nothing else, so a campaign
    /// deleted in place leaves its map behind the same way.
    /// </summary>
    [Fact]
    public void Deleting_a_campaign_leaves_its_map_where_the_game_leaves_it()
    {
        string payload = FixtureFiles.ReadPayload(FixtureFiles.Sav2, "save");

        string result = CampaignSplicer.RemoveCampaign(payload, "White", includeMaps: false, out CampaignSpliceReport report);

        Assert.Equal(CampaignSpliceOutcome.Removed, report.Outcome);
        Assert.Equal(0, report.MapsRemoved);
        Assert.Null(CampaignSplicer.Extract(result, "White"));
        Assert.Equal(7, SavePayloadReader.SplitRecords(result).Count(record => record.Header.StartsWith("MAP", StringComparison.Ordinal)));
    }

    [Fact]
    public void Moving_a_campaign_out_takes_its_map_with_it()
    {
        string payload = FixtureFiles.ReadPayload(FixtureFiles.Sav2, "save");

        string result = CampaignSplicer.RemoveCampaign(payload, "White", includeMaps: true, out CampaignSpliceReport report);

        Assert.Equal(7, report.MapsRemoved);
        Assert.DoesNotContain("MAP_White", result, StringComparison.Ordinal);
        Assert.Contains("MISCPROG", result, StringComparison.Ordinal);
        Assert.EndsWith(Separator, result, StringComparison.Ordinal);
    }

    [Fact]
    public void Taking_out_a_campaign_that_is_not_there_changes_nothing()
    {
        string payload = FixtureFiles.ReadPayload(FixtureFiles.Sav2, "save");

        string result = CampaignSplicer.RemoveCampaign(payload, "Saint", includeMaps: true, out CampaignSpliceReport report);

        Assert.Equal(payload, result);
        Assert.Equal(CampaignSpliceOutcome.NotFound, report.Outcome);
        Assert.True(report.DidNothing);
    }

    // ---- what the game will make of it ----

    /// <summary>
    /// BackwardsCompatibilityRemix.ParseSaveNumber reads the first value of a campaign whatever its
    /// key is, so a record whose first field is something else belongs to whatever that field says.
    /// </summary>
    [Fact]
    public void The_slugcat_is_read_from_the_first_field_the_way_the_game_reads_it()
    {
        string record = "SAVE STATE<progDivB>TIMELINE<svB>Gourmand<svA>SAV STATE NUMBER<svB>White";

        Assert.Equal("Gourmand", CampaignSplicer.SlugcatOf(record));

        CampaignSplicer.InsertCampaign(
            "",
            new CampaignSlice("Gourmand", record, Array.Empty<string>()),
            out CampaignSpliceReport report);

        Assert.Contains(report.Warnings, warning => warning.Contains("'TIMELINE'", StringComparison.Ordinal));
    }

    [Fact]
    public void A_campaign_that_says_it_is_a_different_slugcat_says_so()
    {
        CampaignSplicer.InsertCampaign(
            "",
            new CampaignSlice("Saint", "SAVE STATE<progDivB>SAV STATE NUMBER<svB>White", Array.Empty<string>()),
            out CampaignSpliceReport report);

        Assert.Contains(report.Warnings, warning =>
            warning.Contains("Saint", StringComparison.Ordinal) && warning.Contains("Survivor", StringComparison.Ordinal));
    }

    /// <summary>
    /// PlayerProgression.IsThereASavedGame only counts a record that splits into exactly two parts
    /// on &lt;progDivB&gt;, so a campaign carrying one is one the slot menu will not offer.
    /// </summary>
    [Fact]
    public void A_campaign_holding_a_record_separator_says_the_slot_will_not_offer_it()
    {
        CampaignSplicer.InsertCampaign(
            "",
            new CampaignSlice(
                "White",
                "SAVE STATE<progDivB>SAV STATE NUMBER<svB>White<svA>NOTE<svB>a<progDivB>b",
                Array.Empty<string>()),
            out CampaignSpliceReport report);

        Assert.Contains(report.Warnings, warning => warning.Contains("<progDivB>", StringComparison.Ordinal));
    }

    [Fact]
    public void A_campaign_with_no_value_at_all_in_its_first_field_says_loading_would_fail()
    {
        CampaignSplicer.InsertCampaign(
            "",
            new CampaignSlice("White", "SAVE STATE<progDivB>HASTHEGLOW<svA>FOOD<svB>3", Array.Empty<string>()),
            out CampaignSpliceReport report);

        Assert.Contains(report.Warnings, warning => warning.Contains("would fail", StringComparison.Ordinal));
    }

    [Fact]
    public void Something_that_is_not_a_campaign_at_all_says_so_and_stops_there()
    {
        CampaignSplicer.InsertCampaign(
            "",
            new CampaignSlice("White", "MISCPROG<progDivB>CYCLES<misA>1", Array.Empty<string>()),
            out CampaignSpliceReport report);

        Assert.Single(report.Warnings);
        Assert.Contains("'MISCPROG'", report.Warnings[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// The game reaches the first copy and rewrites the rest unchanged, so a second one would go on
    /// shadowing the campaign being written for as long as the file lives.
    /// </summary>
    [Fact]
    public void A_slot_holding_the_same_slugcat_twice_comes_back_holding_it_once()
    {
        string target = SyntheticSave.Progression(new[]
        {
            ("SAVE STATE", SyntheticSave.SaveStateBody("White", cycle: 1)),
            ("MISCPROG", "CYCLES<misA>1"),
            ("SAVE STATE", SyntheticSave.SaveStateBody("White", cycle: 2)),
        });

        string result = CampaignSplicer.InsertCampaign(target, SliceFor("White"), out CampaignSpliceReport report);

        Assert.Single(CampaignSplicer.Campaigns(result));
        Assert.Contains(report.Warnings, warning => warning.Contains("more than one", StringComparison.Ordinal));
        Assert.Contains("MISCPROG", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// A save written before the ids were names stores a number, and ParsePlayerNumber turns 0 to 3
    /// into White, Yellow, Red and Night. The same campaign written both ways is one campaign.
    /// </summary>
    [Fact]
    public void A_campaign_numbered_the_old_way_is_the_same_campaign_as_the_named_one()
    {
        string target = SyntheticSave.Progression(new[]
        {
            ("SAVE STATE", "SAV STATE NUMBER<svB>0<svA>CYCLENUM<svB>4"),
            ("MISCPROG", "CYCLES<misA>1"),
        });

        Assert.NotNull(CampaignSplicer.Extract(target, "White"));

        string result = CampaignSplicer.InsertCampaign(target, SliceFor("White"), out CampaignSpliceReport report);

        Assert.Equal(CampaignSpliceOutcome.Replaced, report.Outcome);
        Assert.Single(CampaignSplicer.Campaigns(result));
    }

    // ---- nothing to work with ----

    [Fact]
    public void Nothing_in_gives_nothing_back()
    {
        Assert.Null(CampaignSplicer.Extract(null, "White"));
        Assert.Null(CampaignSplicer.Extract("", "White"));
        Assert.Null(CampaignSplicer.Extract("anything", ""));
        Assert.Empty(CampaignSplicer.Campaigns(null));
        Assert.Equal("", CampaignSplicer.RemoveCampaign(null, "White", includeMaps: true, out _));
        Assert.Null(CampaignSplicer.SlugcatOf(null));
        Assert.Null(CampaignSplicer.MapOwnerOf("MAP_"));
    }

    // ---- helpers ----

    private static CampaignSlice SliceFor(string slugcat, params string[] regions)
        => new(
            slugcat,
            "SAVE STATE" + SyntheticSave.HeaderSeparator + SyntheticSave.SaveStateBody(slugcat),
            regions.Select(region => $"MAP_{slugcat}<progDivB>{region}<progDivB>{region}-map").ToArray());

    private static string MiscProg(string payload)
        => SavePayloadReader.SplitRecords(payload).Single(record => record.Header == "MISCPROG").Body;

    private static int IndexOfMiscProg(string payload)
        => SavePayloadReader.SplitRecords(payload).Select(record => record.Header).ToList().IndexOf("MISCPROG");
}
