using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using RainWorldCompanion.Core.LogStreaming.Analysis;
using RainWorldCompanion.Core.Mods;
using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.ViewModels;

public sealed record CaptureTimelineTrackViewModel(
    string Id,
    string Name,
    string Role,
    string Coverage,
    bool HasLogs,
    IReadOnlyList<CaptureTimelineMoment> Moments);

public sealed record CaptureEventRowViewModel(CaptureTimelineMoment Moment)
{
    public long Sequence => Moment.Sequence;
    public string TimeText => Moment.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.CurrentCulture);
    public string Sender => Moment.SenderName.Length == 0 ? "Capture" : Moment.SenderName;
    public string Kind => Moment.Kind.Replace('-', ' ');
    public string Summary => Moment.Summary;
    public string Source => Moment.SourceFile;
    public CaptureEventSeverity Severity => Moment.Severity;
    public string SeverityText => Moment.Severity.ToString();
    public string Location => Moment.RoomId ?? Moment.Region ?? "";
}

public sealed record CaptureCauseViewModel(CaptureCauseCandidate Cause)
{
    public string Name => Cause.ModName;
    public string ScoreText => $"{Cause.Likelihood:P0} {Cause.ConfidenceText}";
    public string EvidenceText => string.Join(" ", Cause.Evidence);
}

public sealed record CaptureIncidentViewModel(CaptureIncident Incident)
{
    public string Id => Incident.Id;
    public string TimeText => Incident.Started.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.CurrentCulture);
    public string Summary => Incident.Summary;
    public string PlayersText => Incident.ParticipantNames.Count == 0
        ? "Capture"
        : string.Join(", ", Incident.ParticipantNames);
    public string LocationText => Incident.RoomId ?? Incident.Region ?? "Location varies";
    public string AlertText => Incident.IsCrossPlayer ? "CROSS-PLAYER" : Incident.Severity.ToString().ToUpperInvariant();
    public CaptureEventSeverity Severity => Incident.Severity;
}

public sealed record CaptureMapPlayerViewModel(
    string Id,
    string Name,
    string RoomId,
    string Region,
    string State,
    bool IsHost,
    bool HasLogs,
    bool HasNearbyError,
    MappedRoom? Placement,
    string Observer)
{
    public string CoverageText => HasLogs ? "Shared logs" : "Meadow observation only";
    public string LocationText => RoomId.Length == 0 ? "Location unavailable" : RoomId;
}

public sealed record CaptureModComparisonViewModel(
    string Id,
    string Name,
    string Builds,
    bool HasMismatch,
    bool HasVerificationGap,
    string StatusText);

public sealed partial class LogCaptureAnalysisViewModel : ObservableObject
{
    private const double GraphWidth = 600;
    private const double GraphHeight = 100;
    private const int MaximumRenderedMomentsPerTrack = 8_000;
    private static readonly TimeSpan LiveRefreshInterval = TimeSpan.FromSeconds(1);
    private readonly ILogCaptureAnalysisController _controller;
    private readonly Func<string, Task<string?>> _pickCaptureFolder;
    private CaptureAnalysisSnapshot _snapshot = CaptureAnalysisSnapshot.Empty();
    private string _currentCaptureFolder = "";
    private bool _currentCaptureActive;
    private DateTimeOffset _nextLiveRefresh;
    private CancellationTokenSource? _loadCancellation;
    private bool _queuedRefresh;
    private bool _suspendTimelineRebuild;
    private DispatcherTimer? _playbackTimer;
    private DateTimeOffset _lastPlaybackTick;
    private Dictionary<string, CapturePlayerObservation[]> _directPlayers = new(StringComparer.Ordinal);
    private Dictionary<string, CapturePlayerObservation[]> _nativePlayersByName = new(StringComparer.CurrentCultureIgnoreCase);
    private Dictionary<string, CaptureTimelineMoment[]> _errorsBySender = new(StringComparer.Ordinal);
    private Dictionary<string, CaptureMapContext[]> _mapContextsBySender = new(StringComparer.Ordinal);
    private Dictionary<string, CaptureModSnapshot[]> _modSnapshotsBySender = new(StringComparer.Ordinal);
    private CaptureMapContext[] _allMapContexts = [];

    public LogCaptureAnalysisViewModel(
        ILogCaptureAnalysisController? controller = null,
        Func<string, Task<string?>>? pickCaptureFolder = null)
    {
        _controller = controller ?? new LogCaptureAnalysisController();
        _pickCaptureFolder = pickCaptureFolder ?? PickCaptureFolderAsync;
    }

    public IReadOnlyList<double> PlaybackRates { get; } = [0.25, 0.5, 1, 2, 4];

