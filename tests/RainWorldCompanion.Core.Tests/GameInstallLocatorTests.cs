using RainWorldCompanion.Core.Settings;
using RainWorldCompanion.Core.System;

namespace RainWorldCompanion.Tests;

/// <summary>
/// Portraits are read out of the player's own game install and never copied into this repo, so
/// the locator has to work off whatever tree it is pointed at. Every test here builds its own
/// tree in a temp directory: none of them depends on Rain World being installed on the machine
/// running them, and none of them reads the live save folder.
/// </summary>
public class GameInstallLocatorTests
{
    private const string StreamingAssets = @"RainWorld_Data\StreamingAssets";
    private const string Illustrations = StreamingAssets + @"\illustrations";
    private const string MoreSlugcats = StreamingAssets + @"\mods\moreslugcats\illustrations";
    private const string WatcherMod = StreamingAssets + @"\mods\watcher\illustrations";

    [Fact]
    public void An_empty_folder_does_not_look_like_an_install()
    {
        using var temp = new TempDirectory("install");

        Assert.False(GameInstallLocator.LooksLikeInstall(temp.Path));
    }

    [Fact]
    public void A_folder_holding_the_streaming_assets_looks_like_an_install()
    {
        using var temp = new TempDirectory("install");
        temp.CreateSubdirectory(StreamingAssets);

        Assert.True(GameInstallLocator.LooksLikeInstall(temp.Path));
    }

