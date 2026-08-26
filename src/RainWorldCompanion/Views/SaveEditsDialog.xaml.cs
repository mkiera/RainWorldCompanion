using System.Globalization;
using System.Windows;

using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Editing;

namespace RainWorldCompanion.Views;

/// <summary>
/// The write goes through the same ladder a slot copy and a library load use, and that ladder
/// does not write without a safety copy, so this says one is coming rather than offering to
/// skip it.
/// </summary>
public partial class SaveEditsDialog : Window
{
    public SaveEditsDialog(SaveWritePlan plan, string campaignName, string fileName, IReadOnlyList<string> warnings)
    {
        Changes = plan.ChangeDescriptions;
        Warnings = warnings;

        Headline = Changes.Count == 1
            ? $"Save one change to {campaignName}?"
            : $"Save {Changes.Count.ToString(CultureInfo.InvariantCulture)} changes to {campaignName}?";

        TargetText = $"They are written to {fileName} in the save folder.";
        ChangeHeader = "WHAT CHANGES";

        BackupText =
            $"The whole save folder is copied before {fileName} is written, and the copy is listed under "
            + "Backups. Restoring it puts every save back as it is now.";

        SizeText = plan.NewLength == plan.OldLength
            ? $"{fileName} stays {SlotCopyService.FormatSize(plan.OldLength)}."
            : $"{fileName} goes from {SlotCopyService.FormatSize(plan.OldLength)} to {SlotCopyService.FormatSize(plan.NewLength)}.";

        InitializeComponent();
        DataContext = this;

        // Cancel keeps the focus, so Enter never writes over a save by accident.
        Loaded += (_, _) => CancelButton.Focus();
    }

    public string Headline { get; }

    public string TargetText { get; }

    public string ChangeHeader { get; }

    public IReadOnlyList<string> Changes { get; }

    public IReadOnlyList<string> Warnings { get; }

    public bool HasWarnings => Warnings.Count > 0;

    public string BackupText { get; }

    public string SizeText { get; }

    private void OnSave(object sender, RoutedEventArgs e) => DialogResult = true;
}
