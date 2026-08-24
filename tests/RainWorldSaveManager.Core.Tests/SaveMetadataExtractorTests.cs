using RainWorldSaveManager.Core.Saves;
using RainWorldSaveManager.Core.Saves.Models;

namespace RainWorldSaveManager.Tests;

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
    public void An_empty_container_reports_no_campaigns_and_no_error()
    {
        var metadata = SaveMetadataExtractor.Extract(FixtureFiles.PathTo(FixtureFiles.Exp1), 0);

        Assert.Null(metadata.ParseError);
        Assert.Empty(metadata.Campaigns);
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

    [Theory]
    [InlineData("sav", 1)]
    [InlineData("sav2", 2)]
    [InlineData("sav3", 3)]
    public void SlotNumberForFileName_maps_the_three_container_names(string fileName, int expected)
        => Assert.Equal(expected, SaveMetadataExtractor.SlotNumberForFileName(fileName));

    [Theory]
    [InlineData("sav - Copy")]
    [InlineData("sav - Copy (2)")]
    [InlineData("sav.bak")]
    [InlineData("sav4")]
    [InlineData("sav0")]
    [InlineData("save")]
    [InlineData("exp1")]
    [InlineData("expCore1")]
    [InlineData("online_sav")]
    [InlineData("online_sav2")]
    [InlineData("options")]
    [InlineData("steam_autocloud.vdf")]
    [InlineData("")]
    public void SlotNumberForFileName_returns_null_for_everything_else(string fileName)
        => Assert.Null(SaveMetadataExtractor.SlotNumberForFileName(fileName));
}
