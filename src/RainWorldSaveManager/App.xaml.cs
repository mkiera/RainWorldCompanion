using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using RainWorldSaveManager.Core.Settings;
using RainWorldSaveManager.Core.System;
using RainWorldSaveManager.Core.Updates;
using RainWorldSaveManager.Services;
using RainWorldSaveManager.ViewModels;

namespace RainWorldSaveManager;

/// <summary>
/// Composition root. Services are constructed by hand here and handed to the main view model.
/// </summary>
public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\RainWorldSaveManager.SingleInstance";

    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Two windows can start a restore within the same second, and both would then be writing
        // into the same save folder and the same backup store. Core refuses the overlap as well,
        // but a second window is not something to let the user open in the first place.
        _singleInstance = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isOnlyInstance);
        if (!isOnlyInstance)
        {
            MessageBox.Show(
                AppInfo.DisplayName + " is already running. Use the window that is already open.",
                AppInfo.DisplayName,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        var settingsStore = new SettingsStore();
        var gameDetector = new GameProcessDetector();

        // The provider starts with no install. The view model points it at the configured path
        // once the settings have been read, which happens off the dispatcher.
        var icons = new SlugcatIconProvider();
        var build = ResolveBuildStamp();
        var viewModel = new MainViewModel(settingsStore, gameDetector, icons, build.Version);

        var window = new MainWindow { DataContext = viewModel };
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_singleInstance is not null)
        {
            try
            {
                _singleInstance.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not the owning thread, which means this instance never took the mutex.
            }

            _singleInstance.Dispose();
            _singleInstance = null;
        }

        base.OnExit(e);
    }

    /// <summary>
    /// What this copy can say about itself: its version, and the build that produced it.
    ///
    /// AssemblyInformationalVersion rather than GetName().Version, because the assembly version is
    /// four numbers and cannot hold a "-beta.1" tail. A build that reports 1.1.0 for a 1.1.0-beta.1
    /// tag is a build that keeps being offered its own release as an update.
    ///
    /// The .NET 8 and later SDKs bundle SourceLink, so a build made in a git checkout has "+" and
    /// the commit appended to that attribute. Build metadata takes no part in version ordering, so
    /// it is split off here rather than carried into every comparison, and the commit is worth
    /// keeping: it is what marks the running row in the list of branch builds.
    /// </summary>
    private static BuildStamp ResolveBuildStamp()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";

        var version = informational;
        var sha = "";
        var plus = informational.IndexOf('+');
        if (plus >= 0)
        {
            version = informational[..plus];
            sha = informational[(plus + 1)..];
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            // Only reachable in a build with no informational version at all, which the SDK does
            // not produce. Three parts because a four-part string is not a semver.
            version = assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }

        return new BuildStamp(
            version.Trim(),
            sha.Trim(),
            Metadata(assembly, "BuildBranch"),
            Metadata(assembly, "BuildRunId"));
    }

    /// <summary>
    /// One [assembly: AssemblyMetadata(key, value)] entry, or blank. The branch-build workflow is
    /// the only thing that sets these, so a local build and a release both read blank.
    /// </summary>
    private static string Metadata(Assembly assembly, string key)
        => assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value ?? "";

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Last resort net. Command handlers already report their own failures.
        e.Handled = true;
        var owner = MainWindow;
        var message = "Something went wrong and the action was stopped.\n\n" + e.Exception.Message;
        if (owner is not null && owner.IsLoaded)
        {
            MessageBox.Show(owner, message, AppInfo.DisplayName, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        else
        {
            MessageBox.Show(message, AppInfo.DisplayName, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
