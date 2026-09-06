using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Tests;

public class RoomMapCatalogTests
{
    [Fact]
    public void Every_bundled_map_has_dens_and_reference_geometry()
    {
        foreach (var map in DenMapCatalog.Maps)
        {
            var rooms = RoomMapCatalog.ForMap(map.Id);
            Assert.True(rooms.Count >= map.Dens.Count);
            Assert.Contains(rooms, room => room.MatchKind == "den-anchor" && room.Bounds.Count == 0);
            Assert.Contains(rooms, room => room.MatchKind.StartsWith("reference-affine", StringComparison.Ordinal) && room.Bounds.Count > 0);
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
