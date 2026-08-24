using System.Windows;
using RainWorldSaveManager.ViewModels;

namespace RainWorldSaveManager;

public partial class MainWindow : Window
{
    private bool _initialised;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

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
                "Rain World Save Manager", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        (DataContext as MainViewModel)?.Shutdown();
    }
}
