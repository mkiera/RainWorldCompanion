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
        Loaded += (_, _) => UpdateMap();
        Closed += (_, _) => _map.Updated -= UpdateMap;
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
