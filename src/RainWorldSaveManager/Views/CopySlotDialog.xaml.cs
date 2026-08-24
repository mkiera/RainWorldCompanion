// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists in the referenced assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Globalization;
using System.Windows;
using RainWorldSaveManager.Core.Backups;
using RainWorldSaveManager.Core.Saves;
using RainWorldSaveManager.Core.Saves.Models;
using RainWorldSaveManager.ViewModels;

namespace RainWorldSaveManager.Views;

/// <summary>
/// Confirms one whole-slot copy. Both sides come from the plan Core built, so the file this shows
/// as being replaced is the file that will be written to and nothing here works it out a second
/// time.
/// </summary>
public partial class CopySlotDialog : Window
{
    public CopySlotDialog(SlotCopyPlan plan)
    {
        SlotSide source = plan.Source;
        SlotSide target = plan.Target;

        SourceName = source.FileName;
        TargetName = target.FileName;

        HeadlineText = "Copy " + source.FileName + " onto " + target.FileName + "?";

        DirectionText = source.Slot == target.Slot
            ? "These are the two halves of slot " + Number(source.Slot) +
              ". Rain World picks between them by whether you are in a Rain Meadow lobby."
            : "Slot " + Number(source.Slot) + " is copied onto slot " + Number(target.Slot) + ".";

        ReplaceWarningText = target.Exists
            ? target.FileName + " is replaced entirely. Everything in it now is gone once this finishes."
            : target.FileName + " does not exist yet and will be created.";

        SourceSummary = Summarise(source);
        TargetSummary = Summarise(target);

        SourceCampaigns = DescribeCampaigns(source);
        TargetCampaigns = DescribeCampaigns(target);

        Warnings = plan.Warnings;
        WarningsVisibility = Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        InitializeComponent();
        DataContext = this;

        // Cancel keeps the focus so Enter never overwrites a save by accident.
        Loaded += (_, _) => CancelButton.Focus();
    }

    public string HeadlineText { get; }

    public string DirectionText { get; }

    public string ReplaceWarningText { get; }

    public string SourceName { get; }

    public string TargetName { get; }

    public string SourceSummary { get; }

    public string TargetSummary { get; }

    public IReadOnlyList<string> SourceCampaigns { get; }

    public IReadOnlyList<string> TargetCampaigns { get; }

    public IReadOnlyList<string> Warnings { get; }

    public Visibility WarningsVisibility { get; }

    /// <summary>The size line under a file name: how big it is and how much is in it.</summary>
    private static string Summarise(SlotSide side)
    {
        if (!side.Exists)
        {
            return "not in the save folder";
        }

        // The Core formatter, not the list one: the message box that follows this dialog reports
        // the same file through SlotCopyResult.Headline, which goes through this one.
        string size = SlotCopyService.FormatSize(side.SizeBytes);

        if (side.Metadata is not { } metadata)
        {
            return size;
        }

        if (metadata.ParseError is { } error)
        {
            return size + "    could not be read: " + error;
        }

        string count = metadata.Campaigns.Count switch
        {
            0 => SlotMetadata.DescribeWithoutCampaigns(metadata.RecordCount),
            1 => "1 campaign",
            _ => Number(metadata.Campaigns.Count) + " campaigns",
        };

        return size + "    " + count;
    }

    private static IReadOnlyList<string> DescribeCampaigns(SlotSide side)
    {
        if (side.Metadata is not { } metadata)
        {
            return Array.Empty<string>();
        }

        var lines = new List<string>();

        foreach (CampaignSummary campaign in metadata.Campaigns)
        {
            string name = SlugcatCatalog.ForId(campaign.SlugcatId).DisplayName;
            lines.Add(campaign.CycleNum.HasValue
                ? name + "    cycle " + Number(campaign.CycleNum.Value)
                : name);
        }

        return lines;
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
