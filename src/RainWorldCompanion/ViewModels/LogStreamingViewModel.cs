using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RainWorldCompanion.ViewModels;

public sealed partial class LogStreamingPeerViewModel : ObservableObject
{
    public LogStreamingPeerViewModel(LogStreamingPeerUiState peer, bool selected, bool duplicateDisplayName = false)
    {
        Id = peer.Id;
        DisplayName = peer.DisplayName.Length == 0 ? "Unknown player" : peer.DisplayName;
        IsHost = peer.IsHost;
        IsLocal = peer.IsLocal;
        IsAvailableReceiver = peer.CanReceiveMyLogs;
        IsReceivingMyLogs = peer.IsReceivingMyLogs;
        ReceiverDeepTraceEnabled = peer.ReceiverDeepTraceEnabled;
        HasDuplicateDisplayName = duplicateDisplayName;
        Incoming = new(peer.Incoming, true);
        Outgoing = new(peer.Outgoing, false);
        isSelectedReceiver = selected && CanSelectAsReceiver;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string IdentityText => HasDuplicateDisplayName
        ? "Steam ID " + Id + " (duplicate display name)"
        : "Steam ID " + Id;
    public bool IsHost { get; }
    public bool IsLocal { get; }
    public bool IsAvailableReceiver { get; }
    public bool IsReceivingMyLogs { get; }
    public bool ReceiverDeepTraceEnabled { get; }
    public bool HasDuplicateDisplayName { get; }
    public bool CanSelectAsReceiver => IsAvailableReceiver || IsReceivingMyLogs;
    public LogStreamingDirectionViewModel Incoming { get; }
    public LogStreamingDirectionViewModel Outgoing { get; }
    public string RoleText => IsHost ? "HOST" : "CLIENT";
    public string LocalText => IsLocal ? "YOU" : "";
    public string ReceiverChoiceText => IsReceivingMyLogs
        ? ReceiverDeepTraceEnabled ? "Receiving your logs. Deep trace on." : "Receiving your logs. Deep trace off."
        : "Available to receive. Approval includes receiver-controlled Deep trace.";

    [ObservableProperty]
    private bool isSelectedReceiver;

}

public sealed class LogStreamingDirectionViewModel
{
    public LogStreamingDirectionViewModel(LogStreamingDirectionUiState direction, bool incoming)
    {
        State = direction.State;
        StatusText = StatusFor(direction.State, incoming);
        StatusTone = ToneFor(direction.State);
        DetailText = direction.Detail.Length == 0 ? DefaultDetail(direction.State, incoming) : direction.Detail;
        ThroughputBytesPerSecond = LogStreamingViewModel.NormalizeRate(direction.ThroughputBytesPerSecond);
        AcknowledgedBytes = Math.Max(0, direction.AcknowledgedBytes);
        BacklogBytes = Math.Max(0, direction.BacklogBytes);
        AcknowledgementAge = direction.AcknowledgementAge;
        ThroughputText = LogStreamingViewModel.FormatRate(ThroughputBytesPerSecond);
        AcknowledgedText = LogStreamingViewModel.FormatBytes(AcknowledgedBytes);
        BacklogText = incoming ? "Not reported" : LogStreamingViewModel.FormatBytes(BacklogBytes);
        AcknowledgementAgeLabel = incoming ? "LAST FLUSH" : "ACK AGE";
        AcknowledgementAgeText = direction.AcknowledgementAge is null
            ? incoming ? "No flush" : "No acknowledgement"
            : LogStreamingViewModel.FormatAge(direction.AcknowledgementAge.Value);
        ReconnectCountText = direction.ReconnectCount.ToString("N0", CultureInfo.CurrentCulture);
        LogSessionText = direction.LogSession.Length == 0 ? "None" : direction.LogSession;
    }

    public LogStreamingPeerState State { get; }
    public string StatusText { get; }
    public LogStreamingStatusTone StatusTone { get; }
    public string DetailText { get; }
    public double ThroughputBytesPerSecond { get; }
    public long AcknowledgedBytes { get; }
    public long BacklogBytes { get; }
    public TimeSpan? AcknowledgementAge { get; }
    public string ThroughputText { get; }
    public string AcknowledgedText { get; }
    public string BacklogText { get; }
    public string AcknowledgementAgeLabel { get; }
    public string AcknowledgementAgeText { get; }
    public string ReconnectCountText { get; }
    public string LogSessionText { get; }

