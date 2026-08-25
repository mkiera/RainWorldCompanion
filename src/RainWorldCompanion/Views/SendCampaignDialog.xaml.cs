// Usings sit above the namespace declaration on purpose. RainWorldCompanion.Core.System
// exists in the referenced assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.ComponentModel;
using System.Windows;

using RainWorldCompanion.Core.Editing;
using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Views;

/// <summary>
/// Picks the slot one campaign is going to, and whether it leaves the one it came from.
///
/// Every line comes from the plan Core built for the slot the picker is on, so the file this shows
/// as changing is the file that will be written to. Changing the picker asks Core again rather than
/// working anything out here.
/// </summary>
public partial class SendCampaignDialog : Window, INotifyPropertyChanged
{
    /// <summary>One entry in the slot picker.</summary>
    public sealed record SlotChoice(SaveSlotRef Ref, string Label)
    {
        /// <summary>
        /// DisplayMemberPath settles what is drawn, but a ComboBoxItem takes its automation name
        /// from this.
        /// </summary>
        public override string ToString() => Label;
    }

    private readonly Func<SaveSlotRef, CampaignMovePlan> _replan;
    private readonly Dictionary<SaveSlotRef, CampaignMovePlan> _plans = new();
    private readonly SaveSlotRef? _source;

    private SlotChoice _selectedTarget;
    private CampaignMovePlan _plan;
    private bool _takeItOut;

