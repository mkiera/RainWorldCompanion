using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.Views;

/// <summary>
/// Its DataContext is a <see cref="ModListDiffViewModel"/>, and it hides itself when there is
/// nothing to compare.
/// </summary>
public partial class ModDiffSection : UserControl
{
    public ModDiffSection()
    {
        InitializeComponent();
    }

    /// <summary>The app never installs or enables anything itself: the game's own files are the game's to write.</summary>
    private void OnOpenWorkshopPage(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ModDiffRowViewModel row || !row.HasWorkshopPage)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(row.WorkshopUrl) { UseShellExecute = true });
        }
        catch (Exception)
        {
        }
    }
}
