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
    private string? _selectedPlayerId;
    private LiveSnapshot? _snapshot;
    private HashSet<string> _visited = new(StringComparer.OrdinalIgnoreCase);
    [ObservableProperty] private bool spoilerMode = true;
    [ObservableProperty] private bool spoilerDetailView;
    partial void OnSpoilerDetailViewChanged(bool value) => Updated?.Invoke();
    public IReadOnlySet<string> VisitedRooms => _visited;
    public bool IsRoomVisible(string? roomId) => !SpoilerMode || roomId != null && _visited.Contains(roomId);
    public string SpoilerStatus => !SpoilerMode ? "All rooms visible."
        : _snapshot?.HasExplorationData == true ? $"Spoiler mode: {_visited.Count} visited rooms from this campaign."
        : "Spoiler mode: waiting for campaign exploration data. Requires rwcompanion 1.0.6 or newer.";

    partial void OnSpoilerModeChanged(bool value) => Adopt(_snapshot);
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
        : FollowedPlayer is { } player ? $"Following {player.Name}" + (player.Placement is null ? ": waiting for a mapped room" : "")
        : "Following player: waiting for them to load.";
    public LiveMapPlayer? FollowedPlayer => Players.FirstOrDefault(p => p.Id == FollowedPlayerId);
    public event Action? Updated;

    public void Adopt(LiveSnapshot? snapshot)
    {
        _snapshot = snapshot;
        _visited = new(snapshot is { HasExplorationData: true } ? snapshot.VisitedRooms ?? [] : [], StringComparer.OrdinalIgnoreCase);
        if (SelectedRoom != null && !IsRoomVisible(SelectedRoom.RoomId)) SelectedRoom = null;
        if (snapshot is not null && _sessionId != snapshot.SessionId)
        {
            StopFollowing();
            SelectedPlayer = null;
            _selectedPlayerId = null;
        }
        if (snapshot is not null) _sessionId = snapshot.SessionId;
        ReportedTimeline = snapshot is null ? "Unknown" : DenWorldCatalog.EffectiveTimeline(snapshot.Campaign, snapshot.Timeline);
        AutomaticMap = snapshot is not null && !string.IsNullOrWhiteSpace(ReportedTimeline);
        if (AutomaticMap)
            Map = DenMapCatalog.ForTimeline(ReportedTimeline, snapshot!.EnabledExpansions.Contains("moreslugcats", StringComparer.OrdinalIgnoreCase));
        var rows = snapshot?.Players.Select(p => new LiveMapPlayer(p.Id, p.Name,
            string.IsNullOrWhiteSpace(p.RoomId) ? "Location unavailable" : !IsRoomVisible(p.RoomId) ? "Unexplored room" : p.RoomId,
            !IsRoomVisible(p.RoomId) ? "Unknown" : p.Region ?? "Unknown", p.Dead switch { true => "Dead", false => "Alive", null => "Unknown" }, p.IsLocal,
            Map is null || string.IsNullOrWhiteSpace(p.RoomId) || !IsRoomVisible(p.RoomId) ? null : RoomMapCatalog.Find(Map.Id, p.RoomId),
            !IsRoomVisible(p.RoomId) ? "Hidden by spoiler mode" : Map is null ? null : RoomMapCatalog.UnavailableReason(Map.Id, p.RoomId),
            snapshot.IsOnline, p.CompanionVersion, p.AllowsHostControl, p.IsHost)).ToArray() ?? [];
        if (!Players.SequenceEqual(rows)) Players = rows;
        SelectedPlayer = Players.FirstOrDefault(p => p.Id == _selectedPlayerId);
        UpdateSearch();
        UpdateCoverage();
        OnPropertyChanged(nameof(SpoilerStatus));
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

    partial void OnSelectedPlayerChanged(LiveMapPlayer? value)
    {
        if (value is not null) _selectedPlayerId = value.Id;
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
            .Where(r => IsRoomVisible(r.RoomId) && r.RoomId.Contains(Query.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.RoomId).Take(80).ToArray();
    }

    private void UpdateCoverage()
    {
        CoverageText = Map is null ? "No bundled map for this timeline. Player room names remain available."
            : $"{RoomMapCatalog.ForMap(Map.Id).Count(r => IsRoomVisible(r.RoomId)):N0} placed rooms · {Players.Count(p => p.Placement is not null)}/{Players.Count} players located. Unmatched rooms remain listed.";
    }
}
