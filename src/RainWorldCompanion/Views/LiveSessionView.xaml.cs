using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.Views;

public partial class LiveSessionView : UserControl
{
    private LiveMapViewModel _map = new();
    private bool _updating;
    private bool _lastSpoilerMode = true;
    private string? _centeredRoom;
    private string? _centeredPlayer;
    private Action? _refreshRoomMenu;
    public LiveSessionView()
    {
        InitializeComponent();
        WorldMap.PlayerSelected += id => _map.SelectedPlayer = _map.Players.FirstOrDefault(p => p.Id == id);
        WorldMap.PlayerFollowed += id => _map.FollowPlayer(id);
        WorldMap.RoomSelected += room => { _map.SelectedRoom = room; WorldMap.SelectedRoom = room; WorldMap.InvalidateVisual(); };
        WorldMap.ManuallyPanned += () => _map.StopFollowing();
        WorldMap.RoomContextRequested += room => { if (DataContext is LiveSessionViewModel view) OpenRoomMenu(view, room); };
        DataContextChanged += (_, _) =>
        {
            _map.Updated -= UpdateMap;
            _map = (DataContext as LiveSessionViewModel)?.MapView ?? new();
            if (IsLoaded) _map.Updated += UpdateMap;
            _centeredRoom = null;
            _centeredPlayer = null;
            UpdateMap();
        };
        Loaded += (_, _) => { _map.Updated -= UpdateMap; _map.Updated += UpdateMap; UpdateMap(); };
        Unloaded += (_, _) =>
        {
            _map.Updated -= UpdateMap;
            if (WorldMap.ContextMenu is { } menu) menu.IsOpen = false;
        };
    }

    private void OpenRoomMenu(LiveSessionViewModel view, MappedRoom room)
    {
        if (WorldMap.ContextMenu is { } previous) previous.IsOpen = false;
        var refreshers = new List<Action>();
        string? reason = view.TeleportUnavailableReason(room);
        var teleport = new MenuItem
        {
            Header = "Teleport me here", IsEnabled = reason == null,
            ToolTip = reason ?? $"Move {view.TeleportPlayer?.Name} to {room.RoomId}. Changing regions brings all local co-op players."
        };
        ToolTipService.SetShowOnDisabled(teleport, true);
        refreshers.Add(() => { var why = view.TeleportUnavailableReason(room); teleport.IsEnabled = why == null; teleport.ToolTip = why ?? "Move your player here."; });
        string? gameplayId = view.CurrentGameplayId;
        teleport.Click += async (_, _) => await view.TeleportHereAsync(room, gameplayId);
        var menu = new ContextMenu { PlacementTarget = WorldMap, Style = (Style)FindResource("Map.RoomMenu") };
        menu.Items.Add(new MenuItem { Header = room.RoomId, IsEnabled = false });
        menu.Items.Add(teleport);
        if (view.IsMeadowHost)
        {
            string? allReason = view.TeleportAllUnavailableReason(room);
            var all = new MenuItem { Header = "Teleport all here", IsEnabled = allReason == null, ToolTip = allReason ?? "Move everyone, including across regions." };
            ToolTipService.SetShowOnDisabled(all, true);
            refreshers.Add(() => { var why = view.TeleportAllUnavailableReason(room); all.IsEnabled = why == null; all.ToolTip = why ?? "Move everyone here."; });
            all.Click += async (_, _) => await view.TeleportAllHereAsync(room, gameplayId);
            menu.Items.Add(all);
            foreach (var player in _map.Players.Where(p => !p.IsLocal))
            {
                string? unavailable = view.HostTeleportUnavailableReason(room, player.Id);
                var action = new MenuItem { Header = "Teleport " + player.Name + " here", IsEnabled = unavailable == null,
                    ToolTip = unavailable ?? "Request a teleport from this player's mod." };
                ToolTipService.SetShowOnDisabled(action, true);
                refreshers.Add(() => { var why = view.HostTeleportUnavailableReason(room, player.Id); action.IsEnabled = why == null; action.ToolTip = why ?? "Request a teleport from this player's mod."; });
                action.Click += async (_, _) => await view.TeleportPlayerHereAsync(room, player.Id, gameplayId);
                menu.Items.Add(action);
            }
        }
        WorldMap.ContextMenu = menu;
        _refreshRoomMenu = () => { foreach (var refresh in refreshers) refresh(); };
        System.ComponentModel.PropertyChangedEventHandler changed = (_, _) => _refreshRoomMenu?.Invoke();
        view.PropertyChanged += changed;
        menu.Closed += (_, _) =>
        {
            view.PropertyChanged -= changed;
            if (ReferenceEquals(WorldMap.ContextMenu, menu)) { WorldMap.ContextMenu = null; _refreshRoomMenu = null; }
        };
        menu.IsOpen = true;
    }

