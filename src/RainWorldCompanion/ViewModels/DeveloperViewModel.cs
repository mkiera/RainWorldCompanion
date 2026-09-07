using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using RainWorldCompanion.Core.Live;
using RainWorldCompanion.LiveProtocol;

namespace RainWorldCompanion.ViewModels;

public sealed partial class DiagnosticValue(string name) : ObservableObject
{
    public string Name { get; } = name;
    [ObservableProperty] private string value = "";
}

public sealed record DiagnosticCategory(string Name, ObservableCollection<DiagnosticValue> Values);

public sealed partial class DeveloperViewModel : ObservableObject
{
    public LiveMapViewModel MapView { get; }
    public DeveloperViewModel(LiveMapViewModel? mapView = null) => MapView = mapView ?? new();

    private readonly Queue<(DateTimeOffset Time, double Rate, double Age)> _history = new();
    private DateTimeOffset? _previousTime;
    private long _previousAccepted;
    private double _currentRate;
    [ObservableProperty] private PointCollection ratePoints = new();
    [ObservableProperty] private PointCollection agePoints = new();
    [ObservableProperty] private string rateText = "0.0 snapshots/s";
    [ObservableProperty] private string ageText = "No snapshot received";
    [ObservableProperty] private string rateScale = "5/s";
    [ObservableProperty] private string ageScale = "3 s";
    [ObservableProperty] private string statusText = "Waiting";
    [ObservableProperty] private string acceptedText = "0";
    [ObservableProperty] private string rejectedText = "0";
    [ObservableProperty] private string playersText = "0";
    public ObservableCollection<DiagnosticCategory> Categories { get; } = [];
    [ObservableProperty] private string snapshotJson = "No current gameplay snapshot.";

