using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.Views;

/// <summary>
/// The mod section of a confirmation dialog, shared by the three dialogs that put stored bytes
/// onto a live slot. One control rather than three copies of the same markup, because three
/// copies of a section that has to word an awkward case carefully is three chances to word it
/// differently.
///
/// <para>Its DataContext is a <see cref="ModListDiffViewModel"/>, and it hides itself when there
/// is nothing to compare.</para>
/// </summary>
public partial class ModDiffSection : UserControl
{
    public ModDiffSection()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Opens a mod's workshop page in whatever the machine uses for the web. The app never
    /// installs or enables anything itself: the game's own files are the game's to write.
    /// </summary>
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
            // A page that will not open costs the shortcut and nothing else. The row still names
            // the mod, which is what the user needs to go and find it.
        }
    }
}