    /// <param name="campaignName">The slugcat as a person reads it, for example "Gourmand".</param>
    /// <param name="source">
    /// The slot it is in now, or null when it is not in a slot at all. A campaign in a backup or in
    /// a library save has nowhere to be taken out of, so that half of this window is not offered.
    /// </param>
    /// <param name="sourceName">What to call where it is now, for example "backup 2026-08-24_120000".</param>
    /// <param name="replan">
    /// Asks Core what putting the campaign into a slot would do. Every side of this dialog comes
    /// from one of those answers, so changing the picker cannot make the dialog disagree with the
    /// write.
    /// </param>
    /// <param name="includeOnline">
    /// Whether the online slots are offered. False when Rain Meadow is not on the machine, where
    /// online_sav is a file nothing reads.
    /// </param>
    public SendCampaignDialog(
        string campaignName,
        SaveSlotRef? source,
        string sourceName,
        Func<SaveSlotRef, CampaignMovePlan> replan,
        bool includeOnline)
    {
        CampaignName = campaignName;
        SourceName = sourceName;

        _source = source;
        _replan = replan;

        Slots = BuildSlots(includeOnline);
        _selectedTarget = FirstSlotOtherThanTheSource();
        _plan = Ask(_selectedTarget.Ref);

        InitializeComponent();
        DataContext = this;

        // Cancel keeps the focus, so Enter never writes over a save by accident.
        Loaded += (_, _) => CancelButton.Focus();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string CampaignName { get; }

    public string SourceName { get; }

    public IReadOnlyList<SlotChoice> Slots { get; }

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
            _plan = Ask(value.Ref);
            RaiseAll();
        }
    }

    /// <summary>
    /// Whether the campaign is taken out of the slot it came from once it has arrived.
    ///
    /// Off by default. Copying leaves both slots holding something, and a move that turned out to be
    /// the wrong slot has thrown away the only copy of a run, so the destructive half is the one the
    /// user has to ask for.
    /// </summary>
    public bool TakeItOut
    {
        get => _takeItOut;
        set
        {
            if (value == _takeItOut)
            {
                return;
            }

            _takeItOut = value;
            RaiseAll();
        }
    }

    /// <summary>Read after the dialog closes.</summary>
    public SaveSlotRef ChosenTarget => _selectedTarget.Ref;

    public bool ChosenToTakeItOut => _takeItOut && CanTakeItOut;

    /// <summary>Core's own answer for the slot the picker is on.</summary>
    public bool CanSend => _plan.CanWrite && !SameAsTheSource;

    public bool SameAsTheSource => _source is { } source
        && _selectedTarget.Ref.Realm == source.Realm
        && _selectedTarget.Ref.Slot == source.Slot;

    /// <summary>
    /// Whether taking it out of where it is now is even a thing that can be done. A backup and a
    /// library save are copies taken at a moment, and changing one would leave it no longer a copy
    /// of anything, so the campaign is only ever copied out of them.
    /// </summary>
    public bool CanTakeItOut => _source is not null;

    public Visibility TakeItOutVisibility =>
        CanTakeItOut ? Visibility.Visible : Visibility.Collapsed;

    public string BlockedReason
    {
        get
        {
            if (SameAsTheSource)
            {
                return CampaignName + " is already in " + SourceName + ".";
            }

            return _plan.Problems.Count > 0
                ? string.Join("\n", _plan.Problems)
                : string.Join("\n", _plan.Write.Problems);
        }
    }

    public Visibility BlockedVisibility =>
        BlockedReason.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string HeadlineText => _takeItOut
        ? "Move " + CampaignName + " to " + _plan.TargetFileName + "?"
        : "Copy " + CampaignName + " to " + _plan.TargetFileName + "?";

    /// <summary>What Core says this does to the slot it is going to.</summary>
    public string EffectText => _plan.Describe();

    /// <summary>
    /// What it does to the slot it came from, which is the half a person is most likely to get
    /// wrong. The map goes with a move, because a map left in a slot with no campaign to go with it
    /// is not what anybody means by moving a campaign.
    /// </summary>
    public string SourceEffectText
    {
        get
        {
            if (!CanTakeItOut)
            {
                return SourceName + " is only read, and is left exactly as it is.";
            }

            return _takeItOut
                ? CampaignName + " is taken out of " + SourceName + ", and its map discovery goes with it."
                : CampaignName + " stays in " + SourceName + " as well.";
        }
    }

    public string SafetyText =>
        "The whole save folder is copied before anything is written, and the copy is listed under "
        + "Backups. Restoring it puts every save back as it is now.";

    public IReadOnlyList<string> Warnings => _plan.Warnings;

    public Visibility WarningsVisibility =>
        Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    private CampaignMovePlan Ask(SaveSlotRef target)
    {
        if (_plans.TryGetValue(target, out CampaignMovePlan? cached))
        {
            return cached;
        }

        CampaignMovePlan plan = _replan(target);
        _plans[target] = plan;
        return plan;
    }

    /// <summary>
    /// The picker opens on a slot the campaign is not already in, because sending it back to where
    /// it is is the one choice that can never do anything.
    /// </summary>
    private SlotChoice FirstSlotOtherThanTheSource()
    {
        foreach (SlotChoice choice in Slots)
        {
            if (_source is { } source && choice.Ref.Realm == source.Realm && choice.Ref.Slot == source.Slot)
            {
                continue;
            }

            return choice;
        }

        return Slots[0];
    }

    private static IReadOnlyList<SlotChoice> BuildSlots(bool includeOnline)
    {
        SaveRealm[] realms = includeOnline
            ? new[] { SaveRealm.Local, SaveRealm.Online }
            : new[] { SaveRealm.Local };

        var choices = new List<SlotChoice>(SaveSlotRef.MaxSlot * realms.Length);

        foreach (SaveRealm realm in realms)
        {
            for (int slot = SaveSlotRef.MinSlot; slot <= SaveSlotRef.MaxSlot; slot++)
            {
                var reference = new SaveSlotRef(realm, slot);
                string kind = realm == SaveRealm.Online ? "Online slot " : "Local slot ";

                choices.Add(new SlotChoice(reference, kind + slot + "  (" + reference.FileName + ")"));
            }
        }

        return choices;
    }

    private void RaiseAll()
    {
        foreach (string name in new[]
                 {
                     nameof(SelectedTarget), nameof(TakeItOut), nameof(CanSend), nameof(SameAsTheSource),
                     nameof(BlockedReason), nameof(BlockedVisibility), nameof(HeadlineText),
                     nameof(EffectText), nameof(SourceEffectText), nameof(Warnings), nameof(WarningsVisibility),
                 })
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    private void OnSend(object sender, RoutedEventArgs e)
    {
        if (CanSend)
        {
            DialogResult = true;
        }
    }
}
