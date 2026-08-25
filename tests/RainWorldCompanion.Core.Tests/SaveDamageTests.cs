using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Tests;

/// <summary>
/// The slot list is what a player reads before deciding to overwrite a save. These tests are
/// about the two ways it can say the wrong thing: describing a damaged slot as an unused one,
/// and describing a sound slot as a corrupt one.
/// </summary>
public class SaveDamageTests
{
    /// <summary>
    /// Keys and Values are matched by index. A write truncated part way through leaves Keys with
    /// all of its children and Values one short, so the last key loses its value. Pairing what
    /// can be paired and reporting nothing turns lost save data into a blank slot.
    /// </summary>
    [Fact]
    public void A_container_whose_keys_outnumber_its_values_reports_the_mismatch()
    {
        using var temp = new TempDirectory();
        var payload = SyntheticSave.SavePayload();
        var path = temp.WriteBytes("sav", SyntheticSave.BytesWithAnUnpairedKey(new[]
        {
            SyntheticSave.Entry("save__Backup", SyntheticSave.Wrap(payload)),
            SyntheticSave.Entry("save", SyntheticSave.Wrap(payload)),
        }));

        var container = SaveContainer.Read(path);

        Assert.NotNull(container.StructureProblem);
        Assert.Contains("2", container.StructureProblem!, StringComparison.Ordinal);
        Assert.Single(container.Entries);
    }

    [Fact]
    public void A_slot_that_lost_its_save_entry_reads_as_damaged_rather_than_empty()
    {
        using var temp = new TempDirectory();
        var payload = SyntheticSave.SavePayload();

        // The live order: "save" is the second key, so a truncated Values list loses exactly the
        // entry that holds the campaign.
        var path = temp.WriteBytes("sav2", SyntheticSave.BytesWithAnUnpairedKey(new[]
        {
            SyntheticSave.Entry("save__Backup", SyntheticSave.Wrap(payload)),
            SyntheticSave.Entry("save", SyntheticSave.Wrap(payload)),
        }));

        var metadata = SaveMetadataExtractor.Extract(path, 2);

        Assert.False(string.IsNullOrWhiteSpace(metadata.ParseError));
        Assert.DoesNotContain("empty", metadata.Describe(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_container_with_no_values_element_at_all_reports_the_mismatch()
    {
        using var temp = new TempDirectory();
        var xml = SyntheticSave.Xml(new[] { SyntheticSave.Entry("save", "value") });
        var start = xml.IndexOf("<Values", StringComparison.Ordinal);
        var end = xml.IndexOf("</Values>", start, StringComparison.Ordinal) + "</Values>".Length;
        var path = temp.WriteBytes("sav", WithBom(xml[..start] + xml[end..]));

        var container = SaveContainer.Read(path);

        Assert.NotNull(container.StructureProblem);
        Assert.Empty(container.Entries);
    }

    [Fact]
    public void A_sound_container_reports_no_structure_problem()
    {
        foreach (var fixture in new[]
                 {
                     FixtureFiles.Sav2, FixtureFiles.Sav3, FixtureFiles.Exp1,
                     FixtureFiles.ExpCore1, FixtureFiles.OnlineSav, FixtureFiles.Options,
                 })
        {
            Assert.Null(SaveContainer.Read(FixtureFiles.PathTo(fixture)).StructureProblem);
        }
    }

    // ---- No digest is not a bad digest ----

    /// <summary>
    /// A value with no 32-character hex prefix is a raw payload, which is how this format stores
    /// the expCore "core" key and every key in options. Reporting it as a failed checksum is the
    /// same sentence the reader shows for a save the game really would discard.
    /// </summary>
    [Fact]
    public void A_save_value_with_no_digest_is_not_described_as_a_bad_checksum()
    {
        using var temp = new TempDirectory();
        var payload = SyntheticSave.SavePayload(slugcat: "Rivulet", cycle: 5);
        var path = temp.WriteBytes("sav", SyntheticSave.Bytes(new[] { SyntheticSave.Entry("save", payload) }));

        var metadata = SaveMetadataExtractor.Extract(path, 1);

        Assert.Null(metadata.ParseError);
        Assert.Null(metadata.ChecksumValid);
        Assert.Contains("Rivulet", metadata.Describe(), StringComparison.Ordinal);
        Assert.DoesNotContain("checksum", metadata.Describe(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_save_value_with_a_wrong_digest_still_is_described_as_a_bad_checksum()
    {
        using var temp = new TempDirectory();
        var payload = SyntheticSave.SavePayload(slugcat: "Rivulet", cycle: 5);
        var path = temp.WriteBytes("sav", SyntheticSave.Bytes(new[]
        {
            SyntheticSave.Entry("save", SyntheticSave.WrapWithBadChecksum(payload)),
        }));

        var metadata = SaveMetadataExtractor.Extract(path, 1);

        Assert.False(metadata.ChecksumValid);
        Assert.Contains("checksum bad", metadata.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_save_value_with_a_right_digest_is_described_without_a_checksum_note()
    {
        var metadata = SaveMetadataExtractor.Extract(FixtureFiles.PathTo(FixtureFiles.Sav2), 2);

        Assert.True(metadata.ChecksumValid);
        Assert.DoesNotContain("checksum", metadata.Describe(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_slot_with_no_save_key_reports_no_checksum_state_at_all()
    {
        var metadata = SaveMetadataExtractor.Extract(FixtureFiles.PathTo(FixtureFiles.Exp1), 1);

        Assert.Null(metadata.ParseError);
        Assert.Null(metadata.ChecksumValid);
        Assert.Equal("Slot 1: empty", metadata.Describe());
    }

    private static byte[] WithBom(string xml)
    {
        var encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        return encoding.GetPreamble().Concat(encoding.GetBytes(xml)).ToArray();
    }
}
