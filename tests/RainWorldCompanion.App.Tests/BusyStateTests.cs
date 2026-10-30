using System.IO;
using System.Reflection;
using RainWorldCompanion.Core.Settings;
using RainWorldCompanion.Core.System;
using RainWorldCompanion.Services;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.App.Tests;

/// <summary>
/// Closing the window or ending the session kills the background thread that writes a save, so
/// the app refuses while busy. Busy is counted, because the Game Hook timer starts its own busy
/// work and finishing it must not clear the flag under a restore that is still writing.
/// </summary>
public class BusyStateTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "RainWorldCompanionTests", Guid.NewGuid().ToString("N"));

    private readonly MainViewModel _viewModel;

    public BusyStateTests()
    {
        Directory.CreateDirectory(_folder);
        _viewModel = new MainViewModel(
            new SettingsStore(Path.Combine(_folder, "settings.json")),
            new NullGameProcessDetector(),
            new SlugcatIconProvider(),
            "test");
        _viewModel.Shutdown();
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void The_window_may_close_when_nothing_is_running()
    {
        Assert.Null(_viewModel.WhyNotClose());
    }

    [Fact]
    public void The_window_refuses_to_close_while_an_operation_runs()
    {
        BeginBusy("Restoring backup");

        Assert.Contains("Restoring backup", _viewModel.WhyNotClose());

        EndBusy();

        Assert.Null(_viewModel.WhyNotClose());
    }

    [Fact]
    public void Finishing_one_operation_leaves_busy_set_while_another_still_runs()
    {
        BeginBusy("Restoring backup");
        BeginBusy("Companion Game Hook");

        EndBusy();

        Assert.True(_viewModel.IsBusy);
        Assert.NotNull(_viewModel.WhyNotClose());
        Assert.NotNull(_viewModel.WhyNotNow());

        EndBusy();

        Assert.False(_viewModel.IsBusy);
        Assert.Null(_viewModel.WhyNotNow());
    }

    private void BeginBusy(string title) => Invoke("BeginBusy", title, "message");

    private void EndBusy() => Invoke("EndBusy");

    private void Invoke(string name, params object[] arguments) =>
        typeof(MainViewModel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_viewModel, arguments);
}
