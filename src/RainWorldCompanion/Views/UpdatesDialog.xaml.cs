using System.Windows;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.Views;

public partial class UpdatesDialog : Window
{
    private readonly UpdatesViewModel _viewModel;
    private readonly CancellationTokenSource _closing = new();

    public UpdatesDialog(UpdatesViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += OnCloseRequested;
        viewModel.ConfirmQueuedInstall = ConfirmQueuedInstall;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>
    /// The first list is read after the window is up rather than before it, so an unreachable
    /// GitHub costs a line inside an open window instead of a pause before anything appears.
    /// </summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        try
        {
            await _viewModel.InitializeAsync(_closing.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnCloseRequested() => Close();

    private bool ConfirmQueuedInstall(string message) => MessageBox.Show(this, message, "Install branch build?",
        MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.CloseRequested -= OnCloseRequested;
        _closing.Cancel();
        _closing.Dispose();
        _viewModel.CancelQueuedInstall();
    }
}
