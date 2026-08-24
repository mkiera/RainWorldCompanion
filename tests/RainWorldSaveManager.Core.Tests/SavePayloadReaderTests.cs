using RainWorldSaveManager.Core.Saves;

namespace RainWorldSaveManager.Tests;

/// <summary>
/// Splitting is done against payloads pulled out of the fixtures by the test-side parser, so a
/// failure here points at the splitter rather than at the container reader.
/// </summary>
public class SavePayloadReaderTests
{
    [Fact]
    public void The_separators_are_the_tokens_the_game_writes()
    {
        Assert.Equal("<progDivA>", SavePayloadReader.RecordSeparator);
        Assert.Equal("<progDivB>", SavePayloadReader.HeaderSeparator);
        Assert.Equal("<svA>", SavePayloadReader.FieldSeparator);
        Assert.Equal("<svB>", SavePayloadReader.ValueSeparator);
    }

    [Fact]
    public void The_sav2_save_payload_splits_into_ten_records()
    {
        var payload = FixtureFiles.ReadPayload(FixtureFiles.Sav2, "save");

        var records = SavePayloadReader.SplitRecords(payload);

        Assert.Equal(10, records.Count);
    }

    [Fact]
    public void The_sav2_headers_include_the_empty_record_and_the_four_known_kinds()
    {
        var payload = FixtureFiles.ReadPayload(FixtureFiles.Sav2, "save");

        var headers = SavePayloadReader.SplitRecords(payload).Select(r => r.Header).ToList();

        Assert.Contains("", headers);
        Assert.Contains("MISCPROG", headers);
        Assert.Contains("SAVE STATE", headers);
        Assert.Contains("MAP_White", headers);
        Assert.Contains("MAPUPDATE_White", headers);
    }

    [Fact]
    public void The_sav3_save_payload_splits_into_eight_records()
    {
        var payload = FixtureFiles.ReadPayload(FixtureFiles.Sav3, "save");

        Assert.Equal(8, SavePayloadReader.SplitRecords(payload).Count);
    }

    [Fact]
    public void The_online_sav_payload_splits_into_four_records_and_holds_no_save_state()
    {
        var payload = FixtureFiles.ReadPayload(FixtureFiles.OnlineSav, "save");

        var records = SavePayloadReader.SplitRecords(payload);

        Assert.Equal(4, records.Count);
        Assert.DoesNotContain(records, r => r.Header == "SAVE STATE");
    }

    [Fact]
    public void The_save_state_body_is_kept_whole_rather_than_split_at_the_header()
    {
        var payload = FixtureFiles.ReadPayload(FixtureFiles.Sav2, "save");

        var saveState = SavePayloadReader.SplitRecords(payload).Single(r => r.Header == "SAVE STATE");

        Assert.Equal(9389, saveState.Body.Length);
        Assert.StartsWith("SAV STATE NUMBER<svB>White", saveState.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_record_header_and_body_are_split_at_the_first_header_separator_only()
    {
        var records = SavePayloadReader.SplitRecords("HEAD<progDivB>a<progDivB>b");

        var record = Assert.Single(records);
        Assert.Equal("HEAD", record.Header);
        Assert.Equal("a<progDivB>b", record.Body);
    }

    [Fact]
    public void Records_are_split_on_every_record_separator()
    {
        var payload = SyntheticSave.Progression(
            new[] { ("ONE", "a"), ("TWO", "b"), ("THREE", "c") },
            trailingEmptyRecord: false);

        var records = SavePayloadReader.SplitRecords(payload);

        Assert.Equal(new[] { "ONE", "TWO", "THREE" }, records.Select(r => r.Header).ToArray());
        Assert.Equal(new[] { "a", "b", "c" }, records.Select(r => r.Body).ToArray());
    }

    [Fact]
    public void The_trailing_separator_produces_a_final_record_with_an_empty_header()
    {
        var payload = SyntheticSave.Progression(new[] { ("ONE", "a") }, trailingEmptyRecord: true);

        var records = SavePayloadReader.SplitRecords(payload);

        Assert.Equal(2, records.Count);
        Assert.Equal("", records[^1].Header);
    }

    [Fact]
    public void A_bare_flag_field_has_a_null_value()
    {
        var body = "CYCLENUM<svB>17<svA>HASTHEGLOW<svA>FOOD<svB>3";

        var fields = SavePayloadReader.SplitFields(body);

        Assert.Equal(3, fields.Count);
        Assert.Equal("CYCLENUM", fields[0].Key);
        Assert.Equal("17", fields[0].Value);
        Assert.Equal("HASTHEGLOW", fields[1].Key);
        Assert.Null(fields[1].Value);
        Assert.Equal("FOOD", fields[2].Key);
        Assert.Equal("3", fields[2].Value);
    }

    [Fact]
    public void A_field_value_containing_the_value_separator_is_split_only_at_the_first_one()
    {
        var fields = SavePayloadReader.SplitFields("DEVOURMENTSTATE<svB>pred<svB>prey");

        var field = Assert.Single(fields);
        Assert.Equal("DEVOURMENTSTATE", field.Key);
        Assert.Equal("pred<svB>prey", field.Value);
    }

    [Fact]
    public void A_field_value_may_be_empty_without_becoming_a_bare_flag()
    {
        var fields = SavePayloadReader.SplitFields("DENPOS<svB>");

        var field = Assert.Single(fields);
        Assert.Equal("DENPOS", field.Key);
        Assert.Equal("", field.Value);
    }

    [Fact]
    public void The_real_sav2_save_state_carries_the_metadata_fields()
    {
        var payload = FixtureFiles.ReadPayload(FixtureFiles.Sav2, "save");
        var saveState = SavePayloadReader.SplitRecords(payload).Single(r => r.Header == "SAVE STATE");

        var fields = SavePayloadReader.SplitFields(saveState.Body);
        var byKey = fields
            .Where(f => f.Value is not null)
            .GroupBy(f => f.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Value!, StringComparer.Ordinal);

        Assert.Equal("White", byKey["SAV STATE NUMBER"]);
        Assert.Equal("17", byKey["CYCLENUM"]);
        Assert.Equal("3", byKey["FOOD"]);
        Assert.Equal("SU_S04", byKey["DENPOS"]);
        Assert.Equal("8840", byKey["SEED"]);
    }

    [Fact]
    public void The_real_sav3_save_state_carries_four_devourment_fields()
    {
        var payload = FixtureFiles.ReadPayload(FixtureFiles.Sav3, "save");
        var saveState = SavePayloadReader.SplitRecords(payload).Single(r => r.Header == "SAVE STATE");

        var devourment = SavePayloadReader.SplitFields(saveState.Body)
            .Count(f => f.Key.StartsWith("DEVOURMENTSTATE", StringComparison.Ordinal));

        Assert.Equal(4, devourment);
    }

    [Fact]
    public void Splitting_an_empty_payload_does_not_throw()
    {
        var records = SavePayloadReader.SplitRecords("");
        var fields = SavePayloadReader.SplitFields("");

        Assert.NotNull(records);
        Assert.NotNull(fields);
    }
}
