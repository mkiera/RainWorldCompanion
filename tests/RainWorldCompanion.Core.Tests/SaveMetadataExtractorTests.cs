using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.Core.Saves.Models;

namespace RainWorldCompanion.Tests;

/// <summary>
/// Extraction feeds the slot list the user reads before overwriting anything, and it runs over
/// whatever files happen to sit in the save folder. It has to describe real saves correctly and
/// survive everything else.
/// </summary>
public class SaveMetadataExtractorTests
{
    [Fact]
    public void Slot_two_reports_the_campaign_recorded_in_sav2()
    {
        var metadata = SaveMetadataExtractor.Extract(FixtureFiles.PathTo(FixtureFiles.Sav2), 2);

        Assert.Null(metadata.ParseError);
        Assert.Equal(2, metadata.Slot);
        Assert.True(metadata.ChecksumValid);

        var campaign = Assert.Single(metadata.Campaigns);
        Assert.Equal("White", campaign.SlugcatId);
        Assert.Equal(17, campaign.CycleNum);
        Assert.Equal(3, campaign.Food);
        Assert.Equal("SU_S04", campaign.DenPos);
        Assert.Equal("8840", campaign.Seed);
        Assert.Equal(0, campaign.DevourmentStateCount);
    }

    [Fact]
    public void Slot_three_reports_the_campaign_and_the_four_devourment_entries_in_sav3()
    {
        var metadata = SaveMetadataExtractor.Extract(FixtureFiles.PathTo(FixtureFiles.Sav3), 3);

        Assert.Null(metadata.ParseError);
        Assert.Equal(3, metadata.Slot);
        Assert.True(metadata.ChecksumValid);

        var campaign = Assert.Single(metadata.Campaigns);
        Assert.Equal("White", campaign.SlugcatId);
        Assert.Equal(9, campaign.CycleNum);
        Assert.Equal(0, campaign.Food);
        Assert.Equal("SU_S04", campaign.DenPos);
        Assert.Equal("5986", campaign.Seed);
        Assert.Equal(4, campaign.DevourmentStateCount);
    }

    [Fact]
    public void A_file_with_no_save_state_record_reports_no_campaigns_and_no_error()
    {
        var metadata = SaveMetadataExtractor.Extract(FixtureFiles.PathTo(FixtureFiles.OnlineSav), 0);

        Assert.Null(metadata.ParseError);
        Assert.Empty(metadata.Campaigns);
    }

