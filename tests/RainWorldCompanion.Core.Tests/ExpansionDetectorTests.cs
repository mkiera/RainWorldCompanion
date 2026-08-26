using System.IO;
using RainWorldCompanion.Core.System;

namespace RainWorldCompanion.Tests;

/// <summary>
/// Neither of them appears in the game's enabled-mods list, which covers workshop and local
/// mods but not the expansions, so the folder being there is the signal. Everything here is
/// allowed to answer "did not look", because the game path is optional throughout this app.
/// </summary>
public class ExpansionDetectorTests : IDisposable
{
    private readonly TempDirectory _install = new("install");

    public void Dispose() => _install.Dispose();

    private string ModsPath => Path.Combine(_install.Path, "RainWorld_Data", "StreamingAssets", "mods");

    private void Install(string modId, bool withInfo = true)
    {
        string folder = Path.Combine(ModsPath, modId);
        Directory.CreateDirectory(folder);

        if (withInfo)
        {
            File.WriteAllText(Path.Combine(folder, "modinfo.json"), "{\"id\":\"" + modId + "\"}");
        }
    }

    [Fact]
    public void Both_expansions_are_found_when_both_are_installed()
    {
        Install(ExpansionDetector.DownpourModId);
        Install(ExpansionDetector.WatcherModId);

        ExpansionPresence found = ExpansionDetector.Detect(_install.Path);

        Assert.True(found.Downpour);
        Assert.True(found.Watcher);
        Assert.True(found.CheckedTheInstall);
    }

    [Fact]
    public void One_expansion_installed_is_not_the_other()
    {
        Install(ExpansionDetector.DownpourModId);

        ExpansionPresence found = ExpansionDetector.Detect(_install.Path);

        Assert.True(found.Downpour);
        Assert.False(found.Watcher);
        Assert.True(found.CheckedTheInstall);
    }

    [Fact]
    public void A_game_folder_with_a_mods_directory_and_no_expansions_is_a_real_answer()
    {
        Directory.CreateDirectory(ModsPath);

        ExpansionPresence found = ExpansionDetector.Detect(_install.Path);

        Assert.False(found.Downpour);
        Assert.False(found.Watcher);
        Assert.True(found.CheckedTheInstall);
    }

    /// <summary>
    /// An uninstall can leave the directory behind, so the folder alone is not enough. The file the
    /// game reads to know what the mod is has to be in it.
    /// </summary>
    [Fact]
    public void An_empty_folder_left_by_an_uninstall_does_not_count_as_installed()
    {
        Install(ExpansionDetector.WatcherModId, withInfo: false);

        Assert.False(ExpansionDetector.Detect(_install.Path).Watcher);
    }

    [Fact]
    public void No_game_folder_means_nothing_was_checked()
    {
        Assert.False(ExpansionDetector.Detect(null).CheckedTheInstall);
        Assert.False(ExpansionDetector.Detect("").CheckedTheInstall);
        Assert.False(ExpansionDetector.Detect("   ").CheckedTheInstall);
    }

    [Fact]
    public void A_folder_that_is_not_a_game_install_means_nothing_was_checked()
        => Assert.False(ExpansionDetector.Detect(_install.Path).CheckedTheInstall);

    [Fact]
    public void A_path_that_is_not_a_path_costs_the_answer_rather_than_throwing()
        => Assert.False(ExpansionDetector.Detect("::not a path::").CheckedTheInstall);

    [Fact]
    public void Not_looking_is_the_same_as_the_unknown_it_ships_with()
        => Assert.Equal(ExpansionPresence.Unknown, ExpansionDetector.Detect(null));
}
