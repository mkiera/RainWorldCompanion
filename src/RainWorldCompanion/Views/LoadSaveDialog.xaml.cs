// Usings sit above the namespace declaration on purpose. RainWorldCompanion.Core.System
// exists in the referenced assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Mods;
using RainWorldCompanion.Core.Library;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.Core.Saves.Models;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.Views;

/// <summary>
/// Every line comes from the plan Core built for the pair the pickers are on, so the file this
/// shows as being replaced is the file that will be written to and nothing here works it out a
/// second time.
/// </summary>
public partial class LoadSaveDialog : Window, INotifyPropertyChanged
{
    public sealed record EntryChoice(LibraryEntry Entry, string Label)
    {
        /// <summary>
        /// DisplayMemberPath settles what is drawn, but a ComboBoxItem takes its automation name
        /// from this.
        /// </summary>
        public override string ToString() => Label;
    }

    public sealed record SlotChoice(SaveSlotRef Ref, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly Func<LibraryEntry, SaveSlotRef, LibraryLoadPlan> _replan;

    // Planning parses the target slot, and a full sav is several megabytes. Keeping the plans means
    // moving a picker back to a pair already looked at costs nothing.
    private readonly Dictionary<(string EntryId, SaveSlotRef Target), LibraryLoadPlan> _plans = new();

    private EntryChoice _selectedEntry;
    private SlotChoice _selectedTarget;
    private LibraryLoadPlan _plan;

    /// <param name="includeOnline">
    /// False when Rain Meadow is not on the machine, where online_sav is a file nothing reads.
    /// </param>
    public LoadSaveDialog(
        IReadOnlyList<LibraryEntry> entries,
        LibraryLoadPlan plan,
        Func<LibraryEntry, SaveSlotRef, LibraryLoadPlan> replan,
        bool includeOnline,
        Func<ModListDiff?>? fixMods = null)
    {
        _replan = replan;
        _plan = plan;
        _fixMods = fixMods;

        Entries = BuildEntries(entries);
        Slots = BuildSlots(includeOnline);

        _selectedEntry = FindEntry(plan.Entry.Id) ?? Entries[0];
        _selectedTarget = FindSlot(plan.Target.Realm, plan.Target.Slot) ?? Slots[0];

        _plans[(plan.Entry.Id, new SaveSlotRef(plan.Target.Realm, plan.Target.Slot))] = plan;

        // The pickers can land on a different pair than the one planned, when the entry is not in
        // the list or the realm is not offered.
        Replan();

        InitializeComponent();
        DataContext = this;

        // Cancel keeps the focus so Enter never overwrites a save by accident.
        Loaded += (_, _) => CancelButton.Focus();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<EntryChoice> Entries { get; }

    public IReadOnlyList<SlotChoice> Slots { get; }

    public EntryChoice SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedEntry))
            {
                return;
            }

            _selectedEntry = value;
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
    public LibraryEntry ChosenEntry => _selectedEntry.Entry;

    public SaveSlotRef ChosenTarget => _selectedTarget.Ref;

    public bool CanLoad => _plan.CanLoad && ModDiff.Settled;

    public string BlockedReason => string.Join("\n", _plan.Problems);

    public string HeadlineText =>
        "Load \"" + _plan.Entry.Name + "\" into " + _plan.Target.FileName + "?";

    /// <summary>
    /// A whole slot is copied over the target and a campaign is spliced into it, which are different
    /// enough that saying the wrong one would mislead about what survives.
    /// </summary>
    public string DirectionText => _plan.Entry.IsCampaign
        ? "The campaign is written into " + Describe(_plan.Target.Realm, _plan.Target.Slot) +
          ". Rain World reads that slot the next time you open it."
        : "The library save is copied into " + Describe(_plan.Target.Realm, _plan.Target.Slot) +
          ". Rain World reads that slot the next time you open it.";

    public string ReplaceWarningText
    {
        get
        {
            if (_plan.Entry.IsCampaign)
            {
                return _plan.Summary.Length > 0
                    ? _plan.Summary + " Every other campaign in " + _plan.Target.FileName + " is left alone."
                    : "Every other campaign in " + _plan.Target.FileName + " is left alone.";
            }

            return _plan.Target.Exists
                ? _plan.Target.FileName + " is replaced entirely. Everything in it now is gone once this finishes."
                : _plan.Target.FileName + " does not exist yet and will be created.";
        }
    }

    public string SourceName => _plan.Entry.Name;

    public string TargetName => _plan.Target.FileName;

    public string SourceSummary
    {
        get
        {
            if (_plan.Entry.Manifest is not { } manifest)
            {
                return _plan.Entry.Problem ?? "this save did not finish being stored";
            }

            var size = SlotCopyService.FormatSize(manifest.SizeBytes);

            if (manifest.Metadata is not { } metadata)
            {
                return size;
            }

            if (metadata.ParseError is { } error)
            {
                return size + "    could not be read: " + error;
            }

            var count = metadata.Campaigns.Count switch
            {
                0 => SlotMetadata.DescribeWithoutCampaigns(metadata.RecordCount),
                1 => "1 campaign",
                _ => Number(metadata.Campaigns.Count) + " campaigns",
            };

            return size + "    " + count;
        }
    }

