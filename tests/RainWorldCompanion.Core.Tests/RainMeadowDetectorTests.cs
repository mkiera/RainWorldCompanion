using RainWorldCompanion.Core.System;

namespace RainWorldCompanion.Tests;

/// <summary>
/// Whether the window shows its Rain Meadow section at all hangs on this, so the cases that matter
/// are the ones where the game folder is unknown or unreadable. Those must still find the mod when
/// the save folder proves it is in use.
/// </summary>
public class RainMeadowDetectorTests
{
    private const string MeadowInfo = """{"id":"henpemaz_rainmeadow","name":"Rain Meadow","version":"0.1.15.1"}""";
    private const string OtherInfo = """{"id":"someone_else","name":"Other Mod","version":"1.0"}""";

    private static string Game(TempDirectory dir) =>
        System.IO.Path.Combine(dir.Path, "game");

    private static string Save(TempDirectory dir) =>
        System.IO.Path.Combine(dir.Path, "save");

    private static void WriteEnabledList(TempDirectory dir, params string[] lines)
    {
        string path = System.IO.Path.Combine(Game(dir), "RainWorld_Data", "StreamingAssets", "enabledMods.txt");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllLines(path, lines);
    }

    private static string WriteWorkshopMod(TempDirectory dir, string id, string info)
    {
        string folder = System.IO.Path.Combine(dir.Path, "workshop", id);
        System.IO.Directory.CreateDirectory(folder);
        System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "modinfo.json"), info);
        return folder;
    }

    private static void WriteSaveFile(TempDirectory dir, string relative, string content = "x")
    {
        string path = System.IO.Path.Combine(Save(dir), relative);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
    }

    [Fact]
    public void The_enabled_mod_list_is_the_authority_when_the_game_folder_is_known()
    {
        using var dir = new TempDirectory();
        string folder = WriteWorkshopMod(dir, "3388224007", MeadowInfo);
        WriteEnabledList(dir, "[WORKSHOP]" + folder);

        RainMeadowPresence presence = RainMeadowDetector.Detect(Save(dir), Game(dir));

        Assert.True(presence.Present);
        Assert.True(presence.Enabled);
        Assert.Equal("0.1.15.1", presence.Version);
    }

    [Fact]
    public void A_local_mod_folder_named_in_the_list_is_resolved_too()
    {
        using var dir = new TempDirectory();
        string mods = System.IO.Path.Combine(Game(dir), "RainWorld_Data", "StreamingAssets", "mods", "meadow-local");
        System.IO.Directory.CreateDirectory(mods);
        System.IO.File.WriteAllText(System.IO.Path.Combine(mods, "modinfo.json"), MeadowInfo);
        WriteEnabledList(dir, "meadow-local");

        RainMeadowPresence presence = RainMeadowDetector.Detect(Save(dir), Game(dir));

        Assert.True(presence.Enabled);
    }

    [Fact]
    public void A_list_that_names_only_other_mods_reports_absent()
    {
        using var dir = new TempDirectory();
        string folder = WriteWorkshopMod(dir, "111", OtherInfo);
        WriteEnabledList(dir, "[WORKSHOP]" + folder);

        RainMeadowPresence presence = RainMeadowDetector.Detect(Save(dir), Game(dir));

        Assert.False(presence.Present);
        Assert.False(presence.Enabled);
    }

    [Theory]
    [InlineData("meadow.json")]
    [InlineData(@"ModConfigs\henpemaz_rainmeadow.txt")]
    [InlineData("online_sav")]
    [InlineData("online_sav2")]
    [InlineData("online_sav3")]
    public void Save_folder_evidence_alone_is_enough_when_the_game_folder_is_unknown(string relative)
    {
        using var dir = new TempDirectory();
        WriteSaveFile(dir, relative);

        RainMeadowPresence presence = RainMeadowDetector.Detect(Save(dir), null);

        Assert.True(presence.Present);
        Assert.False(presence.Enabled);
        Assert.Contains(relative, presence.Reason);
    }

    [Fact]
    public void Save_folder_evidence_wins_over_a_game_list_that_does_not_mention_the_mod()
    {
        // The player turned Rain Meadow off but their online saves are still there, so the section
        // must stay visible or those files become unreachable from the app.
        using var dir = new TempDirectory();
        string folder = WriteWorkshopMod(dir, "111", OtherInfo);
        WriteEnabledList(dir, "[WORKSHOP]" + folder);
        WriteSaveFile(dir, "online_sav2");

        RainMeadowPresence presence = RainMeadowDetector.Detect(Save(dir), Game(dir));

        Assert.True(presence.Present);
        Assert.False(presence.Enabled);
    }

    [Fact]
    public void Nothing_anywhere_reports_absent()
    {
        using var dir = new TempDirectory();
        System.IO.Directory.CreateDirectory(Save(dir));

        RainMeadowPresence presence = RainMeadowDetector.Detect(Save(dir), Game(dir));

        Assert.False(presence.Present);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    public void Empty_paths_do_not_throw(string? save, string? game) =>
        Assert.False(RainMeadowDetector.Detect(save, game).Present);

    [Fact]
    public void A_missing_game_folder_falls_back_rather_than_throwing()
    {
        using var dir = new TempDirectory();
        WriteSaveFile(dir, "meadow.json");

        RainMeadowPresence presence = RainMeadowDetector.Detect(
            Save(dir),
            System.IO.Path.Combine(dir.Path, "does", "not", "exist"));

        Assert.True(presence.Present);
    }

    [Fact]
    public void A_malformed_modinfo_is_skipped_rather_than_throwing()
    {
        using var dir = new TempDirectory();
        string folder = WriteWorkshopMod(dir, "222", "{ this is not json");
        WriteEnabledList(dir, "[WORKSHOP]" + folder);

        RainMeadowPresence presence = RainMeadowDetector.Detect(Save(dir), Game(dir));

        Assert.False(presence.Present);
    }

    [Fact]
    public void A_workshop_entry_whose_folder_is_gone_is_skipped()
    {
        using var dir = new TempDirectory();
        WriteEnabledList(dir, @"[WORKSHOP]C:\nowhere\12345", "");

        RainMeadowPresence presence = RainMeadowDetector.Detect(Save(dir), Game(dir));

        Assert.False(presence.Present);
    }

    [Fact]
    public void The_mod_id_match_ignores_case()
    {
        using var dir = new TempDirectory();
        string folder = WriteWorkshopMod(dir, "333", """{"id":"HENPEMAZ_RAINMEADOW","version":"9"}""");
        WriteEnabledList(dir, "[WORKSHOP]" + folder);

        Assert.True(RainMeadowDetector.Detect(Save(dir), Game(dir)).Enabled);
    }

    [Fact]
    public void A_mod_with_no_version_still_counts_as_present()
    {
        using var dir = new TempDirectory();
        string folder = WriteWorkshopMod(dir, "444", """{"id":"henpemaz_rainmeadow"}""");
        WriteEnabledList(dir, "[WORKSHOP]" + folder);

        RainMeadowPresence presence = RainMeadowDetector.Detect(Save(dir), Game(dir));

        Assert.True(presence.Enabled);
        Assert.Null(presence.Version);
    }
}
