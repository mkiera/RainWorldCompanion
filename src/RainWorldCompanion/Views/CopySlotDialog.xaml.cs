// Usings sit above the namespace declaration on purpose. RainWorldCompanion.Core.System
// exists in the referenced assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.Core.Saves.Models;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.Views;

/// <summary>
/// Picks one whole-slot copy and confirms it. Every line comes from the plan Core built for the
/// pair the pickers are on, so the file this shows as being replaced is the file that will be
/// written to and nothing here works it out a second time.
/// </summary>
public partial class CopySlotDialog : Window, INotifyPropertyChanged
{
    /// <summary>One entry in either picker.</summary>
    public sealed record SlotChoice(SaveSlotRef Ref, string Label)
    {
        /// <summary>
        /// DisplayMemberPath settles what is drawn, but a ComboBoxItem takes its automation name
        /// from this. The record's own ToString would have a screen reader announce
        /// "SlotChoice { Ref = sav, Label = Local slot 1 (sav) }".
        /// </summary>
        public override string ToString() => Label;
    }

    private readonly Func<SaveSlotRef, SaveSlotRef, SlotCopyPlan> _replan;

    // Planning parses both files, and a full sav is several megabytes. Keeping the plans means
    // moving a picker back to a pair already looked at costs nothing.
    private readonly Dictionary<(SaveSlotRef Source, SaveSlotRef Target), SlotCopyPlan> _plans = new();

    private SlotChoice _selectedSource;
    private SlotChoice _selectedTarget;
    private SlotCopyPlan _plan;

    /// <param name="plan">The pair the pickers start on.</param>
    /// <param name="replan">
    /// Asks Core to describe a different pair. Every side of this dialog comes from a plan Core
    /// built, so changing a picker cannot make the dialog disagree with what the copy will do.
    /// </param>
    /// <param name="includeOnline">
    /// Whether the online saves are offered. False when Rain Meadow is not on the machine, where
    /// online_sav is a file nothing writes and offering it would only invite a copy into a slot
    /// the game will never read.
    /// </param>
    public CopySlotDialog(
        SlotCopyPlan plan,
        Func<SaveSlotRef, SaveSlotRef, SlotCopyPlan> replan,
        bool includeOnline)
    {
        _replan = replan;
        _plan = plan;
        _plans[(new SaveSlotRef(plan.Source.Realm, plan.Source.Slot),
                new SaveSlotRef(plan.Target.Realm, plan.Target.Slot))] = plan;

        Choices = BuildChoices(includeOnline);
        _selectedSource = Find(plan.Source.Realm, plan.Source.Slot);
        _selectedTarget = Find(plan.Target.Realm, plan.Target.Slot);

        // Find falls back to the first choice for a realm this dialog is not offering, so the
        // pickers can land on a pair other than the one that was planned. This asks Core about the
        // pair actually shown, and hits the cache when it is the same one.
        Replan();

        InitializeComponent();
        DataContext = this;

        // Cancel keeps the focus so Enter never overwrites a save by accident.
        Loaded += (_, _) => CancelButton.Focus();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Every slot that can take part, so any one can be copied onto any other.</summary>
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
    /// Core's own answer for the pair the pickers are on. The dialog decides nothing itself, so a
    /// pair it lets through is a pair Core has already agreed to.
    /// </summary>
    public bool CanCopy => _plan.CanCopy;

    /// <summary>Why the copy is refused, in Core's words. Empty when it is not.</summary>
    public string BlockedReason => string.Join("\n", _plan.Problems);

    public string HeadlineText =>
        "Copy " + _plan.Source.FileName + " onto " + _plan.Target.FileName + "?";

    public string DirectionText
    {
        get
        {
            if (_plan.Source.Slot == _plan.Target.Slot && _plan.Source.Realm != _plan.Target.Realm)
            {
                return "These are the two halves of slot " + Number(_plan.Source.Slot) +
                       ". Rain World picks between them by whether you are in a Rain Meadow lobby.";
            }

            return Capitalise(Describe(_plan.Source.Realm, _plan.Source.Slot)) +
                   " is copied onto " + Describe(_plan.Target.Realm, _plan.Target.Slot) + ".";
        }
    }

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

    private static IReadOnlyList<SlotChoice> BuildChoices(bool includeOnline)
    {
        var realms = includeOnline
            ? new[] { SaveRealm.Local, SaveRealm.Online }
            : new[] { SaveRealm.Local };

        var choices = new List<SlotChoice>(SaveSlotRef.MaxSlot * realms.Length);

        foreach (SaveRealm realm in realms)
        {
            for (int slot = SaveSlotRef.MinSlot; slot <= SaveSlotRef.MaxSlot; slot++)
            {
                var reference = new SaveSlotRef(realm, slot);
                choices.Add(new SlotChoice(
                    reference,
                    Capitalise(Describe(realm, slot)) + "  (" + reference.FileName + ")"));
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
    /// Rebuilds every line from the plan for the pair the pickers are on, including the pair that
    /// names one file twice: Core is what refuses that, and asking it means the refusal is worded
    /// the same here as it would be if the copy were run.
    /// </summary>
    private void Replan()
    {
        var key = (_selectedSource.Ref, _selectedTarget.Ref);

        if (!_plans.TryGetValue(key, out SlotCopyPlan? cached))
        {
            cached = _replan(_selectedSource.Ref, _selectedTarget.Ref);
            _plans[key] = cached;
        }

        _plan = cached;

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

    /// <summary>"local slot 2", the phrase both the pickers and the direction line are built from.</summary>
    private static string Describe(SaveRealm realm, int slot) =>
        (realm == SaveRealm.Online ? "online slot " : "local slot ") + Number(slot);

    private static string Capitalise(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