    public string TargetSummary => Summarise(_plan.Target);

    public IReadOnlyList<string> SourceCampaigns =>
        DescribeCampaigns(_plan.Entry.Manifest?.Metadata);

    public IReadOnlyList<string> TargetCampaigns => DescribeCampaigns(_plan.Target.Metadata);

    public IReadOnlyList<string> Warnings => _plan.Warnings;

    public Visibility WarningsVisibility =>
        Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    private static IReadOnlyList<EntryChoice> BuildEntries(IReadOnlyList<LibraryEntry> entries)
    {
        var choices = new List<EntryChoice>(entries.Count);

        foreach (var entry in entries)
        {
            var detail = entry.IsCampaign
                ? "  (one campaign)"
                : entry.Manifest?.Metadata?.Campaigns.Count switch
                {
                    null or 0 => "",
                    1 => "  (1 campaign)",
                    var count => "  (" + Number(count.Value) + " campaigns)",
                };

            choices.Add(new EntryChoice(entry, entry.Name + detail));
        }

        return choices;
    }

    private static IReadOnlyList<SlotChoice> BuildSlots(bool includeOnline)
    {
        var realms = includeOnline
            ? new[] { SaveRealm.Local, SaveRealm.Online }
            : new[] { SaveRealm.Local };

        var choices = new List<SlotChoice>(SaveSlotRef.MaxSlot * realms.Length);

        foreach (var realm in realms)
        {
            for (var slot = SaveSlotRef.MinSlot; slot <= SaveSlotRef.MaxSlot; slot++)
            {
                var reference = new SaveSlotRef(realm, slot);
                choices.Add(new SlotChoice(
                    reference,
                    Capitalise(Describe(realm, slot)) + "  (" + reference.FileName + ")"));
            }
        }

        return choices;
    }

    private EntryChoice? FindEntry(string id)
    {
        foreach (var choice in Entries)
        {
            if (string.Equals(choice.Entry.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return choice;
            }
        }

        return null;
    }

    private SlotChoice? FindSlot(SaveRealm realm, int slot)
    {
        foreach (var choice in Slots)
        {
            if (choice.Ref.Realm == realm && choice.Ref.Slot == slot)
            {
                return choice;
            }
        }

        return null;
    }

    /// <summary>
    /// Rebuilt with the rest of the window when the pickers move, because a different entry
    /// carries a different record. The target slot does not change it: the mods are the
    /// machine's, not the slot's.
    /// </summary>
    public ModListDiffViewModel ModDiff => _modDiff;

    private readonly Func<ModListDiff?>? _fixMods;
    private ModListDiffViewModel _modDiff = new(null);

    private void Replan()
    {
        var key = (_selectedEntry.Entry.Id, _selectedTarget.Ref);

        if (!_plans.TryGetValue(key, out var cached))
        {
            cached = _replan(_selectedEntry.Entry, _selectedTarget.Ref);
            _plans[key] = cached;
        }

        _plan = cached;

        _modDiff = new ModListDiffViewModel(_plan.Mods) { FixMods = _fixMods };
        _modDiff.PropertyChanged += (_, _) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanLoad)));

        foreach (var name in new[]
                 {
                     nameof(SelectedEntry), nameof(SelectedTarget), nameof(CanLoad), nameof(BlockedReason),
                     nameof(HeadlineText), nameof(DirectionText), nameof(ReplaceWarningText),
                     nameof(SourceName), nameof(TargetName), nameof(SourceSummary), nameof(TargetSummary),
                     nameof(SourceCampaigns), nameof(TargetCampaigns), nameof(Warnings), nameof(WarningsVisibility),
                     nameof(ModDiff),
                 })
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    private static string Summarise(SlotSide side)
    {
        if (!side.Exists)
        {
            return "not in the save folder";
        }

        var size = SlotCopyService.FormatSize(side.SizeBytes);

        if (side.Metadata is not { } metadata)
        {
            return size;
        }

        if (metadata.ParseError is { } error)
        {
            return size + "    could not be read: " + error;
        }

        var count = metadata.Campaigns.Count switch
        {
            0 => SlotMetadata.DescribeWithoutCampaigns(metadata.RecordCount),
            1 => "1 campaign",
            _ => Number(metadata.Campaigns.Count) + " campaigns",
        };

        return size + "    " + count;
    }

    private static IReadOnlyList<string> DescribeCampaigns(SlotMetadata? metadata)
    {
        if (metadata is null)
        {
            return Array.Empty<string>();
        }

        var lines = new List<string>();

        foreach (var campaign in metadata.Campaigns)
        {
            var name = SlugcatCatalog.ForId(campaign.SlugcatId).DisplayName;
            lines.Add(campaign.CycleNum.HasValue
                ? name + "    cycle " + Number(campaign.CycleNum.Value)
                : name);
        }

        return lines;
    }

    private static string Describe(SaveRealm realm, int slot) =>
        (realm == SaveRealm.Online ? "online slot " : "local slot ") + Number(slot);

    private static string Capitalise(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private void OnLoad(object sender, RoutedEventArgs e)
    {
        if (CanLoad)
        {
            DialogResult = true;
        }
    }
}
