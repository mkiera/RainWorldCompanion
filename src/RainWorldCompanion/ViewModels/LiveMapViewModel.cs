using CommunityToolkit.Mvvm.ComponentModel;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.LiveProtocol;

namespace RainWorldCompanion.ViewModels;

public sealed record LiveMapPlayer(string Id, string Name, string RoomId, string Region, string State,
    bool IsLocal, MappedRoom? Placement, string? UnavailableReason = null,
    bool IsMeadow = false, string? CompanionVersion = null, bool AllowsHostControl = false, bool IsHost = false)
{
    public string ControlLight => string.IsNullOrEmpty(CompanionVersion) ? "#E05C64" : AllowsHostControl ? "#58C785" : "#E8BD52";
    public string ControlStatus => string.IsNullOrEmpty(CompanionVersion) ? "No compatible rwcompanion mod detected"
        : $"rwcompanion {CompanionVersion}: host control {(AllowsHostControl ? "on" : "off")}";
    public string Source => IsLocal ? "Local" : "Online";
    public string MapStatus => RoomId == "Location unavailable" ? RoomId
        : Placement is null ? UnavailableReason is null ? "Not on this map" : "Unavailable on this map"
        : Placement.Bounds.Count == 0 ? "Den anchor only"
        : Placement.MatchKind.StartsWith("terrain-template", StringComparison.Ordinal) ? "Terrain matched"
        : Placement.MatchKind.StartsWith("reference-affine", StringComparison.Ordinal) ? "Approximate placement" : Placement.MatchKind;
}

public sealed partial class LiveMapViewModel : ObservableObject
{
    private string? _sessionId;
    [ObservableProperty] private DenMapDefinition? map = DenMapCatalog.Downpour;
    [ObservableProperty] private bool automaticMap;
    [ObservableProperty] private string reportedTimeline = "Unknown";
    [ObservableProperty] private IReadOnlyList<LiveMapPlayer> players = [];
    [ObservableProperty] private LiveMapPlayer? selectedPlayer;
    [ObservableProperty] private string query = "";
    [ObservableProperty] private IReadOnlyList<MappedRoom> searchResults = [];
    [ObservableProperty] private MappedRoom? selectedRoom;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFollowing))]
    [NotifyPropertyChangedFor(nameof(FollowStatus))]
    private string? followedPlayerId;
    [ObservableProperty] private string coverageText = "";
    public IReadOnlyList<DenMapDefinition> Maps => DenMapCatalog.Maps;
    public bool IsFollowing => FollowedPlayerId is not null;
    public string FollowStatus => FollowedPlayerId is null ? "Double-click a player to follow. Drag or Fit world to stop."
        : FollowedPlayer is { } player ? $"Following {player.Name}" + (player.Placement is null ? ": waiting for a mapped room" : "") : "";
    public LiveMapPlayer? FollowedPlayer => Players.FirstOrDefault(p => p.Id == FollowedPlayerId);
    public event Action? Updated;

    public void Adopt(LiveSnapshot? snapshot)
    {
        if (_sessionId != snapshot?.SessionId) { StopFollowing(); SelectedPlayer = null; }
        _sessionId = snapshot?.SessionId;
        var selectedId = SelectedPlayer?.Id;
        ReportedTimeline = snapshot is null ? "Unknown" : DenWorldCatalog.EffectiveTimeline(snapshot.Campaign, snapshot.Timeline);
        AutomaticMap = snapshot is not null && !string.IsNullOrWhiteSpace(ReportedTimeline);
        if (AutomaticMap)
            Map = DenMapCatalog.ForTimeline(ReportedTimeline, snapshot!.EnabledExpansions.Contains("moreslugcats", StringComparer.OrdinalIgnoreCase));
        var rows = snapshot?.Players.Select(p => new LiveMapPlayer(p.Id, p.Name,
            string.IsNullOrWhiteSpace(p.RoomId) ? "Location unavailable" : p.RoomId,
            p.Region ?? "Unknown", p.Dead switch { true => "Dead", false => "Alive", null => "Unknown" }, p.IsLocal,
            Map is null || string.IsNullOrWhiteSpace(p.RoomId) ? null : RoomMapCatalog.Find(Map.Id, p.RoomId),
            Map is null ? null : RoomMapCatalog.UnavailableReason(Map.Id, p.RoomId),
            snapshot.IsOnline, p.CompanionVersion, p.AllowsHostControl, p.IsHost)).ToArray() ?? [];
        if (!Players.SequenceEqual(rows)) Players = rows;
        SelectedPlayer = Players.FirstOrDefault(p => p.Id == selectedId);
        if (FollowedPlayerId is not null && !Players.Any(p => p.Id == FollowedPlayerId)) StopFollowing();
        UpdateCoverage();
        OnPropertyChanged(nameof(FollowStatus));
        Updated?.Invoke();
    }

    public void FollowPlayer(string id)
    {
        SelectedPlayer = Players.FirstOrDefault(p => p.Id == id);
        if (SelectedPlayer is null) return;
        FollowedPlayerId = id;
        Updated?.Invoke();
    }

    public void StopFollowing()
    {
        if (FollowedPlayerId is null) return;
        FollowedPlayerId = null;
        Updated?.Invoke();
    }

    public void Browse(DenMapDefinition selected)
    {
        if (AutomaticMap) return;
        StopFollowing();
        Map = selected;
        Updated?.Invoke();
    }

    partial void OnMapChanged(DenMapDefinition? value)
    {
        SelectedRoom = null;
        UpdateSearch();
        UpdateCoverage();
    }

    partial void OnQueryChanged(string value) => UpdateSearch();

    private void UpdateSearch()
    {
        SearchResults = Map is null || string.IsNullOrWhiteSpace(Query) ? [] : RoomMapCatalog.ForMap(Map.Id)
            .Where(r => r.RoomId.Contains(Query.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.RoomId).Take(80).ToArray();
    }

    private void UpdateCoverage()
    {
        CoverageText = Map is null ? "No bundled map for this timeline. Player room names remain available."
            : $"{RoomMapCatalog.ForMap(Map.Id).Count:N0} placed rooms · {Players.Count(p => p.Placement is not null)}/{Players.Count} players located. Unmatched rooms remain listed.";
    }
}
