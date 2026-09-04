using System.Windows;
using Microsoft.Win32;

using RainWorldCompanion.Core.Mods;
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

    private void OnImportList(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import a mod list",
            Filter = "Rain World mod list (*.rwmods)|*.rwmods|JSON files (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) == true)
        {
            ViewModel?.ImportList(dialog.FileName);
        }
    }

    private void OnExportList(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export the ticked mod list",
            Filter = "Rain World mod list (*.rwmods)|*.rwmods|JSON files (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = ModListFile.Extension,
            AddExtension = true,
            FileName = "Rain World mods" + ModListFile.Extension,
        };

        if (dialog.ShowDialog(this) == true)
        {
            ViewModel?.ExportList(dialog.FileName);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void Open(string url) => WorkshopLink.Open(url, problem => ViewModel?.ReportLinkProblem(problem));
}