    [ObservableProperty] private IReadOnlyList<CaptureIncidentViewModel> incidents = [];
    [ObservableProperty] private IReadOnlyList<CaptureEventRowViewModel> visibleEvents = [];
    [ObservableProperty] private IReadOnlyList<CaptureCauseViewModel> possibleCauses = [];
    [ObservableProperty] private IReadOnlyList<CaptureMapPlayerViewModel> playersAtCursor = [];
    [ObservableProperty] private IReadOnlyList<CaptureModComparisonViewModel> modComparison = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCaptureCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadCaptureCommand))]
    [NotifyCanExecuteChangedFor(nameof(UseCurrentCaptureCommand))]
    private bool isLoading;

    [ObservableProperty] private string captureFolder = "";
    [ObservableProperty] private string statusText = "Load a streamed-log capture, or start a capture and follow it live.";
    [ObservableProperty] private string warningText = "";
    [ObservableProperty] private bool followCurrentCapture = true;
    [ObservableProperty] private bool followLiveEdge = true;
    [ObservableProperty] private bool isPlaying;
    [ObservableProperty] private double playbackRate = 1;
    [ObservableProperty] private bool hasAnalysis;
    [ObservableProperty] private bool hasWarnings;
    [ObservableProperty] private IReadOnlyList<CaptureTimelineTrackViewModel> timelineTracks = [];
    [ObservableProperty] private IReadOnlyList<CaptureIncidentViewModel> timelineIncidents = [];
    [ObservableProperty] private DateTimeOffset timelineStart;
    [ObservableProperty] private DateTimeOffset timelineEnd;
    [ObservableProperty] private DateTimeOffset visibleStart;
    [ObservableProperty] private DateTimeOffset visibleEnd;
    [ObservableProperty] private DateTimeOffset cursorTime;
    [ObservableProperty] private double horizontalZoom = 1;
    [ObservableProperty] private double laneHeight = 46;
    [ObservableProperty] private CaptureTimelineMoment? selectedMoment;
    [ObservableProperty] private CaptureIncidentViewModel? selectedIncident;
    [ObservableProperty] private string selectedDetails = "Select an event or alert to inspect it.";
    [ObservableProperty] private string selectedTimingText = "";
    [ObservableProperty] private bool showErrors = true;
    [ObservableProperty] private bool showWarnings = true;
    [ObservableProperty] private bool showMovement = true;
    [ObservableProperty] private bool showConnections = true;
    [ObservableProperty] private bool showActions = true;
    [ObservableProperty] private bool showOtherEvents = true;
    [ObservableProperty] private DenMapDefinition? currentMap;
    [ObservableProperty] private string mapStatus = "No location data at the playhead.";
    [ObservableProperty] private PointCollection errorActivityPoints = [];
    [ObservableProperty] private PointCollection pingPoints = [];
    [ObservableProperty] private PointCollection frameTimePoints = [];
    [ObservableProperty] private PointCollection memoryPoints = [];
    [ObservableProperty] private string errorScaleText = "0";
    [ObservableProperty] private string pingScaleText = "0 ms";
    [ObservableProperty] private string frameScaleText = "0 ms";
    [ObservableProperty] private string memoryScaleText = "0 B";

    public string CaptureName => CaptureFolder.Length == 0 ? "No capture loaded" : Path.GetFileName(CaptureFolder);
    public string DurationText => Duration() is { } duration ? FormatDuration(duration) : "0 s";
    public string ParticipantCountText => _snapshot.Participants.Count.ToString("N0", CultureInfo.CurrentCulture);
    public string LogCoverageText => $"{_snapshot.Participants.Count(item => item.HasSharedLogs):N0}/{_snapshot.Participants.Count:N0}";
    public string ErrorCountText => _snapshot.Moments.Count(item => item.Severity >= CaptureEventSeverity.Error).ToString("N0", CultureInfo.CurrentCulture);
    public string WarningCountText => _snapshot.Moments.Count(item => item.Severity == CaptureEventSeverity.Warning).ToString("N0", CultureInfo.CurrentCulture);
    public string CrossPlayerIncidentText => _snapshot.Incidents.Count(item => item.IsCrossPlayer).ToString("N0", CultureInfo.CurrentCulture);
    public string CaptureHealthText => IsCurrentCaptureActive ? "Recording"
        : _snapshot.HasGaps ? "Gaps detected" : _snapshot.IsIncomplete ? "Interrupted or unfinished" : "Complete";
    public string CaptureHealthDetail => IsCurrentCaptureActive ? "Capture is still growing." : _snapshot.TerminationText;
    public string CursorText => CursorTime == default ? "No playhead" : CursorTime.ToLocalTime().ToString("MMM d  HH:mm:ss.fff", CultureInfo.CurrentCulture);
    public string VisibleRangeText => VisibleStart == default || VisibleEnd == default
        ? "No timeline"
        : $"{VisibleStart.ToLocalTime():HH:mm:ss} to {VisibleEnd.ToLocalTime():HH:mm:ss} ({FormatDuration(VisibleEnd - VisibleStart)})";
    public string TimelineStartText => VisibleStart == default ? "" : VisibleStart.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);
    public string TimelineEndText => VisibleEnd == default ? "" : VisibleEnd.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);
    public string HorizontalScaleText => HorizontalZoom <= 1.001 ? "Fit session" : $"{HorizontalZoom:F1}x time zoom";
    public string LaneScaleText => $"{LaneHeight:N0} px lanes";
    public string PlaybackActionText => IsPlaying ? "Pause" : "Play";
    public bool HasIncidents => Incidents.Count > 0;
    public bool HasNoIncidents => !HasIncidents;
    public bool HasVisibleEvents => VisibleEvents.Count > 0;
    public bool HasNoVisibleEvents => !HasVisibleEvents;
    public bool HasPossibleCauses => PossibleCauses.Count > 0;
    public bool HasNoPossibleCauses => !HasPossibleCauses;
    public bool HasPlayersAtCursor => PlayersAtCursor.Count > 0;
    public bool HasNoPlayersAtCursor => !HasPlayersAtCursor;
    public bool HasModComparison => ModComparison.Count > 0;
    public bool HasNoModComparison => !HasModComparison;
    public bool HasModMismatches => ModComparison.Any(item => item.HasMismatch);
    public bool HasModVerificationGaps => ModComparison.Any(item => item.HasVerificationGap);
    public string ModMismatchSummary
    {
        get
        {
            CaptureModComparisonViewModel[] mismatches = ModComparison.Where(item => item.HasMismatch).ToArray();
            if (mismatches.Length == 0) return "All reported mod builds match.";
            string[] sameVersionBuilds = mismatches
                .Where(item => item.StatusText == "Same stated version, different code fingerprints")
                .Select(item => item.Name).ToArray();
            if (sameVersionBuilds.Length > 0)
                return $"{string.Join(", ", sameVersionBuilds.Take(3))} "
                    + (sameVersionBuilds.Length == 1 ? "reports" : "report")
                    + " the same version but different code fingerprints across players.";
            return $"{mismatches.Length:N0} reported mod {(mismatches.Length == 1 ? "build does" : "builds do")} not match across players.";
        }
    }
    public string ModVerificationSummary
    {
        get
        {
            CaptureModComparisonViewModel[] gaps = ModComparison.Where(item => item.HasVerificationGap).ToArray();
            if (gaps.Length == 0) return "";
            return gaps.Any(item => item.StatusText.Contains("inventory", StringComparison.CurrentCultureIgnoreCase))
                ? "One or more players did not provide a complete mod inventory, so some build comparisons cannot be verified."
                : "One or more code fingerprints are unavailable or incomplete, so those build comparisons cannot be verified.";
        }
    }
    public bool CanUseCurrentCapture => !IsLoading && _currentCaptureFolder.Length > 0;
    public bool CanRefreshCapture => !IsLoading && CaptureFolder.Length > 0;
    public string LiveFollowText => FollowCurrentCapture
        ? _currentCaptureActive ? "Following the current capture live" : "Following the latest current capture"
        : "Viewing a saved capture";
    private bool IsCurrentCaptureActive => _currentCaptureActive && CaptureFolder.Length > 0
        && string.Equals(CaptureFolder, _currentCaptureFolder, StringComparison.OrdinalIgnoreCase);

    partial void OnHorizontalZoomChanged(double value)
    {
        double normalized = Math.Clamp(double.IsFinite(value) ? value : 1, 1, 200);
        if (Math.Abs(normalized - value) > 0.001) { HorizontalZoom = normalized; return; }
        UpdateVisibleRange(CursorTime == default ? null : CursorTime, 0.5);
        OnPropertyChanged(nameof(HorizontalScaleText));
    }

    partial void OnLaneHeightChanged(double value)
    {
        double normalized = Math.Clamp(double.IsFinite(value) ? value : 46, 26, 90);
        if (Math.Abs(normalized - value) > 0.001) { LaneHeight = normalized; return; }
        OnPropertyChanged(nameof(LaneScaleText));
    }

    partial void OnPlaybackRateChanged(double value)
    {
        double normalized = value is 0.25 or 0.5 or 1 or 2 or 4 ? value : 1;
        if (Math.Abs(normalized - value) > 0.001) PlaybackRate = normalized;
    }

    partial void OnIsPlayingChanged(bool value)
    {
        OnPropertyChanged(nameof(PlaybackActionText));
        if (!value)
        {
            _playbackTimer?.Stop();
            return;
        }
        _playbackTimer ??= CreatePlaybackTimer();
        _lastPlaybackTick = DateTimeOffset.UtcNow;
        _playbackTimer.Start();
    }

    partial void OnCursorTimeChanged(DateTimeOffset value)
    {
        OnPropertyChanged(nameof(CursorText));
        UpdateCursorState();
    }

    partial void OnVisibleStartChanged(DateTimeOffset value)
    {
        OnPropertyChanged(nameof(VisibleRangeText));
        OnPropertyChanged(nameof(TimelineStartText));
        if (!_suspendTimelineRebuild) RebuildTracks();
    }

    partial void OnVisibleEndChanged(DateTimeOffset value)
    {
        OnPropertyChanged(nameof(VisibleRangeText));
        OnPropertyChanged(nameof(TimelineEndText));
        if (!_suspendTimelineRebuild) RebuildTracks();
    }

    partial void OnSelectedMomentChanged(CaptureTimelineMoment? value)
    {
        if (value is null) return;
        FollowLiveEdge = false;
        CursorTime = value.Timestamp;
        SelectedDetails = value.Details.Length == 0 ? value.Summary : value.Details;
        SelectedTimingText = TimingText(value);
        SelectedIncident = Incidents.FirstOrDefault(incident =>
            incident.Incident.MomentSequences.Contains(value.Sequence));
    }

    partial void OnSelectedIncidentChanged(CaptureIncidentViewModel? value)
    {
        PossibleCauses = value is null
            ? []
            : value.Incident.PossibleCauses.Select(cause => new CaptureCauseViewModel(cause)).ToArray();
        if (value is null)
        {
            RefreshCollectionState();
            return;
        }
        FollowLiveEdge = false;
        CaptureTimelineMoment? selected = SelectedMoment is { } current
            && value.Incident.MomentSequences.Contains(current.Sequence) ? current : null;
        selected ??= _snapshot.Moments.FirstOrDefault(moment =>
            value.Incident.MomentSequences.Contains(moment.Sequence));
        if (selected is not null)
        {
            SelectedMoment = selected;
            CursorTime = selected.Timestamp;
            SelectedDetails = selected.Details.Length == 0 ? selected.Summary : selected.Details;
            SelectedTimingText = TimingText(selected);
        }
        else CursorTime = value.Incident.Started
            + TimeSpan.FromTicks((value.Incident.Ended - value.Incident.Started).Ticks / 2);
        RefreshCollectionState();
    }

    partial void OnShowErrorsChanged(bool value) => RebuildTracks();
    partial void OnShowWarningsChanged(bool value) => RebuildTracks();
    partial void OnShowMovementChanged(bool value) => RebuildTracks();
    partial void OnShowConnectionsChanged(bool value) => RebuildTracks();
    partial void OnShowActionsChanged(bool value) => RebuildTracks();
    partial void OnShowOtherEventsChanged(bool value) => RebuildTracks();

    public void ObserveCurrentCapture(string folder, bool active)
    {
        bool wasActive = _currentCaptureActive;
        _currentCaptureFolder = string.IsNullOrWhiteSpace(folder) ? "" : Path.GetFullPath(folder);
        _currentCaptureActive = active;
        OnPropertyChanged(nameof(CanUseCurrentCapture));
        OnPropertyChanged(nameof(LiveFollowText));
        OnPropertyChanged(nameof(CaptureHealthText));
        OnPropertyChanged(nameof(CaptureHealthDetail));
        UseCurrentCaptureCommand.NotifyCanExecuteChanged();
        if (!FollowCurrentCapture || _currentCaptureFolder.Length == 0) return;
        if (!string.Equals(CaptureFolder, _currentCaptureFolder, StringComparison.OrdinalIgnoreCase))
        {
            QueueLoad(_currentCaptureFolder, open: true);
            return;
        }
        if ((active && DateTimeOffset.UtcNow >= _nextLiveRefresh) || wasActive && !active)
            QueueLoad(_currentCaptureFolder, open: false);
    }

    [RelayCommand]
    private async Task LoadCaptureAsync()
    {
        if (IsLoading) return;
        string? folder;
        try { folder = await _pickCaptureFolder(CaptureFolder); }
        catch (Exception error)
        {
            StatusText = "The capture picker failed: " + error.Message;
            return;
        }
        if (string.IsNullOrWhiteSpace(folder)) return;
        FollowCurrentCapture = false;
        OnPropertyChanged(nameof(LiveFollowText));
        await LoadAsync(folder, open: true);
    }

    [RelayCommand(CanExecute = nameof(CanUseCurrentCapture))]
    private Task UseCurrentCaptureAsync()
    {
        FollowCurrentCapture = true;
        FollowLiveEdge = true;
        OnPropertyChanged(nameof(LiveFollowText));
        return LoadAsync(_currentCaptureFolder, open: true);
    }

    [RelayCommand(CanExecute = nameof(CanRefreshCapture))]
    private Task RefreshCaptureAsync() => LoadAsync(CaptureFolder, open: false);

    [RelayCommand]
    private void FitTimeline()
    {
        if (Math.Abs(HorizontalZoom - 1) > 0.001) HorizontalZoom = 1;
        else SetVisibleRange(TimelineStart, TimelineEnd);
    }

    [RelayCommand]
    private void ZoomTimelineIn() => ZoomTimeline(1.6, 0.5);

    [RelayCommand]
    private void ZoomTimelineOut() => ZoomTimeline(1 / 1.6, 0.5);

    [RelayCommand]
    private void FocusSelection()
    {
        DateTimeOffset focus = SelectedIncident?.Incident.Started ?? SelectedMoment?.Timestamp ?? CursorTime;
        if (focus == default || TimelineEnd <= TimelineStart) return;
        double fullSeconds = Math.Max(0.001, (TimelineEnd - TimelineStart).TotalSeconds);
        double targetSeconds = Math.Clamp(fullSeconds / 12, 8, 30);
        HorizontalZoom = Math.Clamp(fullSeconds / targetSeconds, 1, 200);
        UpdateVisibleRange(focus, 0.5);
    }

    [RelayCommand]
    private void PreviousAlert() => JumpAlert(previous: true);

    [RelayCommand]
    private void NextAlert() => JumpAlert(previous: false);

    [RelayCommand]
    private void TogglePlayback()
    {
        if (!HasAnalysis) return;
        if (!IsPlaying && CursorTime >= TimelineEnd) CursorTime = TimelineStart;
        FollowLiveEdge = false;
        IsPlaying = !IsPlaying;
    }

    public void SetPlayhead(DateTimeOffset time)
    {
        if (!HasAnalysis) return;
        FollowLiveEdge = false;
        CursorTime = Clamp(time, TimelineStart, TimelineEnd);
        SelectedMoment = _snapshot.Moments
            .Where(moment => IsVisible(moment))
            .MinBy(moment => Math.Abs((moment.Timestamp - CursorTime).Ticks));
    }

    public void ZoomTimeline(double factor, double anchorFraction)
    {
        if (!HasAnalysis || !double.IsFinite(factor) || factor <= 0) return;
        DateTimeOffset anchor = VisibleStart + TimeSpan.FromTicks((long)((VisibleEnd - VisibleStart).Ticks
            * Math.Clamp(anchorFraction, 0, 1)));
        HorizontalZoom = Math.Clamp(HorizontalZoom * factor, 1, 200);
        UpdateVisibleRange(anchor, Math.Clamp(anchorFraction, 0, 1));
    }

    public void PanTimeline(double fraction)
    {
        if (!HasAnalysis || VisibleEnd <= VisibleStart) return;
        TimeSpan shift = TimeSpan.FromTicks((long)((VisibleEnd - VisibleStart).Ticks * fraction));
        SetVisibleRange(VisibleStart + shift, VisibleEnd + shift);
        FollowLiveEdge = false;
    }

    private void QueueLoad(string folder, bool open)
    {
        if (IsLoading)
        {
            _queuedRefresh = true;
            return;
        }
        _ = LoadAsync(folder, open);
    }

    private async Task LoadAsync(string folder, bool open)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new();
        CancellationToken token = _loadCancellation.Token;
        IsLoading = true;
        StatusText = open ? "Loading capture..." : "Updating capture...";
        try
        {
            CaptureAnalysisSnapshot snapshot = open
                ? await _controller.OpenAsync(folder, token)
                : await _controller.RefreshAsync(token);
            if (!token.IsCancellationRequested) Adopt(snapshot, open);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!token.IsCancellationRequested) StatusText = "The capture could not be loaded: " + error.Message;
        }
        finally
        {
            IsLoading = false;
            _nextLiveRefresh = DateTimeOffset.UtcNow + LiveRefreshInterval;
            RefreshCaptureCommand.NotifyCanExecuteChanged();
            LoadCaptureCommand.NotifyCanExecuteChanged();
            UseCurrentCaptureCommand.NotifyCanExecuteChanged();
            if (_queuedRefresh)
            {
                _queuedRefresh = false;
                if (FollowCurrentCapture && _currentCaptureFolder.Length > 0
                    && !string.Equals(CaptureFolder, _currentCaptureFolder, StringComparison.OrdinalIgnoreCase))
                    QueueLoad(_currentCaptureFolder, open: true);
            }
        }
    }

    private void Adopt(CaptureAnalysisSnapshot snapshot, bool open)
    {
        DateTimeOffset previousCursor = CursorTime;
        bool preserveViewport = !open && !FollowLiveEdge && VisibleEnd > VisibleStart
            && string.Equals(_snapshot.CaptureId, snapshot.CaptureId, StringComparison.Ordinal)
            && string.Equals(_snapshot.CaptureFolder, snapshot.CaptureFolder, StringComparison.OrdinalIgnoreCase);
        string? selectedIncidentId = SelectedIncident?.Id;
        long? selectedSequence = SelectedMoment?.Sequence;
        _snapshot = snapshot;
        BuildIndexes();
        CaptureFolder = snapshot.CaptureFolder;
        StatusText = snapshot.StatusText;
        WarningText = string.Join(Environment.NewLine, snapshot.Warnings);
        HasWarnings = snapshot.Warnings.Count > 0;
        HasAnalysis = snapshot.Moments.Count > 0 || snapshot.PlayerObservations.Count > 0
            || snapshot.NetworkSamples.Count > 0 || snapshot.PerformanceSamples.Count > 0;
        TimelineStart = snapshot.StartedUtc
            ?? snapshot.Moments.Select(moment => (DateTimeOffset?)moment.Timestamp).FirstOrDefault()
            ?? DateTimeOffset.UtcNow;
        TimelineEnd = snapshot.EndedUtc
            ?? snapshot.Moments.Select(moment => (DateTimeOffset?)moment.Timestamp).LastOrDefault()
            ?? TimelineStart;
        if (TimelineEnd <= TimelineStart) TimelineEnd = TimelineStart.AddSeconds(1);

        Incidents = snapshot.Incidents.OrderBy(incident => incident.Started)
            .Select(incident => new CaptureIncidentViewModel(incident)).ToArray();
        SelectedIncident = selectedIncidentId is null ? null : Incidents.FirstOrDefault(item => item.Id == selectedIncidentId);
        SelectedMoment = selectedSequence is null ? null : snapshot.Moments.FirstOrDefault(item => item.Sequence == selectedSequence);

        if (previousCursor == default || FollowLiveEdge) CursorTime = TimelineEnd;
        else CursorTime = Clamp(previousCursor, TimelineStart, TimelineEnd);
        if (preserveViewport) RebuildTracks();
        else UpdateVisibleRange(CursorTime, FollowLiveEdge ? 1 : 0.5);
        UpdateCursorState();
        RefreshSummaryState();
    }

    private void RebuildTracks()
    {
        var tracks = new List<CaptureTimelineTrackViewModel>();
        foreach (CaptureParticipant participant in _snapshot.Participants)
        {
            CaptureTimelineMoment[] moments = participant.HasSharedLogs
                ? TimelineMoments(_snapshot.Moments.Where(moment => moment.SenderId == participant.Id))
                : [];
            tracks.Add(new(participant.Id, participant.DisplayName,
                participant.IsHost == true ? "HOST" : "CLIENT", participant.CoverageText,
                participant.HasSharedLogs, moments));
        }
        CaptureTimelineMoment[] capture = TimelineMoments(_snapshot.Moments
            .Where(moment => moment.SenderId.Length == 0));
        if (capture.Length > 0) tracks.Insert(0,
            new("capture", "Capture", "SYSTEM", "Receiver events and markers", true, capture));
        TimelineTracks = tracks;
        TimelineIncidents = Incidents.Where(IsVisible).ToArray();
        RefreshVisibleEventsAndGraphs();
    }

    private bool IsVisible(CaptureIncidentViewModel incident)
    {
        if (incident.Incident.Severity >= CaptureEventSeverity.Error) return ShowErrors;
        if (incident.Incident.Severity == CaptureEventSeverity.Warning) return ShowWarnings;
        return true;
    }

    private CaptureTimelineMoment[] TimelineMoments(IEnumerable<CaptureTimelineMoment> source)
    {
        CaptureTimelineMoment[] visible = source.Where(moment => IsVisible(moment)
            && moment.Timestamp >= VisibleStart && moment.Timestamp <= VisibleEnd).ToArray();
        if (visible.Length <= MaximumRenderedMomentsPerTrack) return visible;
        CaptureTimelineMoment[] priority = visible.Where(moment => moment.Severity >= CaptureEventSeverity.Warning
            || moment.Category is CaptureEventCategory.Action or CaptureEventCategory.Marker).ToArray();
        if (priority.Length >= MaximumRenderedMomentsPerTrack)
        {
            int stride = (int)Math.Ceiling(priority.Length / (double)MaximumRenderedMomentsPerTrack);
            return priority.Where((_, index) => index % stride == 0).Take(MaximumRenderedMomentsPerTrack).ToArray();
        }
        int remaining = MaximumRenderedMomentsPerTrack - priority.Length;
        CaptureTimelineMoment[] ordinary = visible.Where(moment => moment.Severity < CaptureEventSeverity.Warning
            && moment.Category is not (CaptureEventCategory.Action or CaptureEventCategory.Marker)).ToArray();
        int ordinaryStride = Math.Max(1, (int)Math.Ceiling(ordinary.Length / (double)remaining));
        return priority.Concat(ordinary.Where((_, index) => index % ordinaryStride == 0).Take(remaining))
            .OrderBy(moment => moment.Timestamp).ToArray();
    }

    private bool IsVisible(CaptureTimelineMoment moment)
    {
        if (moment.Severity >= CaptureEventSeverity.Error) return ShowErrors;
        if (moment.Severity == CaptureEventSeverity.Warning) return ShowWarnings;
        return moment.Category switch
        {
            CaptureEventCategory.Location or CaptureEventCategory.Player => ShowMovement,
            CaptureEventCategory.Connection or CaptureEventCategory.Network => ShowConnections,
            CaptureEventCategory.Action or CaptureEventCategory.Marker => ShowActions,
            _ => ShowOtherEvents,
        };
    }

    private void UpdateVisibleRange(DateTimeOffset? anchor, double anchorFraction)
    {
        if (TimelineEnd <= TimelineStart) return;
        TimeSpan full = TimelineEnd - TimelineStart;
        TimeSpan visible = TimeSpan.FromTicks(Math.Max(1, (long)(full.Ticks / Math.Clamp(HorizontalZoom, 1, 200))));
        DateTimeOffset point = anchor ?? TimelineStart + TimeSpan.FromTicks(full.Ticks / 2);
        DateTimeOffset start = point - TimeSpan.FromTicks((long)(visible.Ticks * Math.Clamp(anchorFraction, 0, 1)));
        SetVisibleRange(start, start + visible);
    }

    private void SetVisibleRange(DateTimeOffset start, DateTimeOffset end)
    {
        TimeSpan width = end - start;
        if (width <= TimeSpan.Zero) return;
        if (start < TimelineStart) { start = TimelineStart; end = start + width; }
        if (end > TimelineEnd) { end = TimelineEnd; start = end - width; }
        if (start < TimelineStart) start = TimelineStart;
        if (end <= start) end = start.AddTicks(1);
        bool wasSuspended = _suspendTimelineRebuild;
        _suspendTimelineRebuild = true;
        try
        {
            VisibleStart = start;
            VisibleEnd = end;
        }
        finally
        {
            _suspendTimelineRebuild = wasSuspended;
        }
        if (!wasSuspended) RebuildTracks();
    }

    private void JumpAlert(bool previous)
    {
        CaptureTimelineMoment[] alerts = _snapshot.Moments
            .Where(moment => moment.Severity >= CaptureEventSeverity.Warning)
            .OrderBy(moment => moment.Timestamp).ToArray();
        CaptureTimelineMoment? target = previous
            ? alerts.LastOrDefault(moment => moment.Timestamp < CursorTime)
            : alerts.FirstOrDefault(moment => moment.Timestamp > CursorTime);
        if (target is null) return;
        SelectedMoment = target;
        if (target.Timestamp < VisibleStart || target.Timestamp > VisibleEnd) UpdateVisibleRange(target.Timestamp, 0.5);
    }

    private void RefreshVisibleEventsAndGraphs()
    {
        if (!HasAnalysis || VisibleEnd <= VisibleStart)
        {
            VisibleEvents = [];
            ErrorActivityPoints = [];
            PingPoints = [];
            FrameTimePoints = [];
            MemoryPoints = [];
            ErrorScaleText = "0";
            PingScaleText = "0 ms";
            FrameScaleText = "0 ms";
            MemoryScaleText = "0 B";
            RefreshCollectionState();
            return;
        }
        CaptureTimelineMoment[] visible = _snapshot.Moments
            .Where(moment => moment.Timestamp >= VisibleStart && moment.Timestamp <= VisibleEnd && IsVisible(moment))
            .TakeLast(2_000).ToArray();
        VisibleEvents = visible.Select(moment => new CaptureEventRowViewModel(moment)).ToArray();
        RefreshGraphs();
        RefreshCollectionState();
    }

    private void UpdateCursorState()
    {
        if (!HasAnalysis || CursorTime == default)
        {
            CurrentMap = null;
            PlayersAtCursor = [];
            ModComparison = [];
            MapStatus = "No location data at the playhead.";
            RefreshCollectionState();
            return;
        }
        CaptureMapContext? context = MapContextAtCursor();
        string timeline = context is null ? "" : DenWorldCatalog.EffectiveTimeline(context.Campaign, context.Timeline);
        CurrentMap = timeline.Length == 0 ? null : DenMapCatalog.ForTimeline(timeline, context!.DownpourEnabled);
        var players = new List<CaptureMapPlayerViewModel>();
        var representedNames = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach ((string playerKey, CapturePlayerObservation[] history) in _directPlayers)
        {
            CapturePlayerObservation? observation = LastAtOrBefore(history, CursorTime, item => item.Timestamp);
            if (observation is null || !observation.IsAvailable) continue;
            bool nearbyError = _errorsBySender.TryGetValue(observation.SenderId, out CaptureTimelineMoment[]? errors)
                && HasMomentWithin(errors, CursorTime, TimeSpan.FromSeconds(2));
            players.Add(MapPlayer(playerKey, observation, hasLogs: true, nearbyError));
            representedNames.Add(observation.PlayerName);
        }
        foreach ((string playerName, CapturePlayerObservation[] history) in _nativePlayersByName)
        {
            if (representedNames.Contains(playerName)) continue;
            CapturePlayerObservation? observation = LastAtOrBefore(history, CursorTime, item => item.Timestamp);
            if (observation is null || !observation.IsAvailable) continue;
            bool hasLogs = _snapshot.Participants.Any(participant => participant.HasSharedLogs
                && participant.DisplayName.Equals(playerName, StringComparison.CurrentCultureIgnoreCase));
            players.Add(MapPlayer("native:" + observation.PlayerId, observation, hasLogs, nearbyError: false));
        }
        PlayersAtCursor = players;
        MapStatus = CurrentMap is null
            ? timeline.Length == 0 ? "No campaign timeline was recorded before the playhead." : $"No bundled map is available for {timeline}."
            : $"{CurrentMap.Id} map. {players.Count(player => player.Placement is not null)}/{players.Count} visible players placed at {CursorTime.ToLocalTime():HH:mm:ss}.";
        RefreshModComparison();
        RefreshCollectionState();
    }

    private CaptureMapPlayerViewModel MapPlayer(
        string id,
        CapturePlayerObservation observation,
        bool hasLogs,
        bool nearbyError)
    {
        string room = observation.RoomId ?? "";
        string region = observation.Region ?? "";
        MappedRoom? placement = CurrentMap is null || room.Length == 0
            ? null
            : RoomMapCatalog.Find(CurrentMap.Id, room);
        return new(id, observation.PlayerName, room, region,
            observation.Dead switch { true => "Dead", false => "Alive", _ => "Unknown" },
            observation.IsHost, hasLogs, nearbyError, placement, observation.ObserverName);
    }

    private CaptureMapContext? MapContextAtCursor()
    {
        if (SelectedMoment is { SenderId.Length: > 0 } selected)
        {
            CaptureMapContext? sender = _mapContextsBySender.TryGetValue(selected.SenderId, out CaptureMapContext[]? contexts)
                ? LastAtOrBefore(contexts, CursorTime, context => context.Timestamp)
                : null;
            if (sender is not null) return sender;
        }
        return LastAtOrBefore(_allMapContexts, CursorTime, context => context.Timestamp);
    }

    private void RefreshModComparison()
    {
        string[] senders = _snapshot.Participants.Where(item => item.HasSharedLogs).Select(item => item.Id).ToArray();
        CaptureModSnapshot[] current = senders
            .Select(sender => _modSnapshotsBySender.TryGetValue(sender, out CaptureModSnapshot[]? snapshots)
                ? LastAtOrBefore(snapshots, CursorTime, snapshot => snapshot.Timestamp)
                : null)
            .Where(snapshot => snapshot is not null).Cast<CaptureModSnapshot>().ToArray();
        var names = _snapshot.Participants.ToDictionary(item => item.Id, item => item.DisplayName, StringComparer.Ordinal);
        var currentBySender = current.ToDictionary(snapshot => snapshot.SenderId, StringComparer.Ordinal);
        var comparisons = new List<CaptureModComparisonViewModel>();
        foreach (string modId in current.SelectMany(snapshot => snapshot.Mods).Select(mod => mod.Id)
                     .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            bool compareFingerprint = !EnabledModsFile.BuiltIn.Contains(modId);
            var builds = new List<string>();
            var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int installed = 0;
            bool missingInventory = false;
            bool truncatedInventory = false;
            bool incompleteFingerprint = false;
            foreach (string sender in senders)
            {
                if (!currentBySender.TryGetValue(sender, out CaptureModSnapshot? snapshot))
                {
                    missingInventory = true;
                    builds.Add(names.GetValueOrDefault(sender, sender) + ": inventory not recorded");
                    continue;
                }
                truncatedInventory |= snapshot.Truncated;
                CaptureModEntry? mod = snapshot.Mods.FirstOrDefault(candidate =>
                    candidate.Id.Equals(modId, StringComparison.OrdinalIgnoreCase));
                if (mod is null)
                {
                    builds.Add(names.GetValueOrDefault(sender, sender) +
                        (snapshot.Truncated ? ": not listed (inventory truncated)" : ": not installed"));
                    continue;
                }
                installed++;
                if (mod.Version.Length > 0) versions.Add(mod.Version);
                if (compareFingerprint)
                {
                    if (mod.CodeFingerprint.Length > 0) hashes.Add(mod.CodeFingerprint);
                    incompleteFingerprint |= !IsCompleteFingerprint(mod);
                    string hash = mod.CodeFingerprint.Length == 0
                        ? "hash unavailable"
                        : mod.CodeFingerprint[..Math.Min(10, mod.CodeFingerprint.Length)];
                    builds.Add($"{names.GetValueOrDefault(sender, sender)}: {(mod.Version.Length == 0 ? "version unknown" : mod.Version)}, hash {hash}");
                }
                else builds.Add($"{names.GetValueOrDefault(sender, sender)}: "
                    + (mod.Version.Length == 0 ? "bundled with Rain World" : mod.Version + " (bundled with Rain World)"));
            }
            CaptureModEntry representative = current.SelectMany(snapshot => snapshot.Mods)
                .First(mod => mod.Id.Equals(modId, StringComparison.OrdinalIgnoreCase));
            bool versionMismatch = versions.Count > 1;
            bool fingerprintMismatch = hashes.Count > 1 && !incompleteFingerprint;
            bool listedByEverySender = installed == senders.Length;
            bool installationMismatch = !listedByEverySender && !missingInventory && !truncatedInventory;
            bool mismatch = versionMismatch || fingerprintMismatch || installationMismatch;
            bool verificationGap = !mismatch && (missingInventory || (!listedByEverySender && truncatedInventory)
                || incompleteFingerprint);
            bool sameStatedVersion = versions.Count == 1 && listedByEverySender
                && current.All(snapshot => snapshot.Mods.Any(mod => mod.Id.Equals(modId, StringComparison.OrdinalIgnoreCase)
                    && mod.Version.Length > 0));
            string status = versionMismatch ? "Version mismatch"
                : fingerprintMismatch ? sameStatedVersion
                    ? "Same stated version, different code fingerprints"
                    : "Different code fingerprints"
                : installationMismatch ? "Installed on only some log-sharing players"
                : hashes.Count > 1 ? "Different reported fingerprints, verification incomplete"
                : missingInventory ? "Mod inventory not recorded for every log-sharing player"
                : !listedByEverySender && truncatedInventory ? "Mod may be omitted from a truncated inventory"
                : incompleteFingerprint ? "Code fingerprint unavailable or incomplete"
                : !compareFingerprint ? "Bundled with Rain World"
                : "Matching version and code fingerprint";
            comparisons.Add(new(modId, representative.DisplayName, string.Join(Environment.NewLine, builds), mismatch,
                verificationGap, status));
        }
        foreach (string sender in senders.Where(sender => !currentBySender.ContainsKey(sender)))
        {
            comparisons.Add(new("inventory:" + sender, names.GetValueOrDefault(sender, sender),
                names.GetValueOrDefault(sender, sender) + ": inventory not recorded", false, true,
                "Mod inventory not recorded before the playhead"));
        }
        foreach (CaptureModSnapshot snapshot in current.Where(snapshot => snapshot.Truncated))
        {
            string name = names.GetValueOrDefault(snapshot.SenderId, snapshot.SenderName);
            comparisons.Add(new("inventory:" + snapshot.SenderId, name,
                name + ": inventory was truncated", false, true, "Mod inventory was truncated"));
        }
        ModComparison = comparisons;
        OnPropertyChanged(nameof(HasModMismatches));
        OnPropertyChanged(nameof(HasModVerificationGaps));
        OnPropertyChanged(nameof(ModMismatchSummary));
        OnPropertyChanged(nameof(ModVerificationSummary));
    }

    private static bool IsCompleteFingerprint(CaptureModEntry mod)
        => mod.CodeFingerprint.Length > 0 && mod.FingerprintStatus.Equals("complete", StringComparison.OrdinalIgnoreCase);

    private void BuildIndexes()
    {
        _directPlayers = _snapshot.PlayerObservations.Where(item => item.IsLocal
                && item.Authority is CaptureObservationAuthority.Direct or CaptureObservationAuthority.DeepTrace)
            .GroupBy(item => item.SenderId + "\n" + item.PlayerId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.OrderBy(item => item.Timestamp).ThenBy(item => item.Authority).ToArray(),
                StringComparer.Ordinal);
        _nativePlayersByName = _snapshot.PlayerObservations.Where(item => item.Authority == CaptureObservationAuthority.Native)
            .GroupBy(item => item.PlayerName, StringComparer.CurrentCultureIgnoreCase)
            .ToDictionary(group => group.Key,
                group => group.OrderBy(item => item.Timestamp).ThenBy(item => item.Authority).ToArray(),
                StringComparer.CurrentCultureIgnoreCase);
        _errorsBySender = _snapshot.Moments.Where(item => item.Severity >= CaptureEventSeverity.Error)
            .GroupBy(item => item.SenderId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.Timestamp).ToArray(),
                StringComparer.Ordinal);
        _mapContextsBySender = _snapshot.MapContexts.GroupBy(item => item.SenderId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.Timestamp).ToArray(),
                StringComparer.Ordinal);
        _modSnapshotsBySender = _snapshot.ModSnapshots.GroupBy(item => item.SenderId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.Timestamp).ToArray(),
                StringComparer.Ordinal);
        _allMapContexts = _snapshot.MapContexts.OrderBy(item => item.Timestamp).ToArray();
    }

    private static T? LastAtOrBefore<T>(
        IReadOnlyList<T> items,
        DateTimeOffset time,
        Func<T, DateTimeOffset> timestamp)
        where T : class
    {
        int low = 0;
        int high = items.Count - 1;
        int best = -1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            if (timestamp(items[middle]) <= time)
            {
                best = middle;
                low = middle + 1;
            }
            else high = middle - 1;
        }
        return best < 0 ? null : items[best];
    }

    private static bool HasMomentWithin(
        IReadOnlyList<CaptureTimelineMoment> moments,
        DateTimeOffset time,
        TimeSpan tolerance)
    {
        DateTimeOffset earliest = time - tolerance;
        int low = 0;
        int high = moments.Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (moments[middle].Timestamp < earliest) low = middle + 1;
            else high = middle;
        }
        return low < moments.Count && moments[low].Timestamp <= time + tolerance;
    }

    private void RefreshGraphs()
    {
        if (VisibleEnd <= VisibleStart)
        {
            ErrorActivityPoints = [];
            PingPoints = [];
            FrameTimePoints = [];
            MemoryPoints = [];
            return;
        }

        CaptureTimelineMoment[] errors = _snapshot.Moments
            .Where(moment => moment.Timestamp >= VisibleStart && moment.Timestamp <= VisibleEnd
                && moment.Severity >= CaptureEventSeverity.Error)
            .ToArray();
        CaptureNetworkSample[] network = _snapshot.NetworkSamples
            .Where(sample => sample.Timestamp >= VisibleStart && sample.Timestamp <= VisibleEnd
                && sample.PingMilliseconds is not null)
            .ToArray();
        CapturePerformanceSample[] performance = _snapshot.PerformanceSamples
            .Where(sample => sample.Timestamp >= VisibleStart && sample.Timestamp <= VisibleEnd)
            .ToArray();

        const int buckets = 80;
        double[] errorCounts = BucketValues(errors.Select(moment => (moment.Timestamp, 1d)), buckets, sum: true);
        double[] pingValues = BucketValues(network.Select(sample =>
            (sample.Timestamp, Math.Max(0, sample.PingMilliseconds!.Value))), buckets, sum: false);
        double[] frameValues = BucketValues(performance.Where(sample => sample.FrameMilliseconds is not null)
            .Select(sample => (sample.Timestamp, Math.Max(0, sample.FrameMilliseconds!.Value))), buckets, sum: false);
        double[] memoryValues = BucketValues(performance.Where(sample => sample.ManagedMemoryBytes is not null)
            .Select(sample => (sample.Timestamp, Math.Max(0, (double)sample.ManagedMemoryBytes!.Value))), buckets, sum: false);

        double errorMaximum = Math.Max(1, errorCounts.DefaultIfEmpty().Max());
        double pingMaximum = Math.Max(1, pingValues.DefaultIfEmpty().Max());
        double frameMaximum = Math.Max(1, frameValues.DefaultIfEmpty().Max());
        double memoryMaximum = Math.Max(1, memoryValues.DefaultIfEmpty().Max());
        ErrorActivityPoints = GraphPoints(errorCounts, errorMaximum);
        PingPoints = GraphPoints(pingValues, pingMaximum);
        FrameTimePoints = GraphPoints(frameValues, frameMaximum);
        MemoryPoints = GraphPoints(memoryValues, memoryMaximum);
        ErrorScaleText = errors.Length == 0 ? "0" : $"{errorMaximum:N0} errors / interval";
        PingScaleText = network.Length == 0 ? "0 ms" : $"{pingMaximum:N0} ms";
        FrameScaleText = frameValues.All(value => value <= 0) ? "0 ms" : $"{frameMaximum:N1} ms";
        MemoryScaleText = memoryValues.All(value => value <= 0)
            ? "0 B"
            : LogStreamingViewModel.FormatBytes((long)Math.Ceiling(memoryMaximum));
    }

    private double[] BucketValues(
        IEnumerable<(DateTimeOffset Time, double Value)> samples,
        int bucketCount,
        bool sum)
    {
        var values = new double[bucketCount];
        var counts = new int[bucketCount];
        double duration = Math.Max(0.001, (VisibleEnd - VisibleStart).TotalSeconds);
        foreach ((DateTimeOffset time, double value) in samples)
        {
            int bucket = Math.Clamp((int)(((time - VisibleStart).TotalSeconds / duration) * bucketCount), 0, bucketCount - 1);
            if (sum) values[bucket] += value;
            else
            {
                values[bucket] += value;
                counts[bucket]++;
            }
        }
        if (!sum)
        {
            for (int index = 0; index < values.Length; index++)
                if (counts[index] > 0) values[index] /= counts[index];
        }
        return values;
    }

    private static PointCollection GraphPoints(IReadOnlyList<double> values, double maximum)
    {
        if (values.Count == 0) return [];
        var points = new PointCollection(values.Count);
        for (int index = 0; index < values.Count; index++)
        {
            double x = values.Count == 1 ? 0 : GraphWidth * index / (values.Count - 1d);
            double y = GraphHeight * (1 - Math.Clamp(values[index] / maximum, 0, 1));
            points.Add(new(x, y));
        }
        points.Freeze();
        return points;
    }

    private void RefreshSummaryState()
    {
        OnPropertyChanged(nameof(CaptureName));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(ParticipantCountText));
        OnPropertyChanged(nameof(LogCoverageText));
        OnPropertyChanged(nameof(ErrorCountText));
        OnPropertyChanged(nameof(WarningCountText));
        OnPropertyChanged(nameof(CrossPlayerIncidentText));
        OnPropertyChanged(nameof(CaptureHealthText));
        OnPropertyChanged(nameof(CaptureHealthDetail));
        OnPropertyChanged(nameof(CanRefreshCapture));
        OnPropertyChanged(nameof(LiveFollowText));
        RefreshCaptureCommand.NotifyCanExecuteChanged();
        RefreshCollectionState();
    }

    private void RefreshCollectionState()
    {
        OnPropertyChanged(nameof(HasIncidents));
        OnPropertyChanged(nameof(HasNoIncidents));
        OnPropertyChanged(nameof(HasVisibleEvents));
        OnPropertyChanged(nameof(HasNoVisibleEvents));
        OnPropertyChanged(nameof(HasPossibleCauses));
        OnPropertyChanged(nameof(HasNoPossibleCauses));
        OnPropertyChanged(nameof(HasPlayersAtCursor));
        OnPropertyChanged(nameof(HasNoPlayersAtCursor));
        OnPropertyChanged(nameof(HasModComparison));
        OnPropertyChanged(nameof(HasNoModComparison));
        OnPropertyChanged(nameof(HasModMismatches));
        OnPropertyChanged(nameof(HasModVerificationGaps));
        OnPropertyChanged(nameof(ModMismatchSummary));
        OnPropertyChanged(nameof(ModVerificationSummary));
    }

    partial void OnCaptureFolderChanged(string value)
    {
        OnPropertyChanged(nameof(CaptureName));
        OnPropertyChanged(nameof(CanRefreshCapture));
        RefreshCaptureCommand.NotifyCanExecuteChanged();
    }

    partial void OnFollowCurrentCaptureChanged(bool value) => OnPropertyChanged(nameof(LiveFollowText));

    partial void OnFollowLiveEdgeChanged(bool value)
    {
        if (value && HasAnalysis) CursorTime = TimelineEnd;
    }

    private TimeSpan? Duration() => TimelineStart == default || TimelineEnd <= TimelineStart
        ? null
        : TimelineEnd - TimelineStart;

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1) return $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}";
        if (duration.TotalMinutes >= 1) return $"{(int)duration.TotalMinutes}:{duration.Seconds:00}";
        return $"{Math.Max(0, duration.TotalSeconds):N1} s";
    }

    private static DateTimeOffset Clamp(DateTimeOffset value, DateTimeOffset minimum, DateTimeOffset maximum) =>
        value < minimum ? minimum : value > maximum ? maximum : value;

    private static string TimingText(CaptureTimelineMoment moment)
    {
        string text = moment.TimingConfidence switch
        {
            CaptureTimingConfidence.Exact => "Exact sender time",
            CaptureTimingConfidence.Aligned => "Sender time aligned to the receiver clock",
            CaptureTimingConfidence.Arrival => "Receiver arrival time",
            _ => "Estimated time",
        };
        if (moment.DuplicateCount > 1) text += $". Combined from {moment.DuplicateCount:N0} matching log entries";
        return text + $". Source: {moment.SourceFile}";
    }

    private static async Task<string?> PickCaptureFolderAsync(string startingPath)
    {
        string? initialDirectory = await Task.Run(() =>
        {
            try
            {
                if (Directory.Exists(startingPath)) return startingPath;
                if (!string.IsNullOrWhiteSpace(startingPath)
                    && Path.GetDirectoryName(startingPath) is { } parent
                    && Directory.Exists(parent)) return parent;
            }
            catch (Exception) { }
            return null;
        });
        var dialog = new OpenFolderDialog
        {
            Title = "Load streamed-log capture",
            Multiselect = false,
        };
        if (initialDirectory is not null) dialog.InitialDirectory = initialDirectory;
        Window? owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive)
            ?? Application.Current?.MainWindow;
        bool? accepted = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        return accepted == true ? dialog.FolderName : null;
    }

    private DispatcherTimer CreatePlaybackTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        timer.Tick += (_, _) =>
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            TimeSpan elapsed = now - _lastPlaybackTick;
            _lastPlaybackTick = now;
            if (!IsPlaying || TimelineEnd <= TimelineStart) return;
            DateTimeOffset next = CursorTime + TimeSpan.FromTicks((long)(elapsed.Ticks * PlaybackRate));
            if (next >= TimelineEnd)
            {
                CursorTime = TimelineEnd;
                IsPlaying = false;
                return;
            }
            CursorTime = next;
            if (CursorTime >= VisibleEnd && VisibleEnd > VisibleStart)
                SetVisibleRange(CursorTime - TimeSpan.FromTicks((long)((VisibleEnd - VisibleStart).Ticks * 0.8)),
                    CursorTime + TimeSpan.FromTicks((long)((VisibleEnd - VisibleStart).Ticks * 0.2)));
        };
        return timer;
    }
}
