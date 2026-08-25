using System.Text;

using RainWorldSaveManager.Core.Editing;
using RainWorldSaveManager.Core.Saves;

namespace RainWorldSaveManager.Tests;

/// <summary>
/// The writer is the first code in this app that can destroy a save, because a container it
/// rebuilds wrongly is one the game answers by wiping the slot. These tests hold it to the only
/// standard worth having: a file it loads and writes back unedited is the same bytes, and a file
/// it edits differs only where the edit was.
/// </summary>
public class ContainerTextTests
{
    public static TheoryData<string> AllFixtures => new()
    {
        FixtureFiles.Sav2,
        FixtureFiles.Sav3,
        FixtureFiles.OnlineSav,
        FixtureFiles.Options,
        FixtureFiles.ExpCore1,
        FixtureFiles.Exp1,
    };

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Loading_and_writing_a_fixture_back_returns_the_same_bytes(string fixture)
    {
        var original = FixtureFiles.Bytes(fixture);

        var written = ContainerText.Load(original).ToBytes();

        Assert.Equal(original, written);
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Replacing_every_value_with_itself_returns_the_same_bytes(string fixture)
    {
        var original = FixtureFiles.Bytes(fixture);
        var container = ContainerText.Load(original);

        foreach (var key in container.Keys)
        {
            container = container.WithValue(key, container.GetValue(key));
        }

        Assert.Equal(original, container.ToBytes());
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Values_decode_to_what_an_independent_parser_reads(string fixture)
    {
        var expected = FixtureFiles.ReadEntries(fixture);
        var container = ContainerText.Load(FixtureFiles.Bytes(fixture));

        Assert.Equal(
            expected.Keys.OrderBy(k => k, StringComparer.Ordinal),
            container.Keys.OrderBy(k => k, StringComparer.Ordinal));

        foreach (var entry in expected)
        {
            Assert.Equal(entry.Value, container.GetValue(entry.Key));
        }
    }

    /// <summary>
    /// The escape table is only complete if re-escaping what a real file holds lands back on the
    /// characters that file stores. Anything the game escapes and this does not would show up
    /// here as a difference, before it could show up as a corrupted save.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Re_escaping_a_stored_value_reproduces_it_exactly(string fixture)
    {
        var container = ContainerText.Load(FixtureFiles.Bytes(fixture));

        foreach (var key in container.Keys)
        {
            var raw = container.GetValueRaw(key);

            Assert.Equal(raw, XmlValueText.Escape(XmlValueText.Unescape(raw)));
        }
    }

    [Fact]
    public void Keys_come_back_in_the_order_the_file_stores_them()
    {
        var container = ContainerText.Load(FixtureFiles.Bytes(FixtureFiles.Sav2));

        // Not alphabetical, and not the order a dictionary would hand back. A writer that assumed
        // either would pair a key with the wrong value.
        Assert.Equal(new[] { "save__Backup", "save" }, container.Keys);
    }

    [Fact]
    public void A_container_with_no_keys_loads_and_writes_back_unchanged()
    {
        var original = FixtureFiles.Bytes(FixtureFiles.Exp1);

        var container = ContainerText.Load(original);

        Assert.Empty(container.Keys);
        Assert.Equal(original, container.ToBytes());
    }

    [Fact]
    public void Editing_one_value_leaves_every_other_value_untouched()
    {
        var container = ContainerText.Load(FixtureFiles.Bytes(FixtureFiles.Sav2));
        var backupBefore = container.GetValueRaw("save__Backup");

        var edited = container.WithValue("save", "replaced");

        Assert.Equal("replaced", edited.GetValue("save"));
        Assert.Equal(backupBefore, edited.GetValueRaw("save__Backup"));
    }

    [Fact]
    public void An_edited_value_is_stored_escaped_and_reads_back_as_written()
    {
        var payload = "SAVE STATE<progDivB>CYCLENUM<svB>20<svA>NOTE<svB>a & b";

        var edited = ContainerText.Load(FixtureFiles.Bytes(FixtureFiles.Sav2)).WithValue("save", payload);

        Assert.Equal(payload, edited.GetValue("save"));

        // The delimiters have to be on disk as entities, because that is what the file they are
        // going into does with them everywhere else.
        var onDisk = Encoding.UTF8.GetString(edited.ToBytes());
        Assert.Contains("CYCLENUM&lt;svB&gt;20", onDisk, StringComparison.Ordinal);
        Assert.Contains("a &amp; b", onDisk, StringComparison.Ordinal);
        Assert.DoesNotContain("CYCLENUM<svB>", onDisk, StringComparison.Ordinal);
    }

    [Fact]
    public void An_edit_the_file_still_has_room_for_keeps_the_file_length()
    {
        var original = FixtureFiles.Bytes(FixtureFiles.Sav2);
        var container = ContainerText.Load(original);

        var written = container.WithValue("save", container.GetValue("save") + "<svA>PADDINGEATER<svB>1").ToBytes();

        Assert.Equal(original.Length, written.Length);
    }

    [Fact]
    public void The_result_still_parses_as_the_container_it_was()
    {
        var container = ContainerText.Load(FixtureFiles.Bytes(FixtureFiles.Sav2));
        var payload = SyntheticSave.SavePayload(cycle: 41);

        var bytes = container.WithValue("save", SyntheticSave.Wrap(payload)).ToBytes();

        using var temp = new TempDirectory();
        var path = temp.WriteBytes("sav2", bytes);
        var reread = SaveContainer.Read(path);

        Assert.Null(reread.StructureProblem);
        Assert.Equal(container.GetValue("save__Backup"), reread.Entries["save__Backup"]);
        Assert.True(SaveChecksum.TryUnwrap(reread.Entries["save"], out var storedPayload, out var checksumValid));
        Assert.True(checksumValid);
        Assert.Equal(payload, storedPayload);
    }

    [Fact]
    public void A_value_too_long_for_the_file_grows_it_rather_than_truncating()
    {
        var original = FixtureFiles.Bytes(FixtureFiles.Sav2);
        var container = ContainerText.Load(original);
        var oversized = new string('x', original.Length + 1000);

        var written = container.WithValue("save", oversized).ToBytes(SizePolicy.GrowIfNeeded);

        Assert.True(written.Length > original.Length);
        Assert.Equal(oversized, ContainerText.Load(written).GetValue("save"));
    }

    [Fact]
    public void Preserving_the_length_refuses_a_value_that_no_longer_fits()
    {
        var container = ContainerText.Load(FixtureFiles.Bytes(FixtureFiles.Sav2));
        var oversized = new string('x', 200_000);

        var edited = container.WithValue("save", oversized);

        Assert.Throws<SaveContainerException>(() => edited.ToBytes(SizePolicy.PreserveLength));
    }

    [Fact]
    public void Bytes_without_a_closing_tag_are_refused()
    {
        var bytes = SyntheticSave.BytesWithoutClosingTag(new[] { SyntheticSave.Entry("save", "x") });

        Assert.Throws<SaveContainerException>(() => ContainerText.Load(bytes));
    }

    [Fact]
    public void Bytes_that_are_not_valid_utf8_are_refused_rather_than_rewritten()
    {
        // A container whose text has been damaged into an illegal byte sequence cannot be encoded
        // back to what it was, so loading it at all is the wrong answer.
        var bytes = SyntheticSave.Bytes(new[] { SyntheticSave.Entry("save", "value") });
        var index = Array.IndexOf(bytes, (byte)'v');
        bytes[index] = 0xC3;
        bytes[index + 1] = 0x28;

        Assert.Throws<SaveContainerException>(() => ContainerText.Load(bytes));
    }

    [Fact]
    public void Asking_for_a_key_the_container_does_not_have_is_refused()
    {
        var container = ContainerText.Load(FixtureFiles.Bytes(FixtureFiles.Sav2));

        Assert.Throws<SaveContainerException>(() => container.GetValue("nope"));
        Assert.Throws<SaveContainerException>(() => container.WithValue("nope", "x"));
        Assert.False(container.ContainsKey("nope"));
        Assert.True(container.ContainsKey("save"));
    }

    [Theory]
    [InlineData("plain text")]
    [InlineData("<svA>")]
    [InlineData("a & b < c > d")]
    [InlineData("&amp;")]
    [InlineData("carriage\rreturn")]
    [InlineData("quote \" and apostrophe '")]
    [InlineData("")]
    public void Escaping_then_unescaping_returns_the_original_text(string text)
    {
        Assert.Equal(text, XmlValueText.Unescape(XmlValueText.Escape(text)));
    }

    [Theory]
    [InlineData("&lt;", "<")]
    [InlineData("&gt;", ">")]
    [InlineData("&amp;", "&")]
    [InlineData("&quot;", "\"")]
    [InlineData("&apos;", "'")]
    [InlineData("&#xD;", "\r")]
    [InlineData("&#65;", "A")]
    [InlineData("bare & ampersand", "bare & ampersand")]
    [InlineData("&notanentity;", "&notanentity;")]
    public void Unescaping_reads_what_the_game_can_write(string stored, string expected)
    {
        Assert.Equal(expected, XmlValueText.Unescape(stored));
    }

    /// <summary>
    /// A carriage return has to leave as a numeric reference. Written literally it would be
    /// normalised to a newline by the next reader, which is a silent one-character edit to
    /// somebody's save.
    /// </summary>
    [Fact]
    public void A_carriage_return_is_escaped_numerically()
    {
        Assert.Equal("a&#xD;b", XmlValueText.Escape("a\rb"));
    }
}
