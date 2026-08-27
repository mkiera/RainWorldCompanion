using System.Windows;
using RainWorldCompanion.Core.Settings;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion;

public partial class MainWindow : Window
{
    private const double DefaultSizeFraction = 0.78;
    private const double DefaultMaxWidth = 1600;
    private const double DefaultMaxHeight = 1000;

    private bool _initialised;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>
    /// Called before Show, so the window opens at the right size instead of resizing after a
    /// visible flash. A saved size restores exactly; otherwise the default scales with the
    /// screen so a big monitor is not left with the same size that suited a small one.
    /// </summary>
    public void ApplyStartupGeometry(SettingsStore settingsStore)
    {
        var saved = settingsStore.TryReadWindowGeometry();

        if (saved is { WindowWidth: { } width, WindowHeight: { } height, WindowLeft: { } left, WindowTop: { } top }
            && IsOnScreen(left, top, width, height))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Width = width;
            Height = height;
            Left = left;
            Top = top;

            if (saved.WindowMaximized)
            {
                WindowState = WindowState.Maximized;
            }

            return;
        }

        var workArea = SystemParameters.WorkArea;
        Width = Math.Clamp(workArea.Width * DefaultSizeFraction, MinWidth, DefaultMaxWidth);
        Height = Math.Clamp(workArea.Height * DefaultSizeFraction, MinHeight, DefaultMaxHeight);
    }

    /// <summary>
    /// Whether a saved rectangle still lands somewhere real, so a monitor unplugged since the
    /// last run does not leave the window stranded off every remaining screen.
    /// </summary>
    private static bool IsOnScreen(double left, double top, double width, double height)
    {
        var virtualScreen = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);

        return virtualScreen.IntersectsWith(new Rect(left, top, Math.Max(width, 1), Math.Max(height, 1)));
    }

    /// <summary>
    /// RestoreBounds rather than the live Width/Height/Left/Top, which read wrong while the
    /// window is minimised or maximised.
    /// </summary>
    private Rect NormalBounds => WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialised)
        {
            return;
        }

        _initialised = true;

        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        try
        {
            await viewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "The app could not finish starting.\n\n" + ex.Message,
                AppInfo.DisplayName, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        var bounds = NormalBounds;
        viewModel.SaveWindowGeometry(bounds.Width, bounds.Height, bounds.X, bounds.Y, WindowState == WindowState.Maximized);
        viewModel.Shutdown();
    }
}
