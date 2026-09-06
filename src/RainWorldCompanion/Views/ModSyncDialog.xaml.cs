using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

using RainWorldCompanion.Core.Library;
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

    private void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        e.Effects = DroppedLists(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnPreviewDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        e.Handled = true;
        var lists = DroppedLists(e);

        // Explorer waits on this handler, so the read is queued behind it.
        Dispatcher.InvokeAsync(() =>
        {
            foreach (string path in lists)
            {
                ViewModel?.ImportList(path);
            }
        });
    }

    private static List<string> DroppedLists(DragEventArgs e)
        => e.Data.GetData(DataFormats.FileDrop) is string[] paths
            ? paths.Where(path => ImportableFile.Classify(path) == ImportableKind.ModList).ToList()
            : new List<string>();

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
        if (ShowExportDialog("Export the ticked mod list", "Rain World mods") is { } path)
        {
            ViewModel?.ExportList(path);
        }
    }

    private void OnSaveImportedProfile(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        string? name = AskForName(
            viewModel.SuggestedProfileName,
            "Save imported list",
            "This keeps the list as a profile. It does not change the game files.",
            "Save profile");
        if (name is not null)
        {
            viewModel.SaveImportedProfile(name);
        }
    }

    private void OnSaveCurrentProfile(object sender, RoutedEventArgs e)
    {
        string? name = AskForName(
            "",
            "Save current list",
            "The mods currently ticked in this window will be saved as a profile.",
            "Save profile");
        if (name is not null)
        {
            ViewModel?.SaveCurrentProfile(name);
        }
    }

    private void OnLoadProfile(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ModListProfileViewModel profile)
        {
            ViewModel?.LoadProfile(profile);
        }
    }

    private void OnLoadHistory(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ModListHistoryViewModel history)
        {
            ViewModel?.LoadHistory(history);
        }
    }

    private void OnPreviewLatestHistory(object sender, RoutedEventArgs e) => ViewModel?.PreviewLatestHistory();

    private void OnOpenProfileMenu(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.DataContext = button.DataContext;
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    private void OnRenameProfile(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ModListProfileViewModel profile)
        {
            return;
        }

        string? name = AskForName(
            profile.Name,
            "Rename saved list",
            "Only the profile name changes.",
            "Rename");
        if (name is not null)
        {
            ViewModel?.RenameProfile(profile, name);
        }
    }

    private void OnReplaceProfile(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ModListProfileViewModel profile)
        {
            return;
        }

        MessageBoxResult answer = MessageBox.Show(
            this,
            $"Replace \"{profile.Name}\" with the mods currently ticked in this window?",
            "Replace saved list",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
        {
            ViewModel?.ReplaceProfile(profile);
        }
    }

    private void OnDeleteProfile(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ModListProfileViewModel profile)
        {
            return;
        }

        MessageBoxResult answer = MessageBox.Show(
            this,
            $"Delete the saved list \"{profile.Name}\"?",
            "Delete saved list",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
        {
            ViewModel?.DeleteProfile(profile);
        }
    }

    private void OnExportProfile(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ModListProfileViewModel profile)
        {
            return;
        }

        if (ShowExportDialog("Export saved mod list", profile.Name) is { } path)
        {
            ViewModel?.ExportSnapshot(profile.Snapshot, path);
        }
    }

    private void OnExportHistory(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ModListHistoryViewModel history)
        {
            return;
        }

        if (ShowExportDialog("Export mod history", "Rain World mods " + history.CapturedAt.LocalDateTime.ToString("yyyy-MM-dd HH-mm")) is { } path)
        {
            ViewModel?.ExportSnapshot(history.Snapshot, path);
        }
    }

    private string? AskForName(string current, string headline, string subtitle, string action)
    {
        var dialog = new ModProfileNameDialog(current, headline, subtitle, action) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.EntryName : null;
    }

    private string? ShowExportDialog(string title, string suggestedName)
    {
        string safeName = string.Concat(suggestedName.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = "Rain World mod list (*.rwmods)|*.rwmods|JSON files (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = ModListFile.Extension,
            AddExtension = true,
            FileName = safeName + ModListFile.Extension,
        };

        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void Open(string url) => WorkshopLink.Open(url, problem => ViewModel?.ReportLinkProblem(problem));
}
