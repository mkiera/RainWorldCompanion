using System.Windows;
using RainWorldCompanion.Controls;
using RainWorldCompanion.Core.Live;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.LiveProtocol;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.App.Tests;

public class SpoilerMapTests
{
    [Fact]
    public void Developer_toggle_updates_live_map_without_revealing_room_actions()
    {
        var map = new LiveMapViewModel();
        map.Adopt(Snapshot());
        var developer = new DeveloperViewModel(map);
        int updates = 0;
        map.Updated += () => updates++;
        Assert.False(developer.MapView.SpoilerDetailView);
        developer.MapView.SpoilerDetailView = true;
        Assert.True(map.SpoilerDetailView);
        Assert.Equal(1, updates);
        Assert.False(map.IsRoomVisible("SU_A37"));
        map.Query = "SU_A37";
        Assert.Empty(map.SearchResults);
        developer.MapView.SpoilerDetailView = false;
        Assert.Equal(2, updates);
    }

    [Fact]
    public void Saved_visits_reveal_rooms_without_app_history_and_remote_players_do_not_reveal_rooms()
    {
        var view = new LiveMapViewModel();
        Assert.True(view.SpoilerMode);
        var snapshot = Snapshot();
        view.Adopt(snapshot);
        view.Query = "SU_A";
        Assert.Single(view.SearchResults);
        Assert.Equal("su_a43", view.SearchResults[0].RoomId, ignoreCase: true);
        Assert.Null(view.Players[0].Placement);
        Assert.Equal("Unexplored room", view.Players[0].RoomId);
        snapshot.VisitedRooms = ["SU_A43", "SU_A37"];
        view.Adopt(snapshot);
        Assert.NotNull(view.Players[0].Placement);
        Assert.Equal(2, view.SearchResults.Count);
        view.SelectedRoom = view.SearchResults[0];
    }

    [Fact]
    public void Missing_data_and_campaign_changes_clear_reveals_and_selection()
    {
        var view = new LiveMapViewModel();
        view.Adopt(Snapshot());
        view.SelectedRoom = RoomMapCatalog.Find("Downpour", "SU_A43");
        view.Adopt(new() { SessionId = "other", Campaign = "White", Timeline = "White", EnabledExpansions = ["moreslugcats"] });
        Assert.False(view.IsRoomVisible("SU_A43"));
        Assert.Null(view.SelectedRoom);
        view.Query = "SU";
        Assert.Empty(view.SearchResults);
        view.SpoilerMode = false;
        Assert.NotEmpty(view.SearchResults);
        view.SpoilerMode = true;
        Assert.Empty(view.SearchResults);
    }

    [Fact]
    public async Task Hidden_rooms_cannot_be_teleported_to_from_stale_actions()
    {
        var view = new LiveSessionViewModel(teleport: (_, _, _, _) => throw new Exception("Must not send."));
        var snapshot = Snapshot();
        snapshot.Players[0].IsLocal = true;
        view.AdoptConnection(LiveConnectionStatus.Connected, snapshot, true);
        await view.TeleportHereAsync(RoomMapCatalog.Find("Downpour", "SU_A37")!);
        Assert.Contains("hidden", view.MapActionText);
    }

    [Fact]
    public void Reveal_preserves_visited_overlap_and_hides_everything_outside_visited_bounds()
    {
        var visited = new MappedRoom("known", "SU", 10, 10, [new(0, 0, 20, 20)], "test");
        var unknown = new MappedRoom("unknown", "SU", 20, 10, [new(15, 0, 20, 20)], "test");
        var geometry = LiveMapCanvas.CreateReveal([visited, unknown], new HashSet<string> { "known" });
        Assert.True(geometry.FillContains(new Point(5, 5)));
        Assert.True(geometry.FillContains(new Point(18, 5)));
        Assert.False(geometry.FillContains(new Point(30, 5)));
        Assert.False(geometry.FillContains(new Point(5, 25)));
        Assert.True(LiveMapCanvas.CreateReveal([visited], new HashSet<string>()).IsEmpty());
    }

    [Fact]
    public void Visited_shelter_icons_are_revealed_without_revealing_the_unvisited_approach()
    {
        var den = RoomMapCatalog.Find("Downpour", "SB_S06")!;
        var approach = RoomMapCatalog.Find("Downpour", "SB_GOR01")!;
        var geometry = LiveMapCanvas.CreateReveal(RoomMapCatalog.ForMap("Downpour"),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SB_S06", "SB_H03" });
        Assert.True(geometry.FillContains(new Point(den.X, den.Y)));
        Assert.False(geometry.FillContains(new Point(approach.X, approach.Y)));
    }

    private static LiveSnapshot Snapshot() => new()
    {
        SessionId = "session", GameplayId = "game", Campaign = "White", Timeline = "White", State = "gameplay", CommandVersion = 1,
        EnabledExpansions = ["moreslugcats"], HasExplorationData = true, VisitedRooms = ["SU_A43"],
        Players = [new() { Id = "peer", Name = "Peer", RoomId = "SU_A37", Region = "SU", Dead = false }]
    };
}
