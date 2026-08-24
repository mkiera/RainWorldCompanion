using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using RainWorldSaveManager.Core.Settings;
using RainWorldSaveManager.Core.System;
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
                "Rain World Save Manager is already running. Use the window that is already open.",
                "Rain World Save Manager",
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
        var viewModel = new MainViewModel(settingsStore, gameDetector, icons, ResolveAppVersion());

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

    private static string ResolveAppVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version is null ? "1.0.0" : version.ToString(3);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Last resort net. Command handlers already report their own failures.
        e.Handled = true;
        var owner = MainWindow;
        var message = "Something went wrong and the action was stopped.\n\n" + e.Exception.Message;
        if (owner is not null && owner.IsLoaded)
        {
            MessageBox.Show(owner, message, "Rain World Save Manager", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        else
        {
            MessageBox.Show(message, "Rain World Save Manager", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
