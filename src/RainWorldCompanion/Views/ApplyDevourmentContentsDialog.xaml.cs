using System.ComponentModel;
using System.Windows;

using RainWorldCompanion.Core.Editing;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.Views;

public partial class ApplyDevourmentContentsDialog : Window, INotifyPropertyChanged
{
    private readonly Func<DevourmentContentsTarget, DevourmentContentsPlan> _replan;
    private DevourmentContentsTarget _selectedTarget;
    private DevourmentContentsPlan _plan;

    public ApplyDevourmentContentsDialog(
        string sourceCampaign,
        int entryCount,
        IReadOnlyList<DevourmentContentsTarget> targets,
        Func<DevourmentContentsTarget, DevourmentContentsPlan> replan)
    {
        if (targets.Count == 0)
        {
            throw new ArgumentException("At least one live campaign is required.", nameof(targets));
        }

        HeadlineText = entryCount == 1
            ? "Apply 1 stomach entry from " + sourceCampaign + "?"
            : "Apply " + entryCount + " stomach entries from " + sourceCampaign + "?";
        Targets = targets;
        _replan = replan;
        _selectedTarget = targets[0];
        _plan = _replan(_selectedTarget);

        InitializeComponent();
        DataContext = this;
        Loaded += (_, _) => CancelButton.Focus();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string HeadlineText { get; }

    public IReadOnlyList<DevourmentContentsTarget> Targets { get; }

    public DevourmentContentsTarget SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedTarget))
            {
                return;
            }

            _selectedTarget = value;
            _plan = _replan(value);
            RaiseAll();
        }
    }

    public DevourmentContentsPlan ChosenPlan => _plan;

    public bool CanApply => _plan.CanWrite;

    public string EffectText => _plan.Describe();

    public string BlockedReason => _plan.Problems.Count > 0
        ? string.Join("\n", _plan.Problems)
        : string.Join("\n", _plan.Write.Problems);

    public Visibility BlockedVisibility =>
        BlockedReason.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    private void RaiseAll()
    {
        foreach (string name in new[]
                 {
                     nameof(SelectedTarget), nameof(ChosenPlan), nameof(CanApply),
                     nameof(EffectText), nameof(BlockedReason), nameof(BlockedVisibility),
                 })
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (CanApply)
        {
            DialogResult = true;
        }
    }
}
