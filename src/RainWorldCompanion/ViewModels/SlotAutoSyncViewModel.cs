using CommunityToolkit.Mvvm.ComponentModel;
using RainWorldCompanion.Core.Backups;

namespace RainWorldCompanion.ViewModels;

/// <summary>
/// One slot's Autosync switch. Owned by the main window rather than the slot row, because the rows
/// are rebuilt on every reload and the switch and its last status must outlive them.
/// </summary>
public sealed partial class SlotAutoSyncViewModel : ObservableObject
{
    private readonly Action<int, bool> _setEnabled;
    private bool _applying;

    public SlotAutoSyncViewModel(int slot, Action<int, bool> setEnabled)
    {
        Slot = slot;
        _setEnabled = setEnabled;
    }

    public int Slot { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private bool isEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string statusText = "";

    [ObservableProperty]
    private bool isProblem;

    public bool HasStatus => StatusText.Length > 0;

    /// <summary>Sets the switch from the service without writing it back.</summary>
    public void Apply(bool enabled, SlotAutoSyncStatus status)
    {
        _applying = true;
        try
        {
            IsEnabled = enabled;
        }
        finally
        {
            _applying = false;
        }

        StatusText = status.Text;
        IsProblem = status.State is SlotAutoSyncState.Conflict or SlotAutoSyncState.Failed;
    }

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_applying)
        {
            _setEnabled(Slot, value);
        }
    }
}
