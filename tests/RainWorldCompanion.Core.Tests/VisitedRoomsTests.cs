using RainWorldCompanion.LiveProtocol;
using RWCompanion.Mod;

namespace RainWorldCompanion.Tests;

public class VisitedRoomsTests
{
    [Fact]
    public void Reads_loaded_and_unloaded_regions_without_mod_visit_history()
    {
        var active = new Region { roomsVisited = ["SU_A43", "SU_S04"] };
        var session = new Session { saveState = new Save
        {
            regionStates = [active, null],
            regionLoadStrings = ["ROOMSVISITED<rgB>SU_STALE<rgA>", "REGIONNAME<rgB>HI<rgA>ROOMSVISITED<rgB>HI_A01,HI_B02,<rgA>OTHER<rgB>HI_SECRET<rgA>"]
        }};
        var snapshot = new LiveSnapshot();
        VisitedRooms.Read(session, snapshot);
        Assert.True(snapshot.HasExplorationData);
        Assert.Equal(["HI_A01", "HI_B02", "SU_A43", "SU_S04"], snapshot.VisitedRooms);
        active.roomsVisited.Add("SU_A37");
        var next = new LiveSnapshot();
        VisitedRooms.Read(session, next);
        Assert.Contains("SU_A37", next.VisitedRooms);
        Assert.DoesNotContain("SU_A37", snapshot.VisitedRooms);
        Assert.DoesNotContain("HI_SECRET", next.VisitedRooms);
        Assert.DoesNotContain("SU_STALE", next.VisitedRooms);
    }

    [Fact]
    public void No_campaign_does_not_claim_exploration_data()
    {
        var snapshot = new LiveSnapshot();
        VisitedRooms.Read(new Session(), snapshot);
        Assert.False(snapshot.HasExplorationData);
        Assert.Empty(snapshot.VisitedRooms);
    }

    private sealed class Session { public Save? saveState; }
    private sealed class Save
    {
        public Region?[] regionStates = [];
        public string?[] regionLoadStrings = [];
    }
    private sealed class Region { public List<string> roomsVisited = []; }
}