    private async void HostControlChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is LiveSessionViewModel view && sender is CheckBox toggle)
            await ApplyHostControlClick(toggle, view);
    }

    internal static async Task ApplyHostControlClick(CheckBox toggle, LiveSessionViewModel view)
    {
        bool enabled = toggle.IsChecked == true;
        await view.SetHostControlAsync(enabled);
        toggle.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, view.AllowHostControl);
    }

    private void UpdateMap()
    {
        _updating = true;
        try
        {
            if (_lastSpoilerMode != _map.SpoilerMode && WorldMap.ContextMenu is { } menu) menu.IsOpen = false;
            _lastSpoilerMode = _map.SpoilerMode;
            WorldMap.Load(_map.Map);
            WorldMap.SetExploration(_map.SpoilerMode, _map.VisitedRooms);
            WorldMap.SpoilerDetailView = _map.SpoilerDetailView;
            WorldMap.Players = _map.Players;
            WorldMap.SelectedPlayerId = _map.SelectedPlayer?.Id;
            WorldMap.SelectedRoom = _map.SelectedRoom;
            if (_map.FollowedPlayer is { Placement: { } room } player)
            {
                string key = _map.Map?.Id + ":" + room.RoomId;
                if (_centeredRoom != key || _centeredPlayer != player.Id)
                {
                    WorldMap.Center(room, preserveZoom: true);
                    _centeredRoom = key;
                    _centeredPlayer = player.Id;
                }
            }
            else { _centeredRoom = null; _centeredPlayer = null; }
            WorldMap.InvalidateVisual();
            _refreshRoomMenu?.Invoke();
        }
        finally { _updating = false; }
    }

    private void FitWorld(object sender, RoutedEventArgs e) { _map.StopFollowing(); _centeredRoom = null; WorldMap.Fit(); }
    private void ZoomIn(object sender, RoutedEventArgs e) => WorldMap.Zoom(1.4);
    private void ZoomOut(object sender, RoutedEventArgs e) => WorldMap.Zoom(1 / 1.4);
    private void MapChosen(object sender, SelectionChangedEventArgs e)
    {
        if (_map is not null && !_updating && MapChoice.SelectedItem is DenMapDefinition map) _map.Browse(map);
    }
    private void SearchRoomSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_map?.SelectedRoom is not { } room || _updating) return;
        _map.StopFollowing();
        WorldMap.SelectedRoom = room;
        WorldMap.Center(room, preserveZoom: false);
    }
    private void PlayerSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_map is null) return;
        WorldMap.SelectedPlayerId = _map.SelectedPlayer?.Id;
        WorldMap.InvalidateVisual();
    }
    private void PlayerDoubleClicked(object sender, MouseButtonEventArgs e)
    {
        if (_map.SelectedPlayer is { } player) { _centeredRoom = null; _map.FollowPlayer(player.Id); }
    }
    private void FollowSelected(object sender, RoutedEventArgs e)
    {
        if (_map.IsFollowing) _map.StopFollowing();
        else if (_map.SelectedPlayer is { } player) { _centeredRoom = null; _map.FollowPlayer(player.Id); }
    }
}