    private static string StatusFor(LogStreamingPeerState state, bool incoming) => state switch
    {
        LogStreamingPeerState.Unsupported => "Unsupported",
        LogStreamingPeerState.NotSharing => incoming ? "Not sharing with you" : "Not receiving from you",
        LogStreamingPeerState.Ready => "Ready",
        LogStreamingPeerState.Streaming => "Streaming",
        LogStreamingPeerState.Reconnecting => "Reconnecting",
        LogStreamingPeerState.ReceiverPaused => incoming ? "Your capture is paused" : "Receiver paused",
        LogStreamingPeerState.StorageLimited => "Storage limit reached",
        _ => "Disconnected"
    };

    private static LogStreamingStatusTone ToneFor(LogStreamingPeerState state) => state switch
    {
        LogStreamingPeerState.Unsupported or LogStreamingPeerState.NotSharing => LogStreamingStatusTone.Danger,
        LogStreamingPeerState.Ready or LogStreamingPeerState.Streaming => LogStreamingStatusTone.Success,
        LogStreamingPeerState.Reconnecting or LogStreamingPeerState.ReceiverPaused or
            LogStreamingPeerState.StorageLimited => LogStreamingStatusTone.Warning,
        _ => LogStreamingStatusTone.Muted
    };

    private static string DefaultDetail(LogStreamingPeerState state, bool incoming) => state switch
    {
        LogStreamingPeerState.Unsupported => "This player needs a compatible Companion Game Hook.",
        LogStreamingPeerState.NotSharing => incoming
            ? "This player has not approved sharing logs with you."
            : "Your logs are not shared with this player.",
        LogStreamingPeerState.Ready => incoming
            ? "Approved and waiting for your capture to start."
            : "Approved and waiting for their capture to start.",
        LogStreamingPeerState.Streaming => incoming ? "Log data is arriving." : "Log data is being sent.",
        LogStreamingPeerState.Reconnecting => "The connection dropped briefly. Companion will retry.",
        LogStreamingPeerState.ReceiverPaused => incoming
            ? "Your capture is paused."
            : "This player paused their capture.",
        LogStreamingPeerState.StorageLimited => "Capture stopped before exceeding its storage limit.",
        _ => "This player is no longer connected to the lobby."
    };
}

public sealed record LogStreamingFilter(string Id, string Name);

public sealed record LogStreamingLineViewModel(
    long Sequence,
    DateTimeOffset Timestamp,
    string SenderId,
    string SenderName,
    string FileName,
    string Text)
{
    public string TimeText => Timestamp == default
        ? ""
        : Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.CurrentCulture);
}

public sealed partial class LogStreamingViewModel : ObservableObject
{
    public const int MaximumViewerRows = 2_000;
    private const int MaximumBufferedRows = 10_000;
    private const double ChartWidth = 600;
    private const double ChartHeight = 100;

    public static IReadOnlyList<string> SharedLogNames { get; } =
        ["consoleLog.txt", "exceptionLog.txt", "BepInEx/LogOutput.log"];

    public static IReadOnlyList<LogStreamingFilter> DiagnosticStreamFilters { get; } =
    [
        new("Companion/events.jsonl", "Diagnostic events"),
        new("Companion/deep-trace.jsonl", "Deep trace")
    ];

    public const string DisclosureText =
        "Streaming uses the current Steam Rain Meadow lobby connection and does not open an Internet-facing listener. " +
        "Sharing sends the complete current contents and new entries from the three game log files: consoleLog.txt, " +
        "exceptionLog.txt, and BepInEx/LogOutput.log. It also sends Companion's structured diagnostic event timeline. " +
        "That timeline includes active mod IDs, names, stated versions, and SHA-256 fingerprints of mod DLLs. " +
        "A fingerprint can identify an exact or private mod build. Mod paths, configurations, and file contents are not sent. " +
        "Approving a receiver lets that receiver switch Deep trace on or off later without another approval. " +
        "While enabled, Deep trace samples player positions, inputs, and game performance. It does not include chat " +
        "or arbitrary files. Switching Deep trace off leaves the three game logs and diagnostic events streaming. " +
        "The receiver's own logs and diagnostics are written locally into the same capture for comparison. " +
        "Other mods may write private information to the game log files. Only choose people you trust.";

