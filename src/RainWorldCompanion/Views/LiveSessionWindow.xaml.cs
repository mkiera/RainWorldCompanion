using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.Views;

public partial class LiveSessionWindow : Window
{
    private readonly LiveMapViewModel _map;
    private bool _updating;
    private string? _centeredRoom;
    private string? _centeredPlayer;
    public LiveSessionWindow(LiveSessionViewModel view)
    {
        InitializeComponent();
        DataContext = view;
        _map = view.MapView;
        _map.Updated += UpdateMap;
        WorldMap.PlayerSelected += id => _map.SelectedPlayer = _map.Players.FirstOrDefault(p => p.Id == id);
        WorldMap.PlayerFollowed += _map.FollowPlayer;
        WorldMap.RoomSelected += room => { _map.SelectedRoom = room; WorldMap.SelectedRoom = room; WorldMap.InvalidateVisual(); };
        WorldMap.ManuallyPanned += _map.StopFollowing;
        WorldMap.RoomContextRequested += room => OpenRoomMenu(view, room);
        Loaded += (_, _) => UpdateMap();
        Closed += (_, _) => _map.Updated -= UpdateMap;
    }

    private void OpenRoomMenu(LiveSessionViewModel view, MappedRoom room)
    {
        string? reason = view.TeleportUnavailableReason(room);
        var teleport = new MenuItem
        {
            Header = "Teleport me here", IsEnabled = reason == null,
            ToolTip = reason ?? $"Move {view.TeleportPlayer?.Name} to {room.RoomId}. Changing regions brings all local co-op players."
        };
        ToolTipService.SetShowOnDisabled(teleport, true);
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
            all.Click += async (_, _) => await view.TeleportAllHereAsync(room, gameplayId);
            menu.Items.Add(all);
            foreach (var player in _map.Players.Where(p => !p.IsLocal))
            {
                string? unavailable = view.HostTeleportUnavailableReason(room, player.Id);
                var action = new MenuItem { Header = "Teleport " + player.Name + " here", IsEnabled = unavailable == null,
                    ToolTip = unavailable ?? "Request a teleport from this player's mod." };
                ToolTipService.SetShowOnDisabled(action, true);
                action.Click += async (_, _) => await view.TeleportPlayerHereAsync(room, player.Id, gameplayId);
                menu.Items.Add(action);
            }
        }
        WorldMap.ContextMenu = menu;
        menu.Closed += (_, _) => { if (ReferenceEquals(WorldMap.ContextMenu, menu)) WorldMap.ContextMenu = null; };
        menu.IsOpen = true;
    }

    private async void HostControlChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is LiveSessionViewModel view && sender is CheckBox toggle)
        {
            bool enabled = toggle.IsChecked == true;
            toggle.IsChecked = view.AllowHostControl;
            await view.SetHostControlAsync(enabled);
        }
    }

    private void UpdateMap()
    {
        _updating = true;
        try
        {
            WorldMap.Load(_map.Map);
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
