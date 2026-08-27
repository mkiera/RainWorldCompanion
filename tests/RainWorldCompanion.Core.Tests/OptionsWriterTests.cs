using RainWorldCompanion.Core.Editing;
using RainWorldCompanion.Core.Mods;
using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Tests;

public class OptionsWriterTests
{
    private static readonly Dictionary<string, int> NoOrder = new(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void The_enabled_list_reads_back_as_it_was_written()
    {
        using var directory = new TempDirectory();
        byte[] before = OptionsFixture.Bytes(OptionsFixture.Payload(
            OptionsFixture.Enabled("moreslugcats", "devourment"),
            OptionsFixture.Record("ScreenResolution", "1")));

        byte[] after = OptionsWriter.Rewrite(before, new[] { "moreslugcats", "warp", "devourment" }, NoOrder);

        directory.WriteBytes("options", after);
        OptionsRead read = OptionsFile.Read(directory.Path);

        Assert.True(read.Read);

        // The two it already had stay where the game put them, and the new one goes on the end.
        Assert.Equal(new[] { "moreslugcats", "devourment", "warp" }, read.EnabledModIds);
    }

    [Fact]
    public void Every_other_setting_in_the_real_options_file_survives_the_write()
    {
        byte[] before = FixtureFiles.Bytes(FixtureFiles.Options);

        byte[] after = OptionsWriter.Rewrite(before, new[] { "warp" }, new Dictionary<string, int> { ["warp"] = 3 });

        List<string> kept = OtherRecords(BlobOf(before));
        List<string> still = OtherRecords(BlobOf(after));

        Assert.NotEmpty(kept);
        Assert.Equal(kept, still);
    }

    [Fact]
    public void The_written_file_is_still_a_container_the_ordinary_reader_accepts()
    {
        using var directory = new TempDirectory();
        byte[] before = FixtureFiles.Bytes(FixtureFiles.Options);

        directory.WriteBytes("options", OptionsWriter.Rewrite(before, new[] { "warp" }, NoOrder));

        Assert.True(SaveContainer.TryRead(directory.Resolve("options"), out SaveContainer? container, out string? error), error);
        Assert.Null(container!.StructureProblem);
    }

    [Fact]
    public void A_named_position_is_updated_where_it_sits_and_the_others_are_left_alone()
    {
        byte[] before = OptionsFixture.Bytes(OptionsFixture.Payload(
            OptionsFixture.Enabled("a"),
            OptionsFixture.LoadOrder(("a", "1"), ("b", "20"), ("c", "7"))));

        byte[] after = OptionsWriter.Rewrite(before, new[] { "a", "b" }, new Dictionary<string, int> { ["b"] = 4 });

        Assert.Equal(
            "a" + OptionsFixture.PairSeparator + "1"
            + OptionsFixture.ListSeparator + "b" + OptionsFixture.PairSeparator + "4"
            + OptionsFixture.ListSeparator + "c" + OptionsFixture.PairSeparator + "7",
            RecordValue(BlobOf(after), "ModLoadOrder"));
    }

    [Fact]
    public void A_mod_the_order_has_never_named_is_appended_rather_than_dropped()
    {
        byte[] before = OptionsFixture.Bytes(OptionsFixture.Payload(
            OptionsFixture.Enabled("a"),
            OptionsFixture.LoadOrder(("a", "1"))));

        byte[] after = OptionsWriter.Rewrite(before, new[] { "a", "new" }, new Dictionary<string, int> { ["new"] = 9 });

        Assert.Equal(
            "a" + OptionsFixture.PairSeparator + "1"
            + OptionsFixture.ListSeparator + "new" + OptionsFixture.PairSeparator + "9",
            RecordValue(BlobOf(after), "ModLoadOrder"));
    }

    [Fact]
    public void An_order_nothing_asked_to_change_comes_out_character_for_character()
    {
        byte[] before = OptionsFixture.Bytes(OptionsFixture.Payload(
            OptionsFixture.Enabled("a"),
            OptionsFixture.LoadOrder(("a", "1"), ("gone", "20"))));

        byte[] after = OptionsWriter.Rewrite(before, new[] { "a", "b" }, NoOrder);

        Assert.Equal(RecordValue(BlobOf(before), "ModLoadOrder"), RecordValue(BlobOf(after), "ModLoadOrder"));
    }

    [Fact]
    public void Turning_everything_off_leaves_an_empty_list_rather_than_no_record()
    {
        using var directory = new TempDirectory();
        byte[] before = OptionsFixture.Bytes(OptionsFixture.Payload(
            OptionsFixture.Enabled("a", "b"),
            OptionsFixture.Record("ScreenResolution", "1")));

        directory.WriteBytes("options", OptionsWriter.Rewrite(before, Array.Empty<string>(), NoOrder));
        OptionsRead read = OptionsFile.Read(directory.Path);

        Assert.True(read.Read);
        Assert.Empty(read.EnabledModIds);
        Assert.Equal("1", RecordValue(BlobOf(directory.ReadBytes("options")), "ScreenResolution"));
    }

    [Fact]
    public void A_vanilla_file_that_never_had_an_enabled_list_gains_one()
    {
        using var directory = new TempDirectory();
        byte[] before = OptionsFixture.Bytes(OptionsFixture.Payload(
            OptionsFixture.Record("ScreenResolution", "1")));

        directory.WriteBytes("options", OptionsWriter.Rewrite(before, new[] { "moreslugcats" }, NoOrder));
        OptionsRead read = OptionsFile.Read(directory.Path);

        Assert.Equal(new[] { "moreslugcats" }, read.EnabledModIds);
        Assert.Equal("1", RecordValue(BlobOf(directory.ReadBytes("options")), "ScreenResolution"));
    }

    [Fact]
    public void Bytes_that_are_not_a_container_are_refused_rather_than_written_over()
        => Assert.Throws<SaveContainerException>(
            () => OptionsWriter.Rewrite(SyntheticSave.GarbageBytes(), new[] { "a" }, NoOrder));

    [Fact]
    public void Writing_the_list_the_real_file_already_holds_changes_no_bytes()
    {
        byte[] before = FixtureFiles.Bytes(FixtureFiles.Options);
        OptionsRead read = OptionsFile.ReadFile(FixtureFiles.PathTo(FixtureFiles.Options));

        byte[] after = OptionsWriter.Rewrite(before, read.EnabledModIds, NoOrder);

        Assert.Equal(before, after);
    }

    [Fact]
    public void A_mod_turned_off_leaves_the_others_where_the_game_wrote_them()
    {
        byte[] before = OptionsFixture.Bytes(OptionsFixture.Payload(
            OptionsFixture.Enabled("a", "b", "c")));

        byte[] after = OptionsWriter.Rewrite(before, new[] { "c", "a" }, NoOrder);

        Assert.Equal(
            "a" + OptionsFixture.ListSeparator + "c",
            RecordValue(BlobOf(after), "EnabledMods"));
    }

    private static string BlobOf(byte[] bytes) => ContainerText.Load(bytes).GetValue(OptionsFile.ContainerKey);

    private static List<string> OtherRecords(string blob)
        => blob.Split(OptionsFixture.RecordSeparator, StringSplitOptions.None)
            .Where(record => record.Length > 0)
            .Where(record => !record.StartsWith("EnabledMods" + OptionsFixture.KeyValueSeparator, StringComparison.Ordinal))
            .Where(record => !record.StartsWith("ModLoadOrder" + OptionsFixture.KeyValueSeparator, StringComparison.Ordinal))
            .ToList();

    private static string? RecordValue(string blob, string key)
        => blob.Split(OptionsFixture.RecordSeparator, StringSplitOptions.None)
            .Where(record => record.StartsWith(key + OptionsFixture.KeyValueSeparator, StringComparison.Ordinal))
            .Select(record => record[(key.Length + OptionsFixture.KeyValueSeparator.Length)..])
            .FirstOrDefault();
}
