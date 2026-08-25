using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Tests;

/// <summary>
/// Reading is the only operation that touches a player's real files without a backup behind it,
/// so these check both that it decodes correctly and that it writes nothing.
/// </summary>
public class SaveContainerTests
{
    [Fact]
    public void Reading_sav2_yields_exactly_the_save_and_save_backup_keys()
    {
        var container = SaveContainer.Read(FixtureFiles.PathTo(FixtureFiles.Sav2));

        Assert.Equal(
            new[] { "save", "save__Backup" },
            container.Entries.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }

    [Theory]
    [InlineData(FixtureFiles.Sav2, new[] { "save", "save__Backup" })]
    [InlineData(FixtureFiles.Sav3, new[] { "save", "save__Backup" })]
    [InlineData(FixtureFiles.OnlineSav, new[] { "save", "save__Backup" })]
    [InlineData(FixtureFiles.ExpCore1, new[] { "core" })]
    [InlineData(FixtureFiles.Options, new[] { "ArenaOnlineMeadowSetup", "ArenaSetup", "options", "thepit_Sandbox" })]
    public void Every_fixture_decodes_to_the_keys_an_independent_parser_finds(string fixture, string[] expectedKeys)
    {
        var container = SaveContainer.Read(FixtureFiles.PathTo(fixture));

        Assert.Equal(expectedKeys, container.Entries.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Values_survive_decoding_byte_for_byte()
    {
        var expected = FixtureFiles.ReadEntries(FixtureFiles.Sav2);

        var container = SaveContainer.Read(FixtureFiles.PathTo(FixtureFiles.Sav2));

        foreach (var entry in expected)
        {
            Assert.Equal(entry.Value, container.Entries[entry.Key]);
        }
    }

    [Fact]
    public void Entries_hold_the_raw_value_with_the_checksum_prefix_still_attached()
    {
        var container = SaveContainer.Read(FixtureFiles.PathTo(FixtureFiles.Sav2));

        var raw = container.Entries["save"];

        Assert.True(SaveChecksum.HasChecksumPrefix(raw));
        Assert.Equal(30039, raw.Length);
    }

    [Fact]
    public void Padded_files_report_their_trailing_nul_count()
    {
        var container = SaveContainer.Read(FixtureFiles.PathTo(FixtureFiles.Sav2));

        Assert.True(container.PaddingByteCount > 0);
        Assert.Equal(28125, container.PaddingByteCount);
    }

    [Theory]
    [InlineData(FixtureFiles.Sav3, 46414)]
    [InlineData(FixtureFiles.OnlineSav, 876)]
    [InlineData(FixtureFiles.Options, 2301)]
    public void Padding_counts_match_the_measured_values(string fixture, int expectedPadding)
    {
        var container = SaveContainer.Read(FixtureFiles.PathTo(fixture));

        Assert.Equal(expectedPadding, container.PaddingByteCount);
        Assert.Equal(FixtureFiles.PaddingCharCount(fixture), container.PaddingByteCount);
    }

    [Theory]
    [InlineData(FixtureFiles.Exp1)]
    [InlineData(FixtureFiles.ExpCore1)]
    public void Unpadded_files_report_zero_padding(string fixture)
    {
        var container = SaveContainer.Read(FixtureFiles.PathTo(fixture));

        Assert.Equal(0, container.PaddingByteCount);
    }

    [Fact]
    public void An_empty_hashtable_reads_as_zero_entries_rather_than_a_failure()
    {
        var container = SaveContainer.Read(FixtureFiles.PathTo(FixtureFiles.Exp1));

        Assert.Empty(container.Entries);
    }

    [Theory]
    [InlineData(FixtureFiles.Sav2, "8")]
    [InlineData(FixtureFiles.Sav3, "6")]
    [InlineData(FixtureFiles.Exp1, "0")]
    [InlineData(FixtureFiles.ExpCore1, "2")]
    [InlineData(FixtureFiles.OnlineSav, "11")]
    [InlineData(FixtureFiles.Options, "7")]
    public void FormatVersion_is_the_text_of_the_version_element(string fixture, string expected)
    {
        var container = SaveContainer.Read(FixtureFiles.PathTo(fixture));

        Assert.Equal(expected, container.FormatVersion);
        Assert.Equal(FixtureFiles.ReadVersion(fixture), container.FormatVersion);
    }

    [Fact]
    public void FormatVersion_is_null_when_the_version_element_is_absent()
    {
        using var temp = new TempDirectory();
        var xml = SyntheticSave.Xml(new[] { SyntheticSave.Entry("save", "value") });
        var stripped = RemoveVersionElement(xml);
        var path = temp.WriteBytes("sav", EncodeWithBom(stripped));

        var container = SaveContainer.Read(path);

        Assert.Null(container.FormatVersion);
    }

    [Fact]
    public void FilePath_is_the_path_that_was_read()
    {
        var path = FixtureFiles.PathTo(FixtureFiles.Sav2);

        Assert.Equal(path, SaveContainer.Read(path).FilePath);
    }

    [Fact]
    public void Reading_leaves_the_file_byte_identical()
    {
        using var temp = new TempDirectory();
        var path = FixtureFiles.CopyTo(temp, FixtureFiles.Sav2, "sav2");
        var before = File.ReadAllBytes(path);

        SaveContainer.Read(path);

        var after = File.ReadAllBytes(path);
        Assert.Equal(before.Length, after.Length);
        Assert.True(before.AsSpan().SequenceEqual(after), "reading a container rewrote it");
    }

    [Fact]
    public void Reading_leaves_the_file_unlocked_for_a_later_writer()
    {
        using var temp = new TempDirectory();
        var path = FixtureFiles.CopyTo(temp, FixtureFiles.Sav2, "sav2");

        SaveContainer.Read(path);

        // A held read handle would make this throw, which would break restore.
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        Assert.Equal(3, new FileInfo(path).Length);
    }

    [Fact]
    public void A_file_with_no_closing_tag_throws()
    {
        using var temp = new TempDirectory();
        var bytes = SyntheticSave.BytesWithoutClosingTag(new[] { SyntheticSave.Entry("save", "value") });
        var path = temp.WriteBytes("sav", bytes);

        Assert.Throws<SaveContainerException>(() => SaveContainer.Read(path));
    }

    [Fact]
    public void A_file_whose_xml_is_broken_throws()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("sav", SyntheticSave.MalformedXmlBytes());

        Assert.Throws<SaveContainerException>(() => SaveContainer.Read(path));
    }

    [Fact]
    public void A_file_of_binary_garbage_throws()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("sav", SyntheticSave.GarbageBytes());

        Assert.Throws<SaveContainerException>(() => SaveContainer.Read(path));
    }

    [Fact]
    public void A_zero_byte_file_throws_from_read()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("sav", Array.Empty<byte>());

        Assert.Throws<SaveContainerException>(() => SaveContainer.Read(path));
    }

