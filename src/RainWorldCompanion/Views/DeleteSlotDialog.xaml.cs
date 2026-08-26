// Usings sit above the namespace declaration on purpose. RainWorldCompanion.Core.System
// exists in the referenced assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.ComponentModel;
using System.Windows;

using RainWorldCompanion.Core.Editing;

namespace RainWorldCompanion.Views;

/// <summary>
/// The confirmation shown before a slot is deleted.
///
/// It names every campaign about to go rather than saying how many, because a slot holding three
/// runs is exactly where somebody means one of them and clicks the wrong thing. Every line comes
/// from the plan Core built, so the slot this describes is the slot that will be written to.
///
/// The word is the one the game itself uses for this, even though the file stays and keeps its
/// progression record. What actually goes is named line by line below it, so the word being the
/// blunter of the two costs nobody anything.
///
/// The map is a choice and it is off by default. The game's own wipe takes it, but the map is the
/// slowest thing in a save to earn back and keeping it costs nothing, so taking it is the half the
/// user has to ask for.
/// </summary>
public partial class DeleteSlotDialog : Window, INotifyPropertyChanged
{
    private readonly IReadOnlyDictionary<SlotDeleteDepth, SlotDeletePlan> _plans;

    private SlotDeletePlan _plan;
    private SlotDeleteDepth _depth;

    /// <param name="plans">
    /// What each depth would do, worked out before this opened. All three come in together because
    /// a row that cannot do anything is drawn disabled rather than left to refuse when it is picked,
    /// and knowing that needs the answer for every row, not just the one showing.
    /// </param>
    public DeleteSlotDialog(IReadOnlyDictionary<SlotDeleteDepth, SlotDeletePlan> plans)
    {
        _plans = plans;

        // Open on the least that would actually do something. For a slot still holding campaigns
        // that is the campaigns alone; for one holding only a map and a progression record the
        // first two rows would change nothing, so it opens further down.
        _depth = Depths.FirstOrDefault(depth => plans[depth].CanWrite, Depths[0]);
        _plan = plans[_depth];

        InitializeComponent();
        DataContext = this;

        // Cancel keeps the focus, so Enter never deletes a slot by accident.
        Loaded += (_, _) => CancelButton.Focus();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The three depths, least first, which is the order the rows are drawn in.</summary>
    private static readonly SlotDeleteDepth[] Depths =
    [
        SlotDeleteDepth.Campaigns,
        SlotDeleteDepth.CampaignsAndMap,
        SlotDeleteDepth.Everything,
    ];

    /// <summary>How much goes. Read after the dialog closes.</summary>
    public SlotDeleteDepth ChosenDepth => _depth;

    // The three rows bind two way rather than to a command, because a screen reader selecting a
    // radio button sets its checked state directly and raises no click.

    public bool JustTheCampaigns
    {
        get => _depth == SlotDeleteDepth.Campaigns;
        set => Choose(value, SlotDeleteDepth.Campaigns);
    }

    public bool TheCampaignsAndTheMap
    {
        get => _depth == SlotDeleteDepth.CampaignsAndMap;
        set => Choose(value, SlotDeleteDepth.CampaignsAndMap);
    }

    public bool AllOfIt
    {
        get => _depth == SlotDeleteDepth.Everything;
        set => Choose(value, SlotDeleteDepth.Everything);
    }

    /// <summary>Whether picking a row would change anything, which is what greys one out.</summary>
    public bool CanPickJustTheCampaigns => _plans[SlotDeleteDepth.Campaigns].CanWrite;

    public bool CanPickTheCampaignsAndTheMap => _plans[SlotDeleteDepth.CampaignsAndMap].CanWrite;

    public bool CanPickAllOfIt => _plans[SlotDeleteDepth.Everything].CanWrite;

    public string HeadlineText => _plan.Campaigns.Count switch
    {
        0 => "Delete what is left in " + _plan.TargetFileName + "?",
        1 => "Delete " + _plan.TargetFileName + ", taking " + _plan.Campaigns[0] + " with it?",
        _ => "Delete " + _plan.TargetFileName + " and all " + _plan.Campaigns.Count + " campaigns in it?",
    };

    public string TargetText => _plan.Campaigns.Count == 0
        ? _plan.TargetFileName + " holds no campaign, but it still holds the map that was explored and the progression record."
        : "The game reads " + _plan.TargetFileName + " as a slot with nothing saved in it afterwards.";

    public string ListHeader => _plan.Campaigns.Count == 1 ? "WHAT GOES" : "WHAT GOES, ALL OF IT";

    public IReadOnlyList<string> Campaigns => _plan.Campaigns;

    /// <summary>Core saying what this does, in the same words the per-campaign delete uses.</summary>
    public string EffectText => _plan.Describe();

    public string StaysText => _plan.WhatStays;

    public string JustTheCampaignsText => "The campaigns only, keeping the map and the progression record";

    public string TheCampaignsAndTheMapText => "The campaigns and the map, keeping the progression record";

    public string AllOfItText => "All of it, leaving the slot as it was before it was ever played";

    public bool HasCampaigns => _plan.Campaigns.Count > 0;

    public Visibility CampaignsVisibility =>
        HasCampaigns ? Visibility.Visible : Visibility.Collapsed;

    public string SafetyText =>
        "The whole save folder is copied before " + _plan.TargetFileName + " is written, and the copy "
        + "is listed under Backups. Restoring it puts every save back as it is now, whichever of these "
        + "you pick.";

    public bool CanDelete => _plan.CanWrite;

    public string BlockedReason => string.Join("\n", _plan.Problems.Count > 0 ? _plan.Problems : _plan.Write.Problems);

    public Visibility BlockedVisibility =>
        BlockedReason.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    private void Choose(bool selected, SlotDeleteDepth depth)
    {
        if (!selected || _depth == depth)
        {
            return;
        }

        _depth = depth;
        _plan = _plans[depth];
        RaiseAll();
    }

    private void RaiseAll()
    {
        foreach (string name in new[]
                 {
                     nameof(JustTheCampaigns), nameof(TheCampaignsAndTheMap), nameof(AllOfIt),
                     nameof(HeadlineText), nameof(TargetText), nameof(ListHeader), nameof(Campaigns),
                     nameof(HasCampaigns), nameof(CampaignsVisibility), nameof(EffectText),
                     nameof(StaysText), nameof(SafetyText), nameof(CanDelete), nameof(BlockedReason),
                     nameof(BlockedVisibility),
                 })
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (CanDelete)
        {
            DialogResult = true;
        }
    }
}
