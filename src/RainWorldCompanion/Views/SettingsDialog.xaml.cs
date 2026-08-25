using System.Windows;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.Views;

public partial class SettingsDialog : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsDialog(SettingsViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Owner = this;
        viewModel.CloseRequested += OnCloseRequested;
        Closed += OnClosed;
    }

    private void OnCloseRequested(bool result)
    {
        try
        {
            DialogResult = result;
        }
        catch (InvalidOperationException)
        {
            // The window was not shown as a dialog, so plain closing is the fallback
            Close();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.CloseRequested -= OnCloseRequested;
        _viewModel.Owner = null;
    }
}