    private readonly ILogStreamingController _controller;
    private readonly List<LogStreamingLineUiState> _lines = [];
    private readonly List<LogStreamingPeerUiState> _peerStates = [];

    public LogStreamingViewModel() : this(new EmptyLogStreamingController()) { }

    public LogStreamingViewModel(ILogStreamingController controller)
    {
        _controller = controller;
        FileFilters.Add(new("", "All streamed data"));
        foreach (string name in SharedLogNames) FileFilters.Add(new(name, name));
        foreach (var filter in DiagnosticStreamFilters) FileFilters.Add(filter);
        SenderFilters.Add(new("", "All players"));
    }

    public ObservableCollection<LogStreamingPeerViewModel> Peers { get; } = [];
    public ObservableCollection<LogStreamingPeerViewModel> ReceiverChoices { get; } = [];
    public ObservableCollection<LogStreamingFilter> SenderFilters { get; } = [];
    public ObservableCollection<LogStreamingFilter> FileFilters { get; } = [];
    public ObservableCollection<LogStreamingLineViewModel> VisibleLogLines { get; } = [];

    [ObservableProperty]
    private bool isSteamLobby;

    [ObservableProperty]
    private bool receiverAdvertised;

    [ObservableProperty]
    private bool deepTraceEnabled;

    [ObservableProperty]
    private LogStreamingCaptureState captureState;

    [ObservableProperty]
    private string captureFolder = "";

