// Usings sit above the namespace declaration on purpose. RainWorldCompanion.Core.System
// exists in the referenced assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.ComponentModel;
using System.Windows;

using RainWorldCompanion.Core.Editing;

namespace RainWorldCompanion.Views;

/// <summary>
/// The confirmation shown before a slot is emptied of its campaigns.
///
/// It names every campaign about to go rather than saying how many, because a slot holding three
/// runs is exactly where somebody means one of them and clicks the wrong thing. Every line comes
/// from the plan Core built, so the slot this describes is the slot that will be written to.
///
/// The map is a choice and it is off by default. The game's own wipe takes it, but the map is the
/// slowest thing in a save to earn back and keeping it costs nothing, so taking it is the half the
/// user has to ask for.
/// </summary>
public partial class EmptySlotDialog : Window, INotifyPropertyChanged
{
    private readonly Func<bool, SlotWipePlan> _replan;

    private SlotWipePlan _plan;
    private bool _takeTheMap;

    /// <param name="plan">What emptying the slot would do, with the map left in place.</param>
    /// <param name="replan">
    /// Asks Core again when the map checkbox moves. Every side of this window comes from one of
    /// those answers, so it cannot disagree with the write.
    /// </param>
    public EmptySlotDialog(SlotWipePlan plan, Func<bool, SlotWipePlan> replan)
    {
        _plan = plan;
        _replan = replan;

        InitializeComponent();
        DataContext = this;

        // Cancel keeps the focus, so Enter never empties a slot by accident.
        Loaded += (_, _) => CancelButton.Focus();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Whether the map discovery goes with the campaigns. Read after the dialog closes.</summary>
    public bool TakeTheMap
    {
        get => _takeTheMap;
        set
        {
            if (value == _takeTheMap)
            {
                return;
            }

            _takeTheMap = value;
            _plan = _replan(value);
            RaiseAll();
        }
    }

    public string HeadlineText => _plan.Campaigns.Count == 1
        ? "Empty " + _plan.TargetFileName + ", taking " + _plan.Campaigns[0] + " with it?"
        : "Empty " + _plan.TargetFileName + " of all its campaigns?";

    public string TargetText =>
        "The game reads " + _plan.TargetFileName + " as a slot with nothing saved in it afterwards.";

    public string ListHeader => _plan.Campaigns.Count == 1 ? "WHAT GOES" : "WHAT GOES, ALL OF IT";

    public IReadOnlyList<string> Campaigns => _plan.Campaigns;

    /// <summary>Core saying what this does, in the same words the per-campaign delete uses.</summary>
    public string EffectText => _plan.Describe();

    public string StaysText => _plan.WhatStays;

    public string MapChoiceText => "Take the map they explored as well";

    public string SafetyText =>
        "The whole save folder is copied before " + _plan.TargetFileName + " is written, and the copy "
        + "is listed under Backups. Restoring it puts every save back as it is now.";

    public bool CanEmpty => _plan.CanWrite;

    public string BlockedReason => string.Join("\n", _plan.Problems.Count > 0 ? _plan.Problems : _plan.Write.Problems);

    public Visibility BlockedVisibility =>
        BlockedReason.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    private void RaiseAll()
    {
        foreach (string name in new[]
                 {
                     nameof(TakeTheMap), nameof(HeadlineText), nameof(TargetText), nameof(ListHeader),
                     nameof(Campaigns), nameof(EffectText), nameof(StaysText), nameof(SafetyText),
                     nameof(CanEmpty), nameof(BlockedReason), nameof(BlockedVisibility),
                 })
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    private void OnEmpty(object sender, RoutedEventArgs e)
    {
        if (CanEmpty)
        {
            DialogResult = true;
        }
    }
}
