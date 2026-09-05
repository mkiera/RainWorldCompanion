using System.Windows;

using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.Views;

public partial class MeadowDialog : Window
{
    public MeadowDialog(MeadowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        Loaded += async (_, _) =>
        {
            if (viewModel.RefreshCommand.CanExecute(null))
            {
                await viewModel.RefreshCommand.ExecuteAsync(null);
            }
        };
    }
}