    [Fact]
    public void A_file_with_no_save_state_record_still_counts_the_records_it_has()
    {
        // The real online_sav holds an empty header, MAP_White, MAPUPDATE_White and MISCPROG, and
        // no SAVE STATE. Campaigns being empty is therefore not the same question as the file
        // being empty, and only the record count can tell them apart.
        var metadata = SaveMetadataExtractor.Extract(FixtureFiles.PathTo(FixtureFiles.OnlineSav), 1, SaveRealm.Online);

        Assert.Empty(metadata.Campaigns);
        Assert.Equal(4, metadata.RecordCount);
        Assert.DoesNotContain("empty", metadata.Describe(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("map and progression", metadata.Describe(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_payload_that_is_only_the_digest_counts_no_records_and_is_still_called_empty()
    {
        // An untouched online slot: the stored value is the 32 character digest and nothing else.
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("online_sav3", SyntheticSave.SaveFile(""));

        var metadata = SaveMetadataExtractor.Extract(path, 3, SaveRealm.Online);

        Assert.Null(metadata.ParseError);
        Assert.Empty(metadata.Campaigns);
        Assert.Equal(0, metadata.RecordCount);
        Assert.Contains("empty", metadata.Describe(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_manifest_that_never_recorded_a_record_count_falls_back_to_the_old_wording()
    {
        // Deserialising a snapshot written before the count existed leaves it null. Guessing from
        // a null would relabel every old backup's slots, so the unknown case keeps saying "empty".
        var slot = new SlotMetadata { Slot = 1, FileName = "online_sav", Realm = SaveRealm.Online };

        Assert.Null(slot.RecordCount);
        Assert.Equal("Online slot 1: empty", slot.Describe());
    }

    [Fact]
    public void An_empty_container_reports_no_campaigns_and_no_error()
    {
        var metadata = SaveMetadataExtractor.Extract(FixtureFiles.PathTo(FixtureFiles.Exp1), 0);

        Assert.Null(metadata.ParseError);
        Assert.Empty(metadata.Campaigns);
    }

    [Fact]
    public void A_container_with_no_save_key_counts_no_records()
    {
        // exp1 has no keys at all, which is a real state rather than a failure.
        var metadata = SaveMetadataExtractor.Extract(FixtureFiles.PathTo(FixtureFiles.Exp1), 0);

        Assert.Equal(0, metadata.RecordCount);
    }

    [Fact]
    public void The_file_name_comes_from_the_path()
    {
        using var temp = new TempDirectory();
        var path = FixtureFiles.CopyTo(temp, FixtureFiles.Sav2, "sav2");

        var metadata = SaveMetadataExtractor.Extract(path, 2);

        Assert.Equal("sav2", metadata.FileName);
    }

    [Fact]
    public void A_garbage_file_reports_a_parse_error_instead_of_throwing()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("sav", SyntheticSave.GarbageBytes());

        var metadata = SaveMetadataExtractor.Extract(path, 1);

        Assert.False(string.IsNullOrWhiteSpace(metadata.ParseError));
        Assert.Equal(1, metadata.Slot);
    }

    [Fact]
    public void A_truncated_container_reports_a_parse_error_instead_of_throwing()
    {
        using var temp = new TempDirectory();
        var bytes = SyntheticSave.BytesWithoutClosingTag(new[] { SyntheticSave.Entry("save", "value") });
        var path = temp.WriteBytes("sav", bytes);

        var metadata = SaveMetadataExtractor.Extract(path, 1);

        Assert.False(string.IsNullOrWhiteSpace(metadata.ParseError));
    }

    [Fact]
    public void A_zero_byte_file_reports_a_parse_error_instead_of_throwing()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("sav2", Array.Empty<byte>());

        var metadata = SaveMetadataExtractor.Extract(path, 2);

        Assert.False(string.IsNullOrWhiteSpace(metadata.ParseError));
        Assert.Equal(2, metadata.Slot);
        Assert.Empty(metadata.Campaigns);
    }

    [Fact]
    public void A_missing_path_reports_a_parse_error_instead_of_throwing()
    {
        using var temp = new TempDirectory();

        var metadata = SaveMetadataExtractor.Extract(temp.Resolve("sav3"), 3);

        Assert.False(string.IsNullOrWhiteSpace(metadata.ParseError));
        Assert.Equal(3, metadata.Slot);
        Assert.Empty(metadata.Campaigns);
    }

    [Fact]
    public void A_wrong_digest_is_reported_as_an_invalid_checksum()
    {
        using var temp = new TempDirectory();
        var payload = SyntheticSave.SavePayload(slugcat: "Artificer", cycle: 5, food: 2, seed: "77");
        var bytes = SyntheticSave.Bytes(new[]
        {
            SyntheticSave.Entry("save__Backup", SyntheticSave.WrapWithBadChecksum(payload)),
            SyntheticSave.Entry("save", SyntheticSave.WrapWithBadChecksum(payload)),
        });
        var path = temp.WriteBytes("sav", bytes);

        var metadata = SaveMetadataExtractor.Extract(path, 1);

        Assert.False(metadata.ChecksumValid);
    }

    [Fact]
    public void A_bare_glow_flag_is_read_as_true()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("sav", SyntheticSave.SaveFile(SyntheticSave.SavePayload(hasGlow: true)));

        var campaign = Assert.Single(SaveMetadataExtractor.Extract(path, 1).Campaigns);

        Assert.True(campaign.HasGlow);
    }

    [Fact]
    public void A_save_without_the_glow_flag_is_read_as_false()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("sav", SyntheticSave.SaveFile(SyntheticSave.SavePayload(hasGlow: false)));

        var campaign = Assert.Single(SaveMetadataExtractor.Extract(path, 1).Campaigns);

        Assert.False(campaign.HasGlow);
    }

    [Fact]
    public void Devourment_fields_are_counted_from_a_synthetic_save_as_well()
    {
        using var temp = new TempDirectory();
        var payload = SyntheticSave.SavePayload(slugcat: "Rivulet", devourmentStates: 7);
        var path = temp.WriteBytes("sav3", SyntheticSave.SaveFile(payload, paddingBytes: 4096));

        var campaign = Assert.Single(SaveMetadataExtractor.Extract(path, 3).Campaigns);

        Assert.Equal("Rivulet", campaign.SlugcatId);
        Assert.Equal(7, campaign.DevourmentStateCount);
    }

    [Fact]
    public void The_padding_on_a_real_save_does_not_reach_the_campaign_fields()
    {
        var metadata = SaveMetadataExtractor.Extract(FixtureFiles.PathTo(FixtureFiles.Sav3), 3);

        var campaign = Assert.Single(metadata.Campaigns);

        Assert.False(campaign.SlugcatId.Contains('\0'), "the slugcat id carried NUL padding");
        Assert.False((campaign.DenPos ?? "").Contains('\0'), "the den position carried NUL padding");
        Assert.False((campaign.Seed ?? "").Contains('\0'), "the seed carried NUL padding");
    }

    [Fact]
    public void Campaign_describe_names_the_slugcat_and_the_cycle()
    {
        var campaign = Assert.Single(SaveMetadataExtractor.Extract(FixtureFiles.PathTo(FixtureFiles.Sav2), 2).Campaigns);

        var text = campaign.Describe();

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Contains("White", text, StringComparison.Ordinal);
        Assert.Contains("17", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Slot_describe_names_the_slot_and_the_campaign()
    {
        var metadata = SaveMetadataExtractor.Extract(FixtureFiles.PathTo(FixtureFiles.Sav2), 2);

        var text = metadata.Describe();

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Contains("2", text, StringComparison.Ordinal);
        Assert.Contains("White", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Slot_describe_still_returns_a_line_for_an_unreadable_file()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("sav2", SyntheticSave.GarbageBytes());

        var text = SaveMetadataExtractor.Extract(path, 2).Describe();

        Assert.False(string.IsNullOrWhiteSpace(text));
    }

    // Mapping a container file name to a slot is covered by SlotCopyTests, which asserts the
    // realm and the number together for both realms and for every name that is not a slot.
}
