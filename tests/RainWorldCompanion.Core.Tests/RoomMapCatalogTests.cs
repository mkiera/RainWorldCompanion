using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Tests;

public class RoomMapCatalogTests
{
    [Theory]
    [InlineData("downpour", "sl_ecnius01")]
    [InlineData("Rivulet", "SL_ECNIUS03")]
    [InlineData("Saint", "SB_E05SAINT")]
    [InlineData("Downpour", "OE_TEMP")]
    public void Rooms_omitted_from_artwork_have_a_reason_and_no_coordinates(string map, string room)
    {
        Assert.Null(RoomMapCatalog.Find(map, room));
        Assert.False(string.IsNullOrWhiteSpace(RoomMapCatalog.UnavailableReason(map, room)));
    }

    [Fact]
    public void Shoreline_route_is_available_on_the_saint_map()
    {
        Assert.NotNull(RoomMapCatalog.Find("Saint", "SL_ECNIUS01"));
        Assert.Null(RoomMapCatalog.UnavailableReason("Saint", "SL_ECNIUS01"));
        Assert.Null(RoomMapCatalog.UnavailableReason("Downpour", "UNKNOWN_ROOM"));
    }

    [Fact]
    public void Every_bundled_map_has_dens_and_reference_geometry()
    {
        foreach (var map in DenMapCatalog.Maps)
        {
            var rooms = RoomMapCatalog.ForMap(map.Id);
            Assert.True(rooms.Count >= map.Dens.Count);
            Assert.Contains(rooms, room => room.MatchKind == "den-anchor" && room.Bounds.Count == 0);
            Assert.Contains(rooms, room => room.MatchKind.StartsWith("terrain-template", StringComparison.Ordinal) && room.Bounds.Count > 0);
            Assert.DoesNotContain(rooms, room => room.MatchKind.StartsWith("reference-affine", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Lookups_ignore_room_and_map_casing()
    {
        var room = Assert.IsType<MappedRoom>(RoomMapCatalog.Find("vanilla", " su_a02 "));
        Assert.Equal("su_a02", room.RoomId);
        Assert.NotEmpty(room.Bounds);
        Assert.Empty(RoomMapCatalog.ForMap("unknown"));
        Assert.Null(RoomMapCatalog.Find("Vanilla", "missing"));
    }
}
