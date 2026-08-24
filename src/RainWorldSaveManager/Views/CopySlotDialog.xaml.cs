// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists in the referenced assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.ComponentModel;
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
public partial class CopySlotDialog : Window, INotifyPropertyChanged
{
    /// <summary>One entry in either picker.</summary>
    public sealed record SlotChoice(SaveSlotRef Ref, string Label);

    private readonly Func<SaveSlotRef, SaveSlotRef, SlotCopyPlan> _replan;

    private SlotChoice _selectedSource;
    private SlotChoice _selectedTarget;
    private SlotCopyPlan _plan;

    /// <param name="plan">The pair the user arrived with, which the pickers start on.</param>
    /// <param name="replan">
    /// Asks Core to describe a different pair. Every side of this dialog comes from a plan Core
    /// built, so changing a picker cannot make the dialog disagree with what the copy will do.
    /// </param>
    public CopySlotDialog(SlotCopyPlan plan, Func<SaveSlotRef, SaveSlotRef, SlotCopyPlan> replan)
    {
        _replan = replan;
        _plan = plan;

        Choices = BuildChoices();
        _selectedSource = Find(plan.Source.Realm, plan.Source.Slot);
        _selectedTarget = Find(plan.Target.Realm, plan.Target.Slot);

        InitializeComponent();
        DataContext = this;

        // Cancel keeps the focus so Enter never overwrites a save by accident.
        Loaded += (_, _) => CancelButton.Focus();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Every slot on both sides, so any one can be copied onto any other.</summary>
    public IReadOnlyList<SlotChoice> Choices { get; }

    public SlotChoice SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedSource))
            {
                return;
            }

            _selectedSource = value;
            Replan();
        }
    }

    public SlotChoice SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedTarget))
            {
                return;
            }

            _selectedTarget = value;
            Replan();
        }
    }

    /// <summary>The pair the user settled on, read after the dialog closes.</summary>
    public SaveSlotRef ChosenSource => _selectedSource.Ref;

    public SaveSlotRef ChosenTarget => _selectedTarget.Ref;

    /// <summary>
    /// False when the two pickers name the same file. Copying a file onto itself is refused by
    /// Core anyway, so this only saves the user the round trip.
    /// </summary>
    public bool CanCopy => _selectedSource.Ref != _selectedTarget.Ref;

    public string BlockedReason =>
        CanCopy ? "" : "Pick two different slots. A file cannot be copied onto itself.";

    public string HeadlineText =>
        "Copy " + _plan.Source.FileName + " onto " + _plan.Target.FileName + "?";

    public string DirectionText => _plan.Source.Slot == _plan.Target.Slot
        ? "These are the two halves of slot " + Number(_plan.Source.Slot) +
          ". Rain World picks between them by whether you are in a Rain Meadow lobby."
        : "Slot " + Number(_plan.Source.Slot) + " is copied onto slot " + Number(_plan.Target.Slot) + ".";

    public string ReplaceWarningText => _plan.Target.Exists
        ? _plan.Target.FileName + " is replaced entirely. Everything in it now is gone once this finishes."
        : _plan.Target.FileName + " does not exist yet and will be created.";

    public string SourceName => _plan.Source.FileName;

    public string TargetName => _plan.Target.FileName;

    public string SourceSummary => Summarise(_plan.Source);

    public string TargetSummary => Summarise(_plan.Target);

    public IReadOnlyList<string> SourceCampaigns => DescribeCampaigns(_plan.Source);

    public IReadOnlyList<string> TargetCampaigns => DescribeCampaigns(_plan.Target);

    public IReadOnlyList<string> Warnings => _plan.Warnings;

    public Visibility WarningsVisibility =>
        Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    private static IReadOnlyList<SlotChoice> BuildChoices()
    {
        var choices = new List<SlotChoice>(SaveSlotRef.MaxSlot * 2);

        foreach (SaveRealm realm in new[] { SaveRealm.Local, SaveRealm.Online })
        {
            for (int slot = SaveSlotRef.MinSlot; slot <= SaveSlotRef.MaxSlot; slot++)
            {
                var reference = new SaveSlotRef(realm, slot);
                string kind = realm == SaveRealm.Online ? "Online" : "Local";
                choices.Add(new SlotChoice(reference, kind + " slot " + Number(slot) + "  (" + reference.FileName + ")"));
            }
        }

        return choices;
    }

    private SlotChoice Find(SaveRealm realm, int slot)
    {
        foreach (SlotChoice choice in Choices)
        {
            if (choice.Ref.Realm == realm && choice.Ref.Slot == slot)
            {
                return choice;
            }
        }

        return Choices[0];
    }

    /// <summary>
    /// Rebuilds every line from a fresh plan. Both sides of a self copy would name one file, and
    /// Core refuses that, so the plan is only asked for when the pair is a real one.
    /// </summary>
    private void Replan()
    {
        if (CanCopy)
        {
            _plan = _replan(_selectedSource.Ref, _selectedTarget.Ref);
        }

        foreach (string name in new[]
                 {
                     nameof(SelectedSource), nameof(SelectedTarget), nameof(CanCopy), nameof(BlockedReason),
                     nameof(HeadlineText), nameof(DirectionText), nameof(ReplaceWarningText),
                     nameof(SourceName), nameof(TargetName), nameof(SourceSummary), nameof(TargetSummary),
                     nameof(SourceCampaigns), nameof(TargetCampaigns), nameof(Warnings), nameof(WarningsVisibility),
                 })
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

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
