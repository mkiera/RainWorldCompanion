using System.Windows;
using RainWorldCompanion.Controls;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.LiveProtocol;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.App.Tests;

public class LiveMapTests
{
    [Fact]
    public void Live_player_in_an_omitted_room_gets_an_explanation()
    {
        var view = new LiveMapViewModel { SpoilerMode = false };
        view.Adopt(new() { SessionId = "session", Campaign = "White", Timeline = "White",
            EnabledExpansions = ["moreslugcats"], Players = [new() { Id = "one", RoomId = "SL_ECNIUS01" }] });
        Assert.Null(view.Players[0].Placement);
        Assert.Equal("Unavailable on this map", view.Players[0].MapStatus);
        Assert.Contains("SL_D01", view.Players[0].UnavailableReason);
    }

    [Theory]
    [InlineData("White", true, "Downpour")]
    [InlineData("White", false, "Vanilla")]
    [InlineData("Gourmand", true, "Downpour")]
    [InlineData("Rivulet", true, "Rivulet")]
    [InlineData("Spear", true, "Spearmaster")]
    [InlineData("Saint", true, "Saint")]
    [InlineData("Artificer", true, "Artificer")]
    [InlineData("Watcher", true, null)]
    public void Map_follows_actual_timeline_and_expansion(string timeline, bool downpour, string? expected)
    {
        var view = new LiveMapViewModel { SpoilerMode = false };
        view.Adopt(new() { SessionId = "session", Campaign = "White", Timeline = timeline,
            EnabledExpansions = downpour ? ["moreslugcats"] : [], Players = [new() { Id = "one", RoomId = "UNKNOWN_ROOM" }] });
        Assert.Equal(expected, view.Map?.Id);
        Assert.Equal("Not on this map", view.Players[0].MapStatus);
    }

    [Fact]
    public void Following_survives_unavailable_rooms_and_stops_when_player_leaves_or_session_ends()
    {
        var view = new LiveMapViewModel { SpoilerMode = false };
        var snapshot = new LiveSnapshot { SessionId = "one", Campaign = "White", Timeline = "White", EnabledExpansions = ["moreslugcats"],
            Players = [new() { Id = "peer", Name = "Peer", RoomId = "SU_S04" }] };
        view.Adopt(snapshot);
        view.FollowPlayer("peer");
        Assert.NotNull(view.FollowedPlayer?.Placement);
        snapshot.Players[0].RoomId = null;
        view.Adopt(snapshot);
        Assert.Equal("peer", view.FollowedPlayerId);
        Assert.Null(view.FollowedPlayer?.Placement);
        Assert.Contains("waiting", view.FollowStatus);
        snapshot.Players[0].RoomId = "SU_S03";
        view.Adopt(snapshot);
        Assert.NotNull(view.FollowedPlayer?.Placement);
        snapshot.Players = [];
        view.Adopt(snapshot);
        Assert.False(view.IsFollowing);
        snapshot.Players = [new() { Id = "peer", RoomId = "SU_S04" }];
        view.Adopt(snapshot);
        view.FollowPlayer("peer");
        view.Adopt(null);
        Assert.Empty(view.Players);
        Assert.False(view.IsFollowing);
    }

    [Fact]
    public void Search_preserves_canonical_den_names_and_follow_center_preserves_zoom()
    {
        var view = new LiveMapViewModel { SpoilerMode = false };
        view.Browse(DenMapCatalog.Rivulet);
        view.Query = "ms_BITTER";
        Assert.Contains(view.SearchResults, room => room.RoomId == "MS_bittershelter");
        var viewport = new DenMapViewport();
        viewport.Resize(new Size(800, 500));
        viewport.Zoom(3, new Point(400, 250));
        double scale = viewport.Scale;
        viewport.Center(new Point(10, 10), clamp: false);
        Assert.Equal(scale, viewport.Scale);
        Assert.Equal(new Point(400, 250), viewport.ToScreen(new Point(10, 10)));
    }
}
