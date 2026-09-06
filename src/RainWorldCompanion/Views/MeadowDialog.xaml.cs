using System.Windows;

using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.Views;

public partial class MeadowDialog : Window
{
    public MeadowDialog(MeadowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // Only the machine is read on opening. Steam is asked when Refresh is pressed and not
        // before, because Steam counts the asking as the game running.
        Loaded += async (_, _) =>
        {
            if (viewModel.CheckModsCommand.CanExecute(null))
            {
                await viewModel.CheckModsCommand.ExecuteAsync(null);
            }
        };
    }
}