    [ObservableProperty]
    private string statusMessage = "Join a Steam Rain Meadow lobby to stream logs.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleReceiverAdvertisementCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartCaptureCommand))]
    [NotifyCanExecuteChangedFor(nameof(PauseCaptureCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCaptureCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleDeepTraceCommand))]
    [NotifyCanExecuteChangedFor(nameof(PrepareSharingCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevokeSelectedSharingCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevokeAllSharingCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenCaptureFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(MarkEventCommand))]
    private bool isBusy;

    [ObservableProperty]
    private bool autoScroll = true;

    [ObservableProperty]
    private string selectedSenderId = "";

    [ObservableProperty]
    private string selectedFileName = "";

    [ObservableProperty]
    private string searchText = "";

    [ObservableProperty]
    private string eventNote = "";

    [ObservableProperty]
    private PointCollection throughputPoints = [];

    [ObservableProperty]
    private PointCollection backlogPoints = [];

    [ObservableProperty]
    private PointCollection acknowledgementAgePoints = [];

    [ObservableProperty]
    private string throughputScaleText = "0 B/s";

    [ObservableProperty]
    private string backlogScaleText = "0 B";

    [ObservableProperty]
    private string acknowledgementAgeScaleText = "0 s";

    public bool HasPeers => Peers.Count > 0;
    public bool HasNoPeers => !HasPeers;
    public bool HasReceiverChoices => ReceiverChoices.Count > 0;
    public bool HasNoReceiverChoices => !HasReceiverChoices;
    public bool HasViewerLines => VisibleLogLines.Count > 0;
    public bool HasNoViewerLines => !HasViewerLines;
    public bool HasCaptureFolder => CaptureFolder.Length > 0;

    public string ReceiverAdvertisementActionText => ReceiverAdvertised
        ? "Stop advertising"
        : "Make me available to receive logs";

    public string ReceiverStatusText => ReceiverAdvertised
        ? "People in this lobby can ask to send their logs to you."
        : "You are not currently listed as a log receiver.";

    public string CaptureStatusText => CaptureState switch
    {
        LogStreamingCaptureState.Capturing => "Capture is running",
        LogStreamingCaptureState.Paused => "Capture is paused",
        _ => "Capture is stopped"
    };

    public string DeepTraceStatusText
    {
        get
        {
            if (!ReceiverAdvertised) return "Make yourself available as a receiver before enabling Deep trace.";
            return DeepTraceEnabled
                ? "On. Approved senders include live position, input, and performance samples."
                : "Off. Normal logs and diagnostic events continue streaming.";
        }
    }

    public string ViewerCountText => VisibleLogLines.Count == 1
        ? "1 visible line"
        : $"{VisibleLogLines.Count:N0} visible lines";

    public string ActiveStreamsText => Peers.Sum(peer =>
        (peer.Incoming.State == LogStreamingPeerState.Streaming ? 1 : 0) +
        (peer.Outgoing.State == LogStreamingPeerState.Streaming ? 1 : 0))
        .ToString("N0", CultureInfo.CurrentCulture);
    public string CurrentThroughputText => FormatRate(Peers.Sum(peer =>
        peer.Incoming.ThroughputBytesPerSecond + peer.Outgoing.ThroughputBytesPerSecond));
    public string CurrentBacklogText => FormatBytes(Peers.Sum(peer => peer.Outgoing.BacklogBytes));
    public string LongestAcknowledgementAgeText
    {
        get
        {
            var ages = Peers.Select(peer => peer.Outgoing.AcknowledgementAge)
                .Where(age => age is not null)
                .Select(age => age!.Value)
                .ToArray();
            return ages.Length == 0 ? "No acknowledgement" : FormatAge(ages.Max());
        }
    }

    public bool CanToggleReceiverAdvertisement => IsSteamLobby && !IsBusy;
    public bool CanStartCapture => IsSteamLobby && ReceiverAdvertised &&
        CaptureState != LogStreamingCaptureState.Capturing && !IsBusy;
    public bool CanPauseCapture => CaptureState == LogStreamingCaptureState.Capturing && !IsBusy;
    public bool CanStopCapture => CaptureState != LogStreamingCaptureState.Stopped && !IsBusy;
    public bool CanToggleDeepTrace => IsSteamLobby && ReceiverAdvertised && !IsBusy;
    public bool CanPrepareSharing => IsSteamLobby && !IsBusy &&
        ReceiverChoices.Any(peer => peer.IsAvailableReceiver && peer.IsSelectedReceiver);
    public bool CanRevokeSelectedSharing => !IsBusy &&
        ReceiverChoices.Any(peer => peer.IsReceivingMyLogs && peer.IsSelectedReceiver);
    public bool CanRevokeAllSharing => !IsBusy && Peers.Any(peer => peer.IsReceivingMyLogs);
    public bool CanOpenCaptureFolder => HasCaptureFolder && !IsBusy;
    public bool CanMarkEvent => HasCaptureFolder && CaptureState != LogStreamingCaptureState.Stopped && !IsBusy;

    partial void OnSelectedSenderIdChanged(string value) => RefreshViewer();
    partial void OnSelectedFileNameChanged(string value) => RefreshViewer();
    partial void OnSearchTextChanged(string value) => RefreshViewer();

    public void Refresh()
    {
        if (IsBusy) return;
        try
        {
            Adopt(_controller.Snapshot());
        }
        catch (Exception error)
        {
            StatusMessage = "Log streaming status is unavailable: " + error.Message;
        }
    }

    public void Adopt(LogStreamingUiState snapshot)
    {
        var selected = Peers.Where(peer => peer.IsSelectedReceiver)
            .Select(peer => peer.Id)
            .ToHashSet(StringComparer.Ordinal);

        IsSteamLobby = snapshot.IsSteamLobby;
        ReceiverAdvertised = snapshot.ReceiverAdvertised;
        DeepTraceEnabled = snapshot.DeepTraceEnabled;
        CaptureState = snapshot.CaptureState;
        CaptureFolder = snapshot.CaptureFolder;
        StatusMessage = snapshot.Message.Length > 0 ? snapshot.Message : DefaultStatus(snapshot);

        bool peersChanged = !_peerStates.SequenceEqual(snapshot.Peers);
        if (peersChanged)
        {
            var duplicateNames = snapshot.Peers
                .GroupBy(peer => peer.DisplayName.Trim(), StringComparer.CurrentCultureIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToHashSet(StringComparer.CurrentCultureIgnoreCase);
            _peerStates.Clear();
            _peerStates.AddRange(snapshot.Peers);
            Peers.Clear();
            ReceiverChoices.Clear();
            foreach (var peer in snapshot.Peers)
            {
                var row = new LogStreamingPeerViewModel(
                    peer, peer.IsReceivingMyLogs || selected.Contains(peer.Id),
                    duplicateNames.Contains(peer.DisplayName.Trim()));
                row.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(LogStreamingPeerViewModel.IsSelectedReceiver))
                    {
                        PrepareSharingCommand.NotifyCanExecuteChanged();
                        RevokeSelectedSharingCommand.NotifyCanExecuteChanged();
                    }
                };
                Peers.Add(row);
                if (peer.CanReceiveMyLogs || peer.IsReceivingMyLogs) ReceiverChoices.Add(row);
            }
        }

        bool linesChanged = ReplaceLines(snapshot.Lines);
        if (peersChanged || linesChanged) ReplaceSenderFilters(snapshot);
        UpdateCharts(snapshot.Samples, snapshot.ObservedAt);
        RefreshDerivedState();
    }

