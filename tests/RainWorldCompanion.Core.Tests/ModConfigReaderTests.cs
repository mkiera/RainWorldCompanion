using RainWorldCompanion.Core.Mods;

namespace RainWorldCompanion.Tests;

/// <summary>
/// Finding the mod settings that travel with a save. What travels is deliberately narrower than
/// what a backup covers, so most of these are about the line between the two.
/// </summary>
public class ModConfigReaderTests
{
    private static ModConfigScan ReadPopulated(TempDirectory directory)
    {
        ModConfigs.Populate(directory);
        return ModConfigReader.Read(directory.Path);
    }

    // ---- what travels ----

    [Fact]
    public void Every_settings_file_the_game_writes_travels()
    {
        using var live = new TempDirectory("live");

        ModConfigScan scan = ReadPopulated(live);

        Assert.True(scan.ReadTheFolder);
        Assert.Equal(
            SaveTree.Sorted(ModConfigs.Travelling),
            SaveTree.Sorted(scan.Files.Select(f => f.RelativePath)));
    }

    [Fact]
    public void The_whole_DvrmentConfs_tree_travels()
    {
        using var live = new TempDirectory("live");

        var found = SaveTree.Sorted(ReadPopulated(live).Files.Select(f => f.RelativePath));

        Assert.Contains(@"ModConfigs\DvrmentConfs\current.json", found);
        Assert.Contains(@"ModConfigs\DvrmentConfs\Preset-kieracustom.txt", found);
    }

    /// <summary>
    /// A backup takes this, because it is the player's own folder. It does not travel, because a
    /// bundle goes to somebody else and a mod's own folder can hold anything.
    /// </summary>
    [Fact]
    public void A_mods_own_folder_is_backed_up_but_does_not_travel()
    {
        using var live = new TempDirectory("live");
        ModConfigs.Populate(live);

        Assert.False(ModConfigReader.Travels(@"ModConfigs\MapOptions\cache.json"));
        Assert.True(new Core.Backups.BackupScope(live.Path).IsInScope(@"ModConfigs\MapOptions\cache.json"));
    }

    [Fact]
    public void A_settings_file_that_is_not_a_txt_does_not_travel()
    {
        Assert.False(ModConfigReader.Travels(@"ModConfigs\willowwisp.bellyplus.json"));
        Assert.True(ModConfigReader.Travels(@"ModConfigs\willowwisp.bellyplus.txt"));
    }

    [Theory]
    [InlineData(@"ModConfigs\steam_autocloud.vdf")]
    [InlineData(@"ModConfigs\DvrmentConfs\steam_autocloud.vdf")]
    public void Steam_cloud_state_never_travels(string relativePath)
    {
        Assert.False(ModConfigReader.Travels(relativePath));
    }

    [Theory]
    [InlineData("sav")]
    [InlineData(@"dvrmentSaveStates\contents_0_White_story.txt")]
    [InlineData("ModConfigs")]
    [InlineData(@"Other\devourment.txt")]
    [InlineData("")]
    [InlineData(null)]
    public void Nothing_outside_ModConfigs_travels(string? relativePath)
    {
        Assert.False(ModConfigReader.Travels(relativePath));
    }

    [Fact]
    public void A_path_that_climbs_out_of_the_folder_does_not_travel()
    {
        Assert.False(ModConfigReader.Travels(@"ModConfigs\..\..\sav"));
        Assert.Equal("", ModConfigReader.ModIdFor(@"ModConfigs\..\..\sav"));
    }

    [Fact]
    public void Either_separator_reads_the_same()
    {
        Assert.True(ModConfigReader.Travels("ModConfigs/devourment.txt"));
        Assert.Equal("devourment", ModConfigReader.ModIdFor("ModConfigs/devourment.txt"));
    }

    /// <summary>
    /// The predicate and the walk must agree, or a file travels in one place and not the other.
    /// </summary>
    [Fact]
    public void Travels_agrees_with_Read_for_every_file_in_the_folder()
    {
        using var live = new TempDirectory("live");
        ModConfigs.Populate(live);

        var found = new HashSet<string>(
            ModConfigReader.Read(live.Path).Files.Select(f => f.RelativePath),
            StringComparer.OrdinalIgnoreCase);

        foreach (string relative in ModConfigs.Travelling.Concat(ModConfigs.StaysBehind))
        {
            Assert.Equal(ModConfigReader.Travels(relative), found.Contains(relative));
        }
    }

    // ---- which mod a file belongs to ----

