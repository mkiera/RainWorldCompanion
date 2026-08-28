using System.Windows;

using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.Views;

public partial class ModSyncDialog : Window
{
    public ModSyncDialog(ModSyncViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private ModSyncViewModel? ViewModel => DataContext as ModSyncViewModel;

    private void OnOpenWorkshopPage(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ModSyncRowViewModel row || !row.HasWorkshopPage)
        {
            return;
        }

        Open(row.WorkshopUrl);
    }

    private void OnLaunchGame(object sender, RoutedEventArgs e) => Open(ModSyncViewModel.SteamRunUrl);

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void Open(string url) => WorkshopLink.Open(url, problem => ViewModel?.ReportLinkProblem(problem));
}
