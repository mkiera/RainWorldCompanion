using RWCompanion.Mod;

namespace RainWorldCompanion.Core.Tests;

public sealed class RecoveryDestinationTests
{
    [Fact]
    public void Current_active_room_wins()
    {
        Assert.Equal("SU_A01", RecoveryDestination.Choose("SU_A01", "SU_A02", new[] { "SU_A01", "SU_A02" }));
    }

    [Fact]
    public void Observed_room_recovers_an_avatar_without_a_current_room()
    {
        Assert.Equal("SU_A02", RecoveryDestination.Choose(null, "SU_A02", new[] { "SU_A01", "SU_A02" }));
    }

    [Fact]
    public void Room_from_an_inactive_world_is_rejected()
    {
        Assert.Null(RecoveryDestination.Choose(null, "HI_A01", new[] { "SU_A01" }));
    }
}