    [RelayCommand(CanExecute = nameof(CanToggleReceiverAdvertisement))]
    private Task ToggleReceiverAdvertisementAsync() => ApplyAsync(
        () => _controller.SetReceiverAvailabilityAsync(!ReceiverAdvertised));

    [RelayCommand(CanExecute = nameof(CanStartCapture))]
    private Task StartCaptureAsync() => ApplyAsync(
        () => _controller.SetCaptureStateAsync(LogStreamingCaptureState.Capturing));

    [RelayCommand(CanExecute = nameof(CanPauseCapture))]
    private Task PauseCaptureAsync() => ApplyAsync(
        () => _controller.SetCaptureStateAsync(LogStreamingCaptureState.Paused));

    [RelayCommand(CanExecute = nameof(CanStopCapture))]
    private Task StopCaptureAsync() => ApplyAsync(
        () => _controller.SetCaptureStateAsync(LogStreamingCaptureState.Stopped));

    [RelayCommand(CanExecute = nameof(CanToggleDeepTrace))]
    private Task ToggleDeepTraceAsync() => ApplyAsync(
        () => _controller.SetDeepTraceEnabledAsync(!DeepTraceEnabled));

    [RelayCommand(CanExecute = nameof(CanPrepareSharing))]
    private Task PrepareSharingAsync() => ApplyAsync(() => _controller.PrepareSharingAsync(
        ReceiverChoices.Where(peer => peer.IsAvailableReceiver && peer.IsSelectedReceiver)
            .Select(peer => peer.Id)
            .Order(StringComparer.Ordinal)
            .ToArray()));

    [RelayCommand(CanExecute = nameof(CanRevokeSelectedSharing))]
    private Task RevokeSelectedSharingAsync() => ApplyAsync(() => _controller.RevokeSharingAsync(
        ReceiverChoices.Where(peer => peer.IsReceivingMyLogs && peer.IsSelectedReceiver)
            .Select(peer => peer.Id)
            .Order(StringComparer.Ordinal)
            .ToArray()));

    [RelayCommand(CanExecute = nameof(CanRevokeAllSharing))]
    private Task RevokeAllSharingAsync() => ApplyAsync(_controller.RevokeAllSharingAsync);

    [RelayCommand(CanExecute = nameof(CanOpenCaptureFolder))]
    private Task OpenCaptureFolderAsync() => ApplyAsync(_controller.OpenCaptureFolderAsync);

    [RelayCommand(CanExecute = nameof(CanMarkEvent))]
    private async Task MarkEventAsync()
    {
        if (await ApplyAsync(() => _controller.MarkEventAsync(EventNote.Trim()))) EventNote = "";
    }

