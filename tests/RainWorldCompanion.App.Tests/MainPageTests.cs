using RainWorldCompanion.Core.Settings;
using RainWorldCompanion.Core.System;
using RainWorldCompanion.Services;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.App.Tests;

public class MainPageTests
{
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
            Assert.True(view.IsLivePageVisible);
            view.IsGameRunning = false;
            Assert.True(view.IsSavePageVisible);
            Assert.Same(live, view.Live);
        }
        finally { view.Shutdown(); }
    }
}