    /// <summary>
    /// Remix writes ModConfigs\&lt;mod id&gt;.txt, so the name is the id, and that is the id the
    /// mod list records. It works for a mod this app has never heard of.
    /// </summary>
    [Theory]
    [InlineData(@"ModConfigs\devourment.txt", "devourment")]
    [InlineData(@"ModConfigs\henpemaz_rainmeadow.txt", "henpemaz_rainmeadow")]
    [InlineData(@"ModConfigs\willowwisp.bellyplus.txt", "willowwisp.bellyplus")]
    public void A_settings_file_is_attributed_to_the_mod_id_its_name_is(string relativePath, string expected)
    {
        Assert.Equal(expected, ModConfigReader.ModIdFor(relativePath));
    }

    /// <summary>Nothing about the folder name says Devourment, so it is named here.</summary>
    [Fact]
    public void Everything_under_DvrmentConfs_belongs_to_Devourment()
    {
        using var live = new TempDirectory("live");

        var devourment = ReadPopulated(live).Files
            .Where(f => f.ModId == ModConfigReader.DevourmentModId)
            .Select(f => f.RelativePath)
            .ToList();

        Assert.Contains(@"ModConfigs\devourment.txt", devourment);
        Assert.Contains(@"ModConfigs\DvrmentConfs\current.json", devourment);
        Assert.Contains(@"ModConfigs\DvrmentConfs\Preset-kieracustom.txt", devourment);
    }

    // ---- the three empty answers ----

    /// <summary>
    /// A folder with no mod settings is a real answer. A folder nobody could look at is not, and
    /// the two must never be shown the same way.
    /// </summary>
    [Fact]
    public void A_folder_with_no_ModConfigs_read_the_folder_and_found_nothing()
    {
        using var live = new TempDirectory("live");

        ModConfigScan scan = ModConfigReader.Read(live.Path);

        Assert.True(scan.ReadTheFolder);
        Assert.Empty(scan.Files);
        Assert.Null(scan.Note);
    }

    [Fact]
    public void No_save_folder_reads_nothing_and_says_so()
    {
        ModConfigScan scan = ModConfigReader.Read(null);

        Assert.False(scan.ReadTheFolder);
        Assert.Empty(scan.Files);
        Assert.NotNull(scan.Note);
    }

    [Fact]
    public void A_folder_that_is_not_there_reads_nothing_rather_than_throwing()
    {
        ModConfigScan scan = ModConfigReader.Read(@"Z:\no\such\folder");

        Assert.True(scan.ReadTheFolder);
        Assert.Empty(scan.Files);
    }

    // ---- links ----

    /// <summary>
    /// A junction inside ModConfigs would otherwise pull files from anywhere on the machine into a
    /// bundle the player then hands to somebody else.
    /// </summary>
    [JunctionFact]
    public void A_junctioned_settings_folder_is_left_alone_and_named()
    {
        using var live = new TempDirectory("live");
        using var elsewhere = new TempDirectory("elsewhere");
        elsewhere.WriteText("secret.txt", "not the player's mod settings");

        live.WriteText(@"ModConfigs\devourment.txt", ModConfigs.SampleConfig);
        Links.TryCreateDirectoryJunction(
            live.Resolve(@"ModConfigs\DvrmentConfs"), elsewhere.Path);

        ModConfigScan scan = ModConfigReader.Read(live.Path);

        Assert.Equal(@"ModConfigs\devourment.txt", Assert.Single(scan.Files).RelativePath);
        Assert.Contains(@"ModConfigs\DvrmentConfs", scan.SkippedLinks);
        Assert.NotNull(scan.Note);
    }

    // ---- settings that belong to one machine ----

    [Fact]
    public void A_settings_file_carrying_a_screen_size_names_those_keys()
    {
        using var live = new TempDirectory("live");
        string path = live.WriteText("SBCameraScroll.txt", ModConfigs.ConfigWithDisplaySettings);

        var keys = ModConfigNotes.MachineSpecificKeys(path);

        Assert.Equal(new[] { "customResolution", "resolution", "fullScreenEffects" }, keys);
    }

    [Fact]
    public void An_ordinary_settings_file_names_nothing()
    {
        using var live = new TempDirectory("live");
        string path = live.WriteText("devourment.txt", ModConfigs.SampleConfig);

        Assert.Empty(ModConfigNotes.MachineSpecificKeys(path));
    }

    /// <summary>A comment is not a setting, and a value is not a key.</summary>
    [Fact]
    public void Only_the_key_side_of_a_line_is_matched()
    {
        using var live = new TempDirectory("live");
        string path = live.WriteText(
            "mod.txt",
            "# resolution is not set here\nfavouriteWord = resolution\nscrollSpeed = 0.4\n");

        Assert.Empty(ModConfigNotes.MachineSpecificKeys(path));
    }

    [Fact]
    public void A_file_that_cannot_be_read_names_nothing_rather_than_throwing()
    {
        Assert.Empty(ModConfigNotes.MachineSpecificKeys(@"Z:\no\such\file.txt"));
        Assert.Empty(ModConfigNotes.MachineSpecificKeys(null));
    }
}