    private async Task<bool> ApplyAsync(Func<Task> action)
    {
        IsBusy = true;
        try
        {
            await action();
            Adopt(_controller.Snapshot());
            return true;
        }
        catch (Exception error)
        {
            StatusMessage = "Log streaming action failed: " + error.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(DeepTraceEnabled));
            RefreshCommands();
        }
    }

    private void ReplaceSenderFilters(LogStreamingUiState snapshot)
    {
        var names = snapshot.Peers
            .Select(peer => new LogStreamingFilter(peer.Id, peer.DisplayName))
            .Concat(snapshot.Lines.Select(line => new LogStreamingFilter(line.SenderId, line.SenderName)))
            .Where(filter => filter.Id.Length > 0)
            .GroupBy(filter => filter.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(filter => filter.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        SenderFilters.Clear();
        SenderFilters.Add(new("", "All players"));
        foreach (var filter in names) SenderFilters.Add(filter);
        if (!SenderFilters.Any(filter => filter.Id == SelectedSenderId)) SelectedSenderId = "";
    }

    private bool ReplaceLines(IReadOnlyList<LogStreamingLineUiState> lines)
    {
        int first = Math.Max(0, lines.Count - MaximumBufferedRows);
        int count = lines.Count - first;
        if (_lines.Count == count && _lines.SequenceEqual(lines.Skip(first))) return false;
        _lines.Clear();
        for (int index = first; index < lines.Count; index++) _lines.Add(lines[index]);
        RefreshViewer();
        return true;
    }

    private void RefreshViewer()
    {
        IEnumerable<LogStreamingLineUiState> query = _lines;
        if (SelectedSenderId.Length > 0)
            query = query.Where(line => string.Equals(line.SenderId, SelectedSenderId, StringComparison.Ordinal));
        if (SelectedFileName.Length > 0)
            query = query.Where(line => string.Equals(line.FileName, SelectedFileName, StringComparison.Ordinal));
        if (SearchText.Length > 0)
            query = query.Where(line => line.Text.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase));

        var rows = query.TakeLast(MaximumViewerRows)
            .Select(line => new LogStreamingLineViewModel(
                line.Sequence, line.Timestamp, line.SenderId, line.SenderName, line.FileName, line.Text))
            .ToArray();
        VisibleLogLines.Clear();
        foreach (var row in rows) VisibleLogLines.Add(row);
        OnPropertyChanged(nameof(HasViewerLines));
        OnPropertyChanged(nameof(HasNoViewerLines));
        OnPropertyChanged(nameof(ViewerCountText));
    }

    private void UpdateCharts(IReadOnlyList<LogStreamingChartUiSample> samples, DateTimeOffset now)
    {
        var recent = samples
            .Where(sample => sample.Time >= now.AddMinutes(-1) && sample.Time <= now)
            .OrderBy(sample => sample.Time)
            .ToArray();
        double throughputMaximum = Math.Max(1, recent.Select(sample => NormalizeRate(sample.ThroughputBytesPerSecond)).DefaultIfEmpty().Max());
        double backlogMaximum = Math.Max(1, recent.Select(sample => (double)sample.BacklogBytes).DefaultIfEmpty().Max());
        var ageSamples = recent.Where(sample => sample.AcknowledgementAgeSeconds is not null).ToArray();
        double ageMaximum = Math.Max(1, ageSamples.Select(sample => sample.AcknowledgementAgeSeconds!.Value).DefaultIfEmpty().Max());
        ThroughputPoints = ChartPoints(recent, now, throughputMaximum, sample => NormalizeRate(sample.ThroughputBytesPerSecond));
        BacklogPoints = ChartPoints(recent, now, backlogMaximum, sample => sample.BacklogBytes);
        AcknowledgementAgePoints = ChartPoints(ageSamples, now, ageMaximum,
            sample => sample.AcknowledgementAgeSeconds!.Value);
        ThroughputScaleText = recent.Length == 0 ? "0 B/s" : FormatRate(throughputMaximum);
        BacklogScaleText = recent.Length == 0 ? "0 B" : FormatBytes((long)Math.Ceiling(backlogMaximum));
        AcknowledgementAgeScaleText = ageSamples.Length == 0 ? "0 s" : FormatAge(TimeSpan.FromSeconds(ageMaximum));
    }

    private static PointCollection ChartPoints(
        IEnumerable<LogStreamingChartUiSample> samples,
        DateTimeOffset now,
        double maximum,
        Func<LogStreamingChartUiSample, double> value)
    {
        var points = new PointCollection(samples.Select(sample => new Point(
            ChartWidth * Math.Clamp(1 - (now - sample.Time).TotalSeconds / 60, 0, 1),
            ChartHeight * (1 - Math.Clamp(value(sample) / maximum, 0, 1)))));
        points.Freeze();
        return points;
    }

    private void RefreshDerivedState()
    {
        OnPropertyChanged(nameof(HasPeers));
        OnPropertyChanged(nameof(HasNoPeers));
        OnPropertyChanged(nameof(HasReceiverChoices));
        OnPropertyChanged(nameof(HasNoReceiverChoices));
        OnPropertyChanged(nameof(HasCaptureFolder));
        OnPropertyChanged(nameof(ReceiverAdvertisementActionText));
        OnPropertyChanged(nameof(ReceiverStatusText));
        OnPropertyChanged(nameof(CaptureStatusText));
        OnPropertyChanged(nameof(DeepTraceStatusText));
        OnPropertyChanged(nameof(ActiveStreamsText));
        OnPropertyChanged(nameof(CurrentThroughputText));
        OnPropertyChanged(nameof(CurrentBacklogText));
        OnPropertyChanged(nameof(LongestAcknowledgementAgeText));
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        ToggleReceiverAdvertisementCommand.NotifyCanExecuteChanged();
        StartCaptureCommand.NotifyCanExecuteChanged();
        PauseCaptureCommand.NotifyCanExecuteChanged();
        StopCaptureCommand.NotifyCanExecuteChanged();
        ToggleDeepTraceCommand.NotifyCanExecuteChanged();
        PrepareSharingCommand.NotifyCanExecuteChanged();
        RevokeSelectedSharingCommand.NotifyCanExecuteChanged();
        RevokeAllSharingCommand.NotifyCanExecuteChanged();
        OpenCaptureFolderCommand.NotifyCanExecuteChanged();
        MarkEventCommand.NotifyCanExecuteChanged();
    }

    private static string DefaultStatus(LogStreamingUiState snapshot)
    {
        if (!snapshot.IsSteamLobby) return "Join a Steam Rain Meadow lobby to stream logs.";
        if (snapshot.CaptureState == LogStreamingCaptureState.Capturing) return "Receiving approved logs from this lobby.";
        if (snapshot.ReceiverAdvertised) return "Available to receive logs from this lobby.";
        return "Log streaming is idle.";
    }

    internal static string FormatRate(double bytesPerSecond) =>
        FormatBytes((long)NormalizeRate(bytesPerSecond)) + "/s";

    internal static double NormalizeRate(double bytesPerSecond) =>
        double.IsFinite(bytesPerSecond) && bytesPerSecond > 0 ? bytesPerSecond : 0;

    internal static string FormatAge(TimeSpan age)
    {
        if (age.TotalMinutes >= 1) return $"{age.TotalMinutes:F1} min";
        return $"{Math.Max(0, age.TotalSeconds):F1} s";
    }

    internal static string FormatBytes(long bytes)
    {
        double value = Math.Max(0, bytes);
        string[] units = ["B", "KiB", "MiB", "GiB"];
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{value:N0} {units[unit]}"
            : $"{value:N1} {units[unit]}";
    }

    private sealed class EmptyLogStreamingController : ILogStreamingController
    {
        private static readonly LogStreamingUiState Empty = new();

        public LogStreamingUiState Snapshot() => Empty;

        public Task SetReceiverAvailabilityAsync(bool available) => Task.CompletedTask;
        public Task SetCaptureStateAsync(LogStreamingCaptureState state) => Task.CompletedTask;
        public Task SetDeepTraceEnabledAsync(bool enabled) => Task.CompletedTask;
        public Task PrepareSharingAsync(IReadOnlyList<string> receiverIds) => Task.CompletedTask;
        public Task RevokeSharingAsync(IReadOnlyList<string> receiverIds) => Task.CompletedTask;
        public Task RevokeAllSharingAsync() => Task.CompletedTask;
        public Task OpenCaptureFolderAsync() => Task.CompletedTask;
        public Task MarkEventAsync(string note) => Task.CompletedTask;
    }
}
