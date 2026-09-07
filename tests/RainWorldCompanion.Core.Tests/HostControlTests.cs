using RWCompanion.Mod;

namespace RainWorldCompanion.Tests;

public class HostControlTests
{
    [Fact]
    public void Only_the_current_host_can_use_an_enabled_gameplay_grant()
    {
        var permission = new HostControlPermission();
        var lobby = new object();
        var host = new object();
        permission.Update(lobby, host, "game", false);
        Assert.False(permission.Accepts(host, permission.Grant));
        permission.Update(lobby, host, "game", true);
        Assert.True(permission.Accepts(host, permission.Grant));
        Assert.False(permission.Accepts(new object(), permission.Grant));
        Assert.False(permission.Accepts(host, "outdated"));
    }

    [Fact]
    public void Turning_permission_off_and_back_on_invalidates_queued_requests()
    {
        var permission = new HostControlPermission();
        var lobby = new object();
        var host = new object();
        permission.Update(lobby, host, "game", true);
        string previous = permission.Grant;
        permission.Update(lobby, host, "game", false);
        Assert.False(permission.Accepts(host, previous));
        permission.Update(lobby, host, "game", true);
        Assert.False(permission.Accepts(host, previous));
        Assert.True(permission.Accepts(host, permission.Grant));
    }

    [Theory]
    [InlineData("host")]
    [InlineData("lobby")]
    [InlineData("game")]
    [InlineData("menu")]
    public void A_changed_session_invalidates_old_host_requests(string change)
    {
        var permission = new HostControlPermission();
        var lobby = new object();
        var host = new object();
        permission.Update(lobby, host, "game", true);
        string previous = permission.Grant;
        permission.Update(change == "lobby" ? new object() : lobby, change == "host" ? new object() : host,
            change == "game" ? "another-game" : change == "menu" ? "" : "game", true);
        Assert.False(permission.Accepts(host, previous));
    }
}
