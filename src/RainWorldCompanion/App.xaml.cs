using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using RainWorldCompanion.Core.Settings;
using RainWorldCompanion.Core.System;
using RainWorldCompanion.Core.Updates;
using RainWorldCompanion.Services;
using RainWorldCompanion.Theming;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion;

/// <summary>Composition root. Services are constructed by hand and handed to the view model.</summary>
public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\RainWorldCompanion.SingleInstance";

    private Mutex? _singleInstance;

    // Both hold an HttpClient, so they live as long as the app rather than being made per check.
    private GitHubReleaseSource? _releases;
    private InstallerDownloader? _downloader;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Two windows can start a restore within the same second, both writing into the same save
        // folder and the same backup store.
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

        // Before anything reads the settings.
        if (SettingsMigration.MoveFolder() == MigrationOutcome.Moved)
        {
            SettingsMigration.RepointSettingsFile(SettingsStore.DefaultSettingsPath);
        }

        var settingsStore = new SettingsStore();
        var gameDetector = new GameProcessDetector();

        // Starts with no install. The view model points it at the configured path once the
        // settings have been read, which happens off the dispatcher.
        var icons = new SlugcatIconProvider();
        var build = ResolveBuildStamp();
        var viewModel = new MainViewModel(settingsStore, gameDetector, icons, build.Version);

        UpdatesFolder.Clear();

        _releases = new GitHubReleaseSource(build.Version);
        _downloader = new InstallerDownloader(build.Version);
        viewModel.AttachUpdates(viewModel.CreateUpdates(
            build,
            _releases,
            _downloader,
            new InstallerLauncher(),
            // Shutdown rather than Environment.Exit, so OnExit runs and releases the mutex.
            // Queued, because this runs from inside the update, which has a line or two left.
            () => Dispatcher.BeginInvoke(Shutdown)));

        // Before Show, so the window is painted in the right colours once rather than opening
        // light and turning dark a moment later.
        var startup = settingsStore.ReadForStartup();
        ThemeManager.Apply(AppThemes.Parse(startup?.Theme));

        var window = new MainWindow { DataContext = viewModel };
        window.ApplyStartupGeometry(startup);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _releases?.Dispose();
        _releases = null;
        _downloader?.Dispose();
        _downloader = null;

        if (_singleInstance is not null)
        {
            try
            {
                _singleInstance.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }

            _singleInstance.Dispose();
            _singleInstance = null;
        }

        base.OnExit(e);
    }

    /// <summary>
    /// AssemblyInformationalVersion rather than GetName().Version, because the assembly version is
    /// four numbers and cannot hold a "-beta.1" tail. The SDK bundles SourceLink, so a build made
    /// in a git checkout has "+" and the commit appended to that attribute.
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
            // Three parts, because a four-part string is not a semver.
            version = assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }

        return new BuildStamp(
            version.Trim(),
            sha.Trim(),
            Metadata(assembly, "BuildBranch"),
            Metadata(assembly, "BuildRunId"));
    }

    /// <summary>
    /// The branch-build workflow is the only thing that sets these, so a local build and a release
    /// both read blank.
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