    [Fact]
    public void A_folder_with_only_the_data_directory_does_not_look_like_an_install()
    {
        using var temp = new TempDirectory("install");
        temp.CreateSubdirectory("RainWorld_Data");

        Assert.False(GameInstallLocator.LooksLikeInstall(temp.Path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_path_does_not_look_like_an_install(string? path)
    {
        Assert.False(GameInstallLocator.LooksLikeInstall(path));
    }

    [Fact]
    public void A_path_that_does_not_exist_does_not_look_like_an_install()
    {
        using var temp = new TempDirectory("install");

        Assert.False(GameInstallLocator.LooksLikeInstall(temp.Resolve("nowhere")));
    }

    [Fact]
    public void A_portrait_in_the_base_illustrations_folder_is_found()
    {
        using var temp = new TempDirectory("install");
        var expected = WritePortrait(temp, Illustrations, "multiplayerportrait01-white.png");

        Assert.Equal(expected, GameInstallLocator.FindPortraitFile(temp.Path, "White"), ignoreCase: true);
    }

    [Fact]
    public void A_portrait_shipped_with_a_mod_is_found()
    {
        using var temp = new TempDirectory("install");
        var expected = WritePortrait(temp, MoreSlugcats, "multiplayerportrait41-gourmand.png");

        Assert.Equal(expected, GameInstallLocator.FindPortraitFile(temp.Path, "Gourmand"), ignoreCase: true);
    }

    [Fact]
    public void The_watcher_portrait_is_found_in_its_own_mod_folder()
    {
        using var temp = new TempDirectory("install");
        var expected = WritePortrait(temp, WatcherMod, "multiplayerportrait41-watcher.png");

        Assert.Equal(expected, GameInstallLocator.FindPortraitFile(temp.Path, "Watcher"), ignoreCase: true);
    }

    [Fact]
    public void The_slugcat_own_colour_variant_wins_over_the_white_one()
    {
        using var temp = new TempDirectory("install");
        WritePortrait(temp, Illustrations, "multiplayerportrait01-saint.png");
        var preferred = WritePortrait(temp, Illustrations, "multiplayerportrait41-saint.png");

        Assert.Equal(preferred, GameInstallLocator.FindPortraitFile(temp.Path, "Saint"), ignoreCase: true);
    }

    [Fact]
    public void A_dead_face_variant_is_never_returned_even_when_it_is_the_only_file()
    {
        using var temp = new TempDirectory("install");
        WritePortrait(temp, Illustrations, "multiplayerportrait00-white.png");

        // The 0 in the second digit is the X-eyed dead face. Showing it beside a live campaign
        // would read as the run being over.
        Assert.Null(GameInstallLocator.FindPortraitFile(temp.Path, "White"));
    }

    [Fact]
    public void A_dead_face_is_passed_over_for_the_live_one()
    {
        using var temp = new TempDirectory("install");
        WritePortrait(temp, Illustrations, "multiplayerportrait40-rivulet.png");
        var alive = WritePortrait(temp, Illustrations, "multiplayerportrait41-rivulet.png");

        Assert.Equal(alive, GameInstallLocator.FindPortraitFile(temp.Path, "Rivulet"), ignoreCase: true);
    }

    [Fact]
    public void A_slugcat_with_no_portrait_at_all_returns_null()
    {
        using var temp = new TempDirectory("install");
        WritePortrait(temp, Illustrations, "multiplayerportrait01-white.png");

        Assert.Null(GameInstallLocator.FindPortraitFile(temp.Path, "Inv"));
    }

    [Fact]
    public void An_install_with_no_illustrations_at_all_returns_null()
    {
        using var temp = new TempDirectory("install");
        temp.CreateSubdirectory(StreamingAssets);

        Assert.Null(GameInstallLocator.FindPortraitFile(temp.Path, "White"));
    }

    [Fact]
    public void The_id_is_matched_case_insensitively_against_the_lowercased_file_name()
    {
        using var temp = new TempDirectory("install");
        var expected = WritePortrait(temp, MoreSlugcats, "multiplayerportrait41-artificer.png");

        Assert.Equal(expected, GameInstallLocator.FindPortraitFile(temp.Path, "ARTIFICER"), ignoreCase: true);
    }

    [Fact]
    public void A_similarly_named_slugcat_does_not_borrow_another_portrait()
    {
        using var temp = new TempDirectory("install");
        WritePortrait(temp, Illustrations, "multiplayerportrait41-spearmaster.png");

        Assert.Null(GameInstallLocator.FindPortraitFile(temp.Path, "Spear"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_slugcat_id_returns_null(string slugcatId)
    {
        using var temp = new TempDirectory("install");
        WritePortrait(temp, Illustrations, "multiplayerportrait01-white.png");

        Assert.Null(GameInstallLocator.FindPortraitFile(temp.Path, slugcatId));
    }

    [Fact]
    public void An_install_path_that_does_not_exist_returns_null_rather_than_throwing()
    {
        using var temp = new TempDirectory("install");

        Assert.Null(GameInstallLocator.FindPortraitFile(temp.Resolve("nowhere"), "White"));
    }

    /// <summary>
    /// The install path is decoration: it only decides whether portraits are drawn. A wrong one
    /// must never be the reason a backup is refused.
    /// </summary>
    [Fact]
    public void A_wrong_install_path_does_not_make_the_settings_invalid()
    {
        var settings = new AppSettings
        {
            GameSavePath = @"C:\Games\Rain World Saves",
            BackupRootPath = @"C:\Backups\RainWorldCompanion",
            GameInstallPath = @"C:\this\does\not\exist",
        };

        Assert.Null(SettingsValidation.Validate(settings.GameSavePath, settings.BackupRootPath));
    }

    [Fact]
    public void A_missing_install_path_does_not_make_the_settings_invalid()
    {
        var settings = new AppSettings
        {
            GameSavePath = @"C:\Games\Rain World Saves",
            BackupRootPath = @"C:\Backups\RainWorldCompanion",
            GameInstallPath = null,
        };

        Assert.Null(SettingsValidation.Validate(settings.GameSavePath, settings.BackupRootPath));
    }

    [Fact]
    public void The_install_path_survives_a_save_and_a_load()
    {
        using var temp = new TempDirectory("settings");
        var store = new SettingsStore(temp.Resolve("settings.json"));
        var settings = new AppSettings
        {
            GameSavePath = @"C:\Games\Rain World Saves",
            BackupRootPath = @"C:\Backups\RainWorldCompanion",
            GameInstallPath = @"D:\Steam\steamapps\common\Rain World",
        };

        store.Save(settings);

        Assert.Equal(@"D:\Steam\steamapps\common\Rain World", store.Load().GameInstallPath);
    }

    /// <summary>Writes a file with a PNG signature. The locator matches on names, not pixels.</summary>
    private static string WritePortrait(TempDirectory temp, string folder, string fileName)
        => temp.WriteBytes(
            folder + "\\" + fileName,
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
}
