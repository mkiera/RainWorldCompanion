using System.IO;
using RainWorldCompanion.Core.Settings;
using RainWorldCompanion.Core.System;
using RainWorldCompanion.Services;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.App.Tests;

public class MainPageTests
{
    [Fact]
    public void Live_footer_uses_the_shortcut_command_for_developer_menu_while_disconnected()
    {
        var view = new MainViewModel(new SettingsStore(), new GameProcessDetector(), new SlugcatIconProvider(), "1.4.0");
        try
        {
            view.Live.AdoptSetup(false, false, null, "Companion Game Hook is not installed.");
            view.OpenLiveFeaturesCommand.Execute(null);

            Assert.False(view.IsGameRunning);
            Assert.False(view.Live.SetupReady);
            Assert.True(view.IsLivePageVisible);
            Assert.True(view.OpenDeveloperWindowCommand.CanExecute(null));

            string markup = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Xaml", "MainWindow.xaml"));
            Assert.Contains("x:Name=\"DeveloperMenuButton\"", markup, StringComparison.Ordinal);
            Assert.Contains("Command=\"{Binding OpenDeveloperWindowCommand}\"", markup, StringComparison.Ordinal);
            Assert.Contains("Visibility=\"{Binding IsLivePageVisible, Converter={StaticResource BoolToVis}}\"", markup, StringComparison.Ordinal);

            string shortcutSource = File.ReadAllText(Path.Combine(
                AppContext.BaseDirectory, "AppSource", "App.xaml.cs"));
            Assert.Contains("viewModel.OpenDeveloperWindowCommand.Execute(null);", shortcutSource, StringComparison.Ordinal);
        }
        finally { view.Shutdown(); }
    }

    [Fact]
    public void Game_start_and_exit_switch_pages_without_replacing_live_state()
    {
        var view = new MainViewModel(new SettingsStore(), new GameProcessDetector(), new SlugcatIconProvider(), "1.3.0");
        try
        {
            var live = view.Live;
            Assert.True(view.IsSavePageVisible);
            view.OpenLiveFeaturesCommand.Execute(null);
            Assert.True(view.IsLivePageVisible);
            view.OpenSavesCommand.Execute(null);
            Assert.True(view.IsSavePageVisible);
            view.IsGameRunning = true;
            Assert.True(view.IsLivePageVisible);
            Assert.False(view.IsSavePageVisible);
            view.OpenSavesCommand.Execute(null);
            Assert.True(view.IsSavePageVisible);
            Assert.False(view.IsCurrentPageReady);
            view.IsGameRunning = true;
            Assert.True(view.IsSavePageVisible);
            view.OpenLiveFeaturesCommand.Execute(null);
            Assert.True(view.IsLivePageVisible);
            Assert.True(view.IsCurrentPageReady);
            view.IsGameRunning = false;
            Assert.True(view.IsSavePageVisible);
            Assert.Same(live, view.Live);
        }
        finally { view.Shutdown(); }
    }
}
