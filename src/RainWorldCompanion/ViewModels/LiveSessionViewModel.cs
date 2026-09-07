using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RainWorldCompanion.Core.Live;
using RainWorldCompanion.LiveProtocol;
using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.ViewModels;

public sealed record LivePlayerRow(string Id, string Name, string Location, string Region, string State, string Source);

public sealed partial class LiveSessionViewModel : ObservableObject
{
    private readonly Func<Task> _install;
    private readonly Func<string, string, string, string, Task<LiveCommandResult>>? _teleport;
    private readonly Func<bool, Task<LiveCommandResult>>? _setHostControl;
    private LiveSnapshot? _snapshot;
    private readonly Func<string, string, string, Task<LiveCommandResult>>? _teleportAll;

    public LiveSessionViewModel(Func<Task>? install = null,
        Func<string, string, string, string, Task<LiveCommandResult>>? teleport = null,
        Func<bool, Task<LiveCommandResult>>? setHostControl = null,
        Func<string, string, string, Task<LiveCommandResult>>? teleportAll = null)
    {
        _install = install ?? (() => Task.CompletedTask);
        _teleport = teleport;
        _setHostControl = setHostControl;
        _teleportAll = teleportAll;
        MapView.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(MapView.SelectedRoom) or nameof(MapView.SpoilerMode)) RefreshGroupAction(); };
    }

    [ObservableProperty] private bool isMapActionRunning;
    [ObservableProperty] private string mapActionText = "Right-click a room to teleport.";
    [ObservableProperty] private bool allowHostControl;
    [ObservableProperty] private bool canSetHostControl;

    public LiveMapPlayer? TeleportPlayer => MapView.SelectedPlayer is { IsLocal: true } selected
        ? selected : MapView.Players.FirstOrDefault(p => p.IsLocal);
    public string? CurrentGameplayId => _snapshot?.GameplayId;
    public bool IsMeadowHost => _snapshot?.IsHost == true;
    [ObservableProperty] private string hostActionText = "";

    public bool IsMeadow => _snapshot?.IsOnline == true;
    public string TeleportAllReason => TeleportAllUnavailableReason(MapView.SelectedRoom) ?? "Move everyone to " + MapView.SelectedRoom!.RoomId + ". Cross-region travel is supported.";
    public bool CanTeleportAll => TeleportAllUnavailableReason(MapView.SelectedRoom) == null;

    public string? TeleportAllUnavailableReason(MappedRoom? room)
    {
        if (IsMapActionRunning) return "Wait for the current map action to finish.";
        if (_snapshot is not { IsOnline: true, IsHost: true }) return "Only the Meadow host can teleport everyone.";
        if (_teleportAll == null || !_snapshot.SupportsTeleportAll) return "Update rwcompanion to 1.0.5 or newer.";
        if (!_snapshot.AllowHostControl) return "You: Allow host control is off.";
        if (!string.IsNullOrEmpty(_snapshot.TeleportAllUnavailableReason)) return _snapshot.TeleportAllUnavailableReason;
        if (_snapshot.State is not ("gameplay" or "paused")) return "Enter campaign gameplay.";
        if (room == null) return "Select a destination room.";
        if (!MapView.IsRoomVisible(room.RoomId)) return "This room is hidden by spoiler mode.";
        if (MapView.Map == null || RoomMapCatalog.Find(MapView.Map.Id, room.RoomId) != room) return "Choose a room on the current campaign map.";
        return null;
    }

    private void RefreshGroupAction()
    {
        OnPropertyChanged(nameof(IsMeadow));
        OnPropertyChanged(nameof(IsMeadowHost));
        OnPropertyChanged(nameof(TeleportAllReason));
        OnPropertyChanged(nameof(CanTeleportAll));
        TeleportAllCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsMapActionRunningChanged(bool value) => RefreshGroupAction();

    [RelayCommand(CanExecute = nameof(CanTeleportAll))]
    private Task TeleportAll() => MapView.SelectedRoom is { } room ? TeleportAllHereAsync(room, CurrentGameplayId) : Task.CompletedTask;

    public async Task TeleportAllHereAsync(MappedRoom room, string? gameplayId)
    {
        if (gameplayId != CurrentGameplayId) { MapActionText = "Gameplay changed. Select the room again."; return; }
        if (TeleportAllUnavailableReason(room) is { } reason) { MapActionText = reason; return; }
        IsMapActionRunning = true;
        MapActionText = "Preparing everyone for " + room.RoomId + "...";
        try { MapActionText = (await _teleportAll!(_snapshot!.GameplayId, room.RoomId, room.RegionCode)).Message; }
        catch (Exception error) { MapActionText = "Teleport all failed: " + error.Message; }
        finally { IsMapActionRunning = false; }
    }

    public string? HostTeleportUnavailableReason(MappedRoom room, string playerId)
    {
        if (IsMapActionRunning) return "Wait for the current map action to finish.";
        if (_teleport == null || _snapshot is not { IsHost: true, CommandVersion: 1, State: "gameplay" or "paused" }) return "Only the current host can request a teleport.";
        if (!MapView.IsRoomVisible(room.RoomId)) return "This room is hidden by spoiler mode.";
        if (MapView.Map == null || RoomMapCatalog.Find(MapView.Map.Id, room.RoomId) != room) return "Choose a room on the current campaign map.";
        var player = _snapshot.Players.FirstOrDefault(p => p.Id == playerId && !p.IsLocal);
        if (player == null) return "The player has left the session.";
        if (string.IsNullOrEmpty(player.CompanionVersion)) return "This player needs rwcompanion 1.0.4 or newer.";
        if (!player.AllowsHostControl) return "This player has Allow host control turned off or is not in gameplay.";
        if (player.Dead != false) return "The player must be alive.";
        if (!string.Equals(player.Region, room.RegionCode, StringComparison.OrdinalIgnoreCase)) return "Choose a room in this player's loaded region.";
        return null;
    }

    public async Task TeleportPlayerHereAsync(MappedRoom room, string playerId, string? gameplayId)
    {
        if (gameplayId != CurrentGameplayId) { MapActionText = "Gameplay changed. Right-click the room again."; return; }
        if (HostTeleportUnavailableReason(room, playerId) is { } reason) { MapActionText = reason; return; }
        IsMapActionRunning = true;
        MapActionText = "Waiting for the player's mod to teleport to " + room.RoomId + "...";
        try { MapActionText = (await _teleport!(_snapshot!.GameplayId, playerId, room.RoomId, room.RegionCode)).Message; }
        catch (Exception error) { MapActionText = "Host teleport failed: " + error.Message; }
        finally { IsMapActionRunning = false; }
    }

    public string? TeleportUnavailableReason(MappedRoom room)
    {
        if (IsMapActionRunning) return "Wait for the current map action to finish.";
        if (_teleport == null || _snapshot is not { CommandVersion: 1 }) return "Connect an updated rwcompanion mod to teleport.";
        if (_snapshot.State is not ("gameplay" or "paused")) return "Enter a campaign to teleport.";
        if (!MapView.IsRoomVisible(room.RoomId)) return "This room is hidden by spoiler mode.";
        if (MapView.Map == null || RoomMapCatalog.Find(MapView.Map.Id, room.RoomId) != room) return "Choose a room on the current campaign map.";
        if (TeleportPlayer is not { State: "Alive" } player) return "A living local player is required.";
        if (_snapshot.IsOnline && !string.Equals(player.Region, room.RegionCode, StringComparison.OrdinalIgnoreCase))
            return "Per-player teleports stay within the loaded region. The host can use Teleport all to change regions.";
        return null;
    }

    public async Task TeleportHereAsync(MappedRoom room, string? gameplayId = null)
    {
        if (gameplayId != null && gameplayId != CurrentGameplayId) { MapActionText = "Gameplay changed. Right-click the room again."; return; }
        if (TeleportUnavailableReason(room) is { } reason) { MapActionText = reason; return; }
        var player = TeleportPlayer!;
        IsMapActionRunning = true;
        MapActionText = $"Teleporting {player.Name} to {room.RoomId}...";
        try { MapActionText = (await _teleport!(_snapshot!.GameplayId, player.Id, room.RoomId, room.RegionCode)).Message; }
        catch (Exception exception) { MapActionText = "Teleport failed: " + exception.Message; }
        finally { IsMapActionRunning = false; }
    }

    public async Task SetHostControlAsync(bool enabled)
    {
        if (!CanSetHostControl || IsMapActionRunning || _setHostControl == null) return;
        IsMapActionRunning = true;
        try
        {
            var result = await _setHostControl(enabled);
            if (result.Success) AllowHostControl = enabled;
            MapActionText = result.Message;
        }
        catch (Exception exception) { MapActionText = "Could not change host control: " + exception.Message; }
        finally { IsMapActionRunning = false; }
    }

    public LiveMapViewModel MapView { get; } = new();

    public ObservableCollection<LivePlayerRow> Players { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IndicatorText))]
    private bool installed;

    [ObservableProperty]
    private bool setupReady;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IndicatorText))]
    private string installedVersion = "";

    [ObservableProperty]
    private string setupText = "rwcompanion is not installed.";

    [ObservableProperty]
    private string connectionText = "Waiting for the game connection.";

    [ObservableProperty]
    private string runningVersion = "Not connected";

    [ObservableProperty]
    private string gameVersion = "Unknown";

    [ObservableProperty]
    private string campaign = "Waiting for gameplay";

    [ObservableProperty]
    private string timeline = "Unknown";

    [ObservableProperty]
    private string gameState = "Not connected";

    [ObservableProperty]
    private string expansions = "Unknown";

    [ObservableProperty]
    private string operationText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private bool isWorking;

    [ObservableProperty]
    private string installLabel = "Install and enable rwcompanion";

    public string IndicatorText => Installed ? $"rwcompanion {InstalledVersion}" : "rwcompanion: not installed";

    public void AdoptSetup(bool exists, bool ready, string? version, string description)
    {
        Installed = exists;
        SetupReady = ready;
        InstalledVersion = version ?? "unknown version";
        SetupText = description;
        InstallLabel = ready ? "Repair rwcompanion" : exists ? "Enable or repair rwcompanion" : "Install and enable rwcompanion";
    }

    public void AdoptConnection(LiveConnectionStatus status, LiveSnapshot? snapshot, bool gameRunning)
    {
        ConnectionText = status switch
        {
            LiveConnectionStatus.Connected => "Connected",
            LiveConnectionStatus.Incompatible => "The running mod uses an incompatible live protocol.",
            _ => gameRunning ? "Waiting for connection" : SetupReady ? "Ready, game closed" : "Not connected",
        };
        bool current = status == LiveConnectionStatus.Connected && snapshot is not null;
        _snapshot = current ? snapshot : null;
        HostActionText = _snapshot?.HostActionText ?? "";
        CanSetHostControl = current && snapshot!.CommandVersion == 1 && _setHostControl != null;
        AllowHostControl = current && snapshot!.AllowHostControl;
        MapView.Adopt(current ? snapshot : null);
        RefreshGroupAction();
        RunningVersion = current ? snapshot!.ModVersion : "Not connected";
        GameVersion = current ? snapshot!.GameVersion : "Unknown";
        Campaign = current && !string.IsNullOrWhiteSpace(snapshot!.Campaign) ? snapshot.Campaign : "Waiting for gameplay";
        Timeline = current && !string.IsNullOrWhiteSpace(snapshot!.Timeline) ? snapshot.Timeline : "Unknown";
        GameState = current ? snapshot!.State : "Not connected";
        Expansions = current ? string.Join(", ", snapshot!.EnabledExpansions) : "Unknown";
        var rows = current ? snapshot!.Players.Select(p => new LivePlayerRow(p.Id, p.Name,
            string.IsNullOrWhiteSpace(p.RoomId) ? "Location unavailable" : p.RoomId,
            p.Region ?? "Unknown", p.Dead switch { true => "Dead", false => "Alive", null => "Unknown" }, p.IsLocal ? "Local" : "Online")).ToArray() : [];
        if (!Players.SequenceEqual(rows))
        {
            Players.Clear();
            foreach (var row in rows) Players.Add(row);
        }
    }

    private bool CanInstall() => !IsWorking;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private Task Install() => _install();
}