    [Fact]
    public void A_missing_path_throws_from_read()
    {
        using var temp = new TempDirectory();

        Assert.Throws<SaveContainerException>(() => SaveContainer.Read(temp.Resolve("not-here")));
    }

    [Fact]
    public void TryRead_reports_a_failure_instead_of_throwing()
    {
        using var temp = new TempDirectory();
        var bytes = SyntheticSave.BytesWithoutClosingTag(new[] { SyntheticSave.Entry("save", "value") });
        var path = temp.WriteBytes("sav", bytes);

        var read = SaveContainer.TryRead(path, out var container, out var error);

        Assert.False(read);
        Assert.Null(container);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryRead_on_a_zero_byte_file_does_not_throw()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("sav", Array.Empty<byte>());

        var read = SaveContainer.TryRead(path, out var container, out var error);

        Assert.False(read);
        Assert.Null(container);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryRead_on_a_missing_path_does_not_throw()
    {
        using var temp = new TempDirectory();

        var read = SaveContainer.TryRead(temp.Resolve("not-here"), out var container, out var error);

        Assert.False(read);
        Assert.Null(container);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryRead_on_a_good_file_succeeds_with_no_error()
    {
        var read = SaveContainer.TryRead(FixtureFiles.PathTo(FixtureFiles.Sav2), out var container, out var error);

        Assert.True(read);
        Assert.NotNull(container);
        Assert.Null(error);
        Assert.Equal(2, container!.Entries.Count);
    }

    [Fact]
    public void TryRead_on_the_empty_container_succeeds_with_no_entries()
    {
        var read = SaveContainer.TryRead(FixtureFiles.PathTo(FixtureFiles.Exp1), out var container, out var error);

        Assert.True(read);
        Assert.Null(error);
        Assert.Empty(container!.Entries);
    }

    [Fact]
    public void A_synthetic_container_round_trips_through_the_reader()
    {
        using var temp = new TempDirectory();
        var payload = SyntheticSave.SavePayload(slugcat: "Saint", cycle: 200, food: 8, seed: "4242");
        var path = temp.WriteBytes("sav", SyntheticSave.SaveFile(payload, paddingBytes: 1024));

        var container = SaveContainer.Read(path);

        Assert.Equal(1024, container.PaddingByteCount);
        Assert.Equal(SaveChecksum.Wrap(payload), container.Entries["save"]);
    }

    // This is the assertion that proves the salt constant is the game's, not a transcription of itself.
    [Theory]
    [InlineData(FixtureFiles.Sav2)]
    [InlineData(FixtureFiles.Sav3)]
    [InlineData(FixtureFiles.OnlineSav)]
    public void Every_checksummed_entry_in_a_real_save_verifies(string fixture)
    {
        var container = SaveContainer.Read(FixtureFiles.PathTo(fixture));

        Assert.NotEmpty(container.Entries);

        foreach (var entry in container.Entries)
        {
            Assert.True(
                SaveChecksum.HasChecksumPrefix(entry.Value),
                $"{fixture} key '{entry.Key}' has no checksum prefix");

            var unwrapped = SaveChecksum.TryUnwrap(entry.Value, out var payload, out var checksumValid);

            Assert.True(unwrapped, $"{fixture} key '{entry.Key}' did not unwrap");
            Assert.True(checksumValid, $"{fixture} key '{entry.Key}' failed its checksum");
            Assert.Equal(entry.Value[..32], SaveChecksum.Compute(payload));
            Assert.Equal(entry.Value[32..], payload);
        }
    }

    [Theory]
    [InlineData(FixtureFiles.ExpCore1)]
    [InlineData(FixtureFiles.Options)]
    public void Files_that_store_raw_values_are_not_reported_as_corrupt(string fixture)
    {
        var container = SaveContainer.Read(FixtureFiles.PathTo(fixture));

        Assert.NotEmpty(container.Entries);

        foreach (var entry in container.Entries)
        {
            Assert.False(
                SaveChecksum.TryUnwrap(entry.Value, out var payload, out _),
                $"{fixture} key '{entry.Key}' was treated as checksummed");
            Assert.Equal(entry.Value, payload);
        }
    }

    private static byte[] EncodeWithBom(string xml)
    {
        var encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        return encoding.GetPreamble().Concat(encoding.GetBytes(xml)).ToArray();
    }

    private static string RemoveVersionElement(string xml)
    {
        var start = xml.IndexOf("<Version ", StringComparison.Ordinal);
        var end = xml.IndexOf("</Version>", StringComparison.Ordinal) + "</Version>".Length;
        return xml[..start] + xml[end..];
    }
}
