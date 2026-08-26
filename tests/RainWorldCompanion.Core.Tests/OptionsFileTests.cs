using RainWorldCompanion.Core.Mods;

namespace RainWorldCompanion.Tests;

/// <summary>
/// Reading which mods the game has turned on, out of the options file in the save folder.
/// </summary>
public class OptionsFileTests
{
    // ---- the real file ----

    /// <summary>
    /// The fixture is a copy of a real options file from a modded install, so this is the whole
    /// reader run against game output rather than against a shape invented here.
    /// </summary>
    [Fact]
    public void Reads_the_mods_out_of_a_real_options_file()
    {
        using var directory = new TempDirectory();
        FixtureFiles.CopyTo(directory, FixtureFiles.Options, "options");

        OptionsRead read = OptionsFile.Read(directory.Path);

        Assert.True(read.Read);
        Assert.Null(read.Problem);
        Assert.Equal("v1.11.8", read.LastGameVersion);
        Assert.Equal(22, read.EnabledModIds.Count);
        Assert.Equal("devourment", read.EnabledModIds[0]);
        Assert.Contains("henpemaz_rainmeadow", read.EnabledModIds);
        Assert.Equal(0, read.LoadOrder["slime-cubed.devconsole"]);
        Assert.Equal(16, read.LoadOrder["devourment"]);
    }

    /// <summary>
    /// The game leaves an entry in the load order after a mod is turned off. The dictionary comes
    /// back as written so the caller decides what to do with the leftovers, and this file has six
    /// of them.
    /// </summary>
    [Fact]
    public void Keeps_load_order_entries_for_mods_that_are_no_longer_on()
    {
        using var directory = new TempDirectory();
        FixtureFiles.CopyTo(directory, FixtureFiles.Options, "options");

        OptionsRead read = OptionsFile.Read(directory.Path);

        Assert.DoesNotContain("dressmyslugcat", read.EnabledModIds);
        Assert.True(read.LoadOrder.ContainsKey("dressmyslugcat"));
        Assert.Equal(28, read.LoadOrder.Count);
    }

    // ---- the grammar ----

    [Fact]
    public void Reads_ids_in_the_order_they_were_written()
    {
        using var directory = new TempDirectory();
        OptionsFixture.WriteInto(directory, OptionsFixture.Payload(
            OptionsFixture.Enabled("second", "first", "third")));

        OptionsRead read = OptionsFile.Read(directory.Path);

        Assert.True(read.Read);
        Assert.Equal(new[] { "second", "first", "third" }, read.EnabledModIds);
    }

    [Fact]
    public void Reads_the_load_order_and_the_game_version()
    {
        using var directory = new TempDirectory();
        OptionsFixture.WriteInto(directory, OptionsFixture.Payload(
            OptionsFixture.Enabled("one", "two"),
            OptionsFixture.LoadOrder(("one", "4"), ("two", "0")),
            OptionsFixture.Record("LastGameVersion", "v1.11.8")));

        OptionsRead read = OptionsFile.Read(directory.Path);

        Assert.Equal(4, read.LoadOrder["one"]);
        Assert.Equal(0, read.LoadOrder["two"]);
        Assert.Equal("v1.11.8", read.LastGameVersion);
    }

    /// <summary>
    /// A vanilla install writes no EnabledMods record rather than an empty one. That has to read
    /// as "nothing was on", which is an answer, and not as a file we failed to understand.
    /// </summary>
    [Fact]
    public void No_enabled_mods_record_reads_as_a_vanilla_install()
    {
        using var directory = new TempDirectory();
        OptionsFixture.WriteInto(directory, OptionsFixture.Payload(
            OptionsFixture.Record("SaveSlot", "1"),
            OptionsFixture.Record("LastGameVersion", "v1.11.8")));

        OptionsRead read = OptionsFile.Read(directory.Path);

        Assert.True(read.Read);
        Assert.Null(read.Problem);
        Assert.Empty(read.EnabledModIds);
        Assert.Equal("v1.11.8", read.LastGameVersion);
    }

    /// <summary>
    /// InputSetup is written once per player, so repeated keys are normal here. Taking the first
    /// keeps a later record from quietly replacing what was already read.
    /// </summary>
    [Fact]
    public void A_repeated_key_is_read_once()
    {
        using var directory = new TempDirectory();
        OptionsFixture.WriteInto(directory, OptionsFixture.Payload(
            OptionsFixture.Enabled("first"),
            OptionsFixture.Enabled("second")));

        OptionsRead read = OptionsFile.Read(directory.Path);

        Assert.Equal(new[] { "first" }, read.EnabledModIds);
    }

    /// <summary>A position that will not parse costs that mod its place, not the whole list.</summary>
    [Fact]
    public void A_load_order_position_that_is_not_a_number_is_skipped()
    {
        using var directory = new TempDirectory();
        OptionsFixture.WriteInto(directory, OptionsFixture.Payload(
            OptionsFixture.LoadOrder(("good", "3"), ("bad", "later"), ("alsogood", "5"))));

        OptionsRead read = OptionsFile.Read(directory.Path);

        Assert.True(read.Read);
        Assert.False(read.LoadOrder.ContainsKey("bad"));
        Assert.Equal(3, read.LoadOrder["good"]);
        Assert.Equal(5, read.LoadOrder["alsogood"]);
    }

    [Fact]
    public void A_blank_game_version_reads_as_no_game_version()
    {
        using var directory = new TempDirectory();
        OptionsFixture.WriteInto(directory, OptionsFixture.Payload(
            OptionsFixture.Record("LastGameVersion", "   ")));

        Assert.Null(OptionsFile.Read(directory.Path).LastGameVersion);
    }

    // ---- when there is nothing to read ----

    [Fact]
    public void No_save_folder_reads_nothing_and_says_so()
    {
        OptionsRead read = OptionsFile.Read(null);

        Assert.False(read.Read);
        Assert.NotNull(read.Problem);
        Assert.Empty(read.EnabledModIds);
        Assert.Empty(read.LoadOrder);
    }

    [Fact]
    public void A_save_folder_with_no_options_file_reads_nothing_and_says_so()
    {
        using var directory = new TempDirectory();

        OptionsRead read = OptionsFile.Read(directory.Path);

        Assert.False(read.Read);
        Assert.Contains("no options file", read.Problem);
    }

    [Fact]
    public void A_file_that_is_not_a_container_reads_nothing_and_says_so()
    {
        using var directory = new TempDirectory();
        directory.WriteText("options", "this is not a save container");

        OptionsRead read = OptionsFile.Read(directory.Path);

        Assert.False(read.Read);
        Assert.NotNull(read.Problem);
    }

    /// <summary>
    /// A container with no settings entry is readable and holds nothing we want, which is worth
    /// telling apart from a file we could not open at all.
    /// </summary>
    [Fact]
    public void A_container_without_a_settings_entry_reads_nothing_and_says_so()
    {
        using var directory = new TempDirectory();
        FixtureFiles.CopyTo(directory, FixtureFiles.Exp1, "options");

        OptionsRead read = OptionsFile.Read(directory.Path);

        Assert.False(read.Read);
        Assert.Contains("no settings entry", read.Problem);
    }
}
