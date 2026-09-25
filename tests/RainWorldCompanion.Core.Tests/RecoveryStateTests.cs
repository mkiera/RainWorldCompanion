using RWCompanion.Mod;

namespace RainWorldCompanion.Tests;

public class RecoveryStateTests
{
    [Fact]
    public void Revival_uses_the_inherited_private_alive_setter()
    {
        var player = new PlayerState { Food = 3 };
        Assert.True(player.dead);
        RecoveryState.Revive(player);
        Assert.False(player.dead);
        Assert.Equal(3, player.Food);
    }

    private class CreatureState
    {
        public bool alive { get; private set; }
        public bool dead => !alive;
    }

    private sealed class PlayerState : CreatureState
    {
        public int Food { get; set; }
    }
}