    public void Refresh(LiveDiagnostics? diagnostics, LiveSessionViewModel live,
        IReadOnlyDictionary<string, string> application, DateTimeOffset now)
    {
        UpdateCharts(diagnostics, now);
        SetCategory("Companion and mod setup", application.Concat(new Dictionary<string, string>
        {
            ["Installed"] = live.Installed.ToString(), ["Setup ready"] = live.SetupReady.ToString(),
            ["Installed mod version"] = live.InstalledVersion, ["Setup status"] = live.SetupText,
            ["Operation"] = live.OperationText, ["Working"] = live.IsWorking.ToString()
        }));
        SetCategory("Connection", new Dictionary<string, string>
        {
            ["Status"] = diagnostics?.Status.ToString() ?? "Listener unavailable",
            ["Endpoint"] = diagnostics?.Port is { } port ? $"127.0.0.1:{port}" : "Not listening",
            ["Discovery directory"] = diagnostics?.DiscoveryDirectory ?? ProtocolInfo.DiscoveryDirectory,
            ["Expected protocol"] = ProtocolInfo.Version.ToString(),
            ["Last authenticated protocol"] = diagnostics?.ObservedProtocol?.ToString() ?? "Unknown",
            ["Last authenticated mod version"] = diagnostics?.ObservedModVersion ?? "Unknown",
            ["TCP connections"] = diagnostics?.Connections.ToString() ?? "0",
            ["Received messages"] = diagnostics?.Messages.ToString() ?? "0",
            ["Accepted snapshots"] = diagnostics?.Accepted.ToString() ?? "0",
            ["Rejected reads or messages"] = diagnostics?.Rejected.ToString() ?? "0",
            ["Ignored sequences"] = diagnostics?.Ignored.ToString() ?? "0",
            ["Received bytes"] = diagnostics?.Bytes.ToString() ?? "0",
            ["Last received (UTC)"] = diagnostics?.LastReceived?.ToString("O") ?? "Never",
            ["Last accepted (UTC)"] = diagnostics?.LastAccepted?.ToString("O") ?? "Never",
            ["Accepted snapshot age"] = diagnostics?.LastAccepted is { } accepted ? $"{Math.Max(0, (now - accepted).TotalSeconds):F1} s" : "Unknown",
            ["Stale timeout"] = "3 seconds", ["Maximum sampling rate"] = "5 Hz",
            ["Last event"] = diagnostics?.LastEvent ?? "Listener unavailable"
        });
        var snapshot = diagnostics?.Status == LiveConnectionStatus.Connected ? diagnostics.Snapshot : null;
        SetCategory("Live map", new Dictionary<string, string>
        {
            ["Reported timeline"] = live.MapView.ReportedTimeline,
            ["Map variant"] = live.MapView.Map?.Id ?? "Not on a bundled map",
            ["Automatic map selection"] = live.MapView.AutomaticMap.ToString(),
            ["Room coverage"] = live.MapView.CoverageText,
            ["Selected room"] = live.MapView.SelectedRoom?.RoomId ?? "None",
            ["Followed player ID"] = live.MapView.FollowedPlayerId ?? "None",
            ["Follow state"] = live.MapView.FollowStatus,
            ["Player placements"] = string.Join("\n", live.MapView.Players.Select(p => $"{p.Id}: {p.RoomId}, {p.MapStatus}"))
        });
        SetCategory("Game session", new Dictionary<string, string>
        {
            ["Session ID"] = snapshot?.SessionId ?? "Unavailable", ["Sequence"] = snapshot?.Sequence.ToString() ?? "Unavailable",
            ["State"] = snapshot?.State ?? "Not connected", ["Game version"] = snapshot?.GameVersion ?? "Unknown",
            ["Reported game installation"] = snapshot?.GameInstallPath ?? "Unknown",
            ["Campaign"] = snapshot?.Campaign ?? "Unknown", ["Timeline"] = snapshot?.Timeline ?? "Unknown",
            ["Enabled expansions"] = snapshot is null ? "Unknown" : string.Join(", ", snapshot.EnabledExpansions),
            ["Player count"] = snapshot?.Players.Length.ToString() ?? "0"
        });
        var players = new Dictionary<string, string>();
        foreach (var player in snapshot?.Players ?? [])
        {
            var prefix = $"[{players.Count / 7 + 1}] ";
            players[prefix + "Identity"] = player.Id;
            players[prefix + "Name"] = player.Name;
            players[prefix + "Source"] = player.IsLocal ? "Local" : "Online";
            players[prefix + "Room"] = player.RoomId ?? "Location unavailable";
            players[prefix + "Region"] = player.Region ?? "Unknown";
            players[prefix + "Alive/dead"] = player.Dead switch { true => "Dead", false => "Alive", null => "Unknown" };
            players[prefix + "Is local"] = player.IsLocal.ToString();
        }
        if (players.Count == 0) players["Players"] = "No current player data";
        SetCategory("Players", players);
        SnapshotJson = snapshot is null ? "No current gameplay snapshot." : JsonSerializer.Serialize(new
        {
            snapshot.SessionId, snapshot.Sequence, snapshot.ProtocolVersion, snapshot.ModVersion,
            snapshot.GameVersion, snapshot.GameInstallPath, snapshot.Campaign, snapshot.Timeline,
            snapshot.State, snapshot.EnabledExpansions, snapshot.Players
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private void UpdateCharts(LiveDiagnostics? diagnostics, DateTimeOffset now)
    {
        var accepted = diagnostics?.Accepted ?? 0;
        var elapsed = _previousTime is { } previous ? (now - previous).TotalSeconds : 0;
        if (_previousTime is null || elapsed >= 1)
        {
            _currentRate = elapsed > 0 ? Math.Max(0, accepted - _previousAccepted) / elapsed : 0;
            _previousTime = now;
            _previousAccepted = accepted;
        }
        var rate = _currentRate;
        var age = diagnostics?.LastAccepted is { } received ? Math.Max(0, (now - received).TotalSeconds) : 0;
        _history.Enqueue((now, rate, age));
        while (_history.Count > 1 && now - _history.Peek().Time > TimeSpan.FromSeconds(30)) _history.Dequeue();
        var maxRate = Math.Max(5, Math.Ceiling(_history.Max(p => p.Rate)));
        var maxAge = Math.Max(3, Math.Ceiling(_history.Max(p => p.Age)));
        RatePoints = ChartPoints(now, maxRate, p => p.Rate);
        AgePoints = diagnostics?.LastAccepted is null ? new PointCollection() : ChartPoints(now, maxAge, p => p.Age);
        RateText = $"{rate:F1} snapshots/s";
        AgeText = diagnostics?.LastAccepted is null ? "No snapshot received" : $"{age:F1} s since accepted snapshot";
        RateScale = $"{maxRate:F0}/s";
        AgeScale = $"{maxAge:F0} s";
        StatusText = diagnostics?.Status.ToString() ?? "Listener unavailable";
        AcceptedText = accepted.ToString("N0");
        RejectedText = (diagnostics?.Rejected ?? 0).ToString("N0");
        PlayersText = (diagnostics?.Status == LiveConnectionStatus.Connected ? diagnostics.Snapshot?.Players.Length ?? 0 : 0).ToString();
    }

    private PointCollection ChartPoints(DateTimeOffset now, double maximum,
        Func<(DateTimeOffset Time, double Rate, double Age), double> value)
    {
        var points = new PointCollection(_history.Select(p => new Point(
            480 * Math.Clamp(1 - (now - p.Time).TotalSeconds / 30, 0, 1),
            100 * (1 - Math.Clamp(value(p) / maximum, 0, 1)))));
        points.Freeze();
        return points;
    }

    private void SetCategory(string name, IEnumerable<KeyValuePair<string, string>> values)
    {
        var category = Categories.FirstOrDefault(item => item.Name == name);
        if (category is null) { category = new(name, []); Categories.Add(category); }
        var entries = values.ToArray();
        if (!category.Values.Select(item => item.Name).SequenceEqual(entries.Select(item => item.Key)))
        {
            category.Values.Clear();
            foreach (var entry in entries) category.Values.Add(new(entry.Key));
        }
        for (int index = 0; index < entries.Length; index++) category.Values[index].Value = entries[index].Value;
    }
}
