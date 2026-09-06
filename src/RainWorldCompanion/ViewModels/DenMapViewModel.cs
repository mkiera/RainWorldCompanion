using CommunityToolkit.Mvvm.ComponentModel;
using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.ViewModels;

public sealed record DenTimelineOption(string Id)
{
    public string Label => SlugcatCatalog.ForId(Id).DisplayName;
    public override string ToString() => Label;
}

public sealed partial class DenMapViewModel : ObservableObject
{
    private readonly DenWorldCatalog _world;
    private bool _timelineConfirmed;
    private MappedDen? _pendingDen;
    private string? _selectionProblem;
    public string Timeline { get; private set; }
    public DenMapDefinition Map => DenMapCatalog.ForTimeline(Timeline, _world.DownpourEnabled)
        ?? (_world.DownpourEnabled ? DenMapCatalog.Downpour : DenMapCatalog.Vanilla);
    public string MapTitle => Map.Title;
    public IReadOnlyList<DenTimelineOption> TimelineOptions { get; private set; }
    public bool NeedsTimelineChoice => _pendingDen is not null;
    public string TimelineAdvice => _selectionProblem ?? (_pendingDen is not null
        ? $"Choose a compatible timeline above to use {_pendingDen.RoomId}."
        : "Use den applies the position and timeline together. Cancel discards both.");

    public DenMapViewModel(string currentRoomId, string fieldName, string timeline, DenWorldCatalog world)
    {
        _world = world;
        Timeline = timeline;
        TimelineOptions = AllTimelineOptions();
        FieldName = fieldName;
        CurrentRoomId = currentRoomId.Trim();
        selectedDen = CurrentDen;
        matches = VisibleDens;
    }

    public string FieldName { get; }
    public string CurrentRoomId { get; }
    public MappedDen? CurrentDen => Map.Find(CurrentRoomId);
    public bool HasCurrentDen => CurrentDen is not null;
    public string CurrentText => CurrentDen is not null ? $"Current: {CurrentRoomId}"
        : string.IsNullOrEmpty(CurrentRoomId) ? "No current den is set." : $"{CurrentRoomId} is not on this map.";

    [ObservableProperty]
    private string search = "";

    public IReadOnlyList<MappedDen> VisibleDens => Map.Dens.Where(IsAvailable).ToArray();
    public bool IsAvailable(MappedDen den) => Map.Find(den.RoomId) is not null && _world.Check(den.RoomId, Timeline).Available;

    [ObservableProperty]
    private IReadOnlyList<MappedDen> matches;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUseDen))]
    [NotifyPropertyChangedFor(nameof(SelectedRoomText))]
    [NotifyPropertyChangedFor(nameof(SelectedRegionText))]
    [NotifyPropertyChangedFor(nameof(SelectionAdvice))]
    private MappedDen? selectedDen;

    public bool CanUseDen => SelectedDen is not null && IsAvailable(SelectedDen);
    public string SelectedRoomText => SelectedDen?.RoomId ?? "Choose a den";
    public string SelectedRegionText => SelectedDen?.RegionName ?? "Click a marker or select from the list.";
    public string MatchCountText => $"{Matches.Count} of {VisibleDens.Count} dens";
    public string SelectionAdvice => SelectedDen is null ? "Select an available den. Dimmed icons are unavailable in the selected timeline."
        : _world.Explanation(SelectedDen.RoomId, Timeline);

    partial void OnSearchChanged(string value)
    {
        string query = value.Trim();
        Matches = VisibleDens.Where(den => den.RoomId.Contains(query, StringComparison.OrdinalIgnoreCase)
            || den.RegionName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || den.RegionCode.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        OnPropertyChanged(nameof(MatchCountText));
    }

    public bool TryChangeTimeline(string requested, Func<bool> confirm)
    {
        if (requested == Timeline) return true;
        if (!_world.SupportedTimelines.Contains(requested)) return false;
        if (_pendingDen is not null && !CompatibleTimelines(_pendingDen).Contains(requested)) return false;
        if (_pendingDen is not null)
        {
            if (!confirm())
            {
                ClearPendingDen();
                return false;
            }
        }
        else if (!_timelineConfirmed)
        {
            if (!confirm()) return false;
            _timelineConfirmed = true;
        }
        MappedDen? candidate = _pendingDen ?? SelectedDen;
        bool fromMarker = _pendingDen is not null;
        Timeline = requested;
        OnPropertyChanged(nameof(Timeline));
        OnPropertyChanged(nameof(Map));
        OnPropertyChanged(nameof(MapTitle));
        OnPropertyChanged(nameof(CurrentDen));
        OnPropertyChanged(nameof(HasCurrentDen));
        OnPropertyChanged(nameof(CurrentText));
        ClearPendingDen();
        if (fromMarker) Search = "";
        OnSearchChanged(Search);
        OnPropertyChanged(nameof(VisibleDens));
        SelectedDen = candidate is not null && IsAvailable(candidate) ? Map.Find(candidate.RoomId) : null;
        OnPropertyChanged(nameof(SelectedDen));
        OnPropertyChanged(nameof(CanUseDen));
        OnPropertyChanged(nameof(SelectionAdvice));
        return true;
    }

    public bool TrySelectDen(MappedDen den, Func<string, bool> confirmTimeline)
    {
        ClearPendingDen();
        if (IsAvailable(den))
        {
            Search = "";
            SelectedDen = Map.Find(den.RoomId);
            return true;
        }
        var alternatives = CompatibleTimelines(den);
        if (alternatives.Count == 0)
        {
            _selectionProblem = $"No installed timeline could be verified for {den.RoomId}.";
            OnPropertyChanged(nameof(TimelineAdvice));
            return false;
        }
        _pendingDen = den;
        if (alternatives.Count == 1)
        {
            bool changed = TryChangeTimeline(alternatives[0], () => confirmTimeline(alternatives[0]));
            if (!changed) ClearPendingDen();
            return changed;
        }
        TimelineOptions = alternatives.Prepend(Timeline).Distinct().Select(t => new DenTimelineOption(t)).ToArray();
        OnPropertyChanged(nameof(TimelineOptions));
        OnPropertyChanged(nameof(TimelineAdvice));
        return false;
    }

    private void ClearPendingDen()
    {
        _pendingDen = null;
        _selectionProblem = null;
        TimelineOptions = AllTimelineOptions();
        OnPropertyChanged(nameof(TimelineOptions));
        OnPropertyChanged(nameof(TimelineAdvice));
    }

    private IReadOnlyList<DenTimelineOption> AllTimelineOptions() => _world.SupportedTimelines.Append(Timeline)
        .Distinct().Select(t => new DenTimelineOption(t)).ToArray();

    private IReadOnlyList<string> CompatibleTimelines(MappedDen den) => _world.AvailableTimelines(den.RoomId)
        .Where(t => DenMapCatalog.ForTimeline(t, _world.DownpourEnabled)?.Find(den.RoomId) is not null).ToArray();
}
