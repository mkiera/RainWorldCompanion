using System.Windows;
using RainWorldCompanion.Core.Library;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.Views;

/// <summary>
/// The settings of a library save, without the save. The picker is the same one the load dialog
/// carries, so the wording a row gets about a mod is decided in one place.
/// </summary>
public partial class TakeSettingsDialog : Window
{
    public TakeSettingsDialog(string sourceName, ModConfigOffer? offer)
    {
        Headline = $"Take mod settings from \"{sourceName}\"";
        Subtitle = "The save itself stays in the library. Only the settings of the mods you tick "
            + "are written over the ones in your save folder.";
        Settings = new ModConfigPickerViewModel(offer);

        InitializeComponent();
        DataContext = this;
    }

    public string Headline { get; }

    public string Subtitle { get; }

    public ModConfigPickerViewModel Settings { get; }

    public IReadOnlyCollection<string> Chosen => Settings.Chosen;

    private void OnTake(object sender, RoutedEventArgs e)
    {
        // Nothing ticked writes nothing, and reporting that as a finished operation would say
        // something happened when it did not.
        if (Settings.HasAnyTaken)
        {
            DialogResult = true;
        }
    }
}
