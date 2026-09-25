using RainWorldCompanion.Core.Live;
using RainWorldCompanion.LiveProtocol;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.App.Tests;

public class LiveSessionTests
{
    [Theory]
    [InlineData(LiveConnectionStatus.Waiting, false)]
    [InlineData(LiveConnectionStatus.Disconnected, false)]
    [InlineData(LiveConnectionStatus.Disconnected, true)]
    public void Enabled_compatible_install_remains_green_without_connection(LiveConnectionStatus status, bool running)
    {
        var view = new LiveSessionViewModel();
        view.AdoptSetup(true, true, "1.0.1", "Installed and enabled");
        view.AdoptConnection(status, null, running);

        Assert.True(view.SetupReady);
        Assert.Equal("1.0.1", view.InstalledVersion);
        Assert.Equal("Not connected", view.RunningVersion);
        Assert.Empty(view.Players);
    }

    [Fact]
    public void Room_updates_keep_canonical_names_and_unknown_remote_locations()
    {
        var view = new LiveSessionViewModel();
        view.AdoptSetup(true, true, "1.0.1", "Installed and enabled");
        var snapshot = new LiveSnapshot
        {
            ModVersion = "1.0.0", Campaign = "White", Timeline = "Rivulet",
            Players = [new LivePlayer { Id = "local", Name = "Player 1", RoomId = "MS_bittershelter", IsLocal = true, Dead = false },
                new LivePlayer { Id = "peer", Name = "Peer" }],
        };
        view.AdoptConnection(LiveConnectionStatus.Connected, snapshot, true);
        Assert.Equal("MS_bittershelter", view.Players[0].Location);
        Assert.Equal("Location unavailable", view.Players[1].Location);
        Assert.Equal("Alive", view.Players[0].State);
        Assert.Equal("Unknown", view.Players[1].State);
        Assert.Equal("1.0.0", view.RunningVersion);
        Assert.Equal("1.0.1", view.InstalledVersion);
        Assert.Equal("Rivulet", view.Timeline);

        snapshot.Players[0].RoomId = "MS_LAB5";
        snapshot.Players[0].Dead = true;
        view.AdoptConnection(LiveConnectionStatus.Connected, snapshot, true);
        Assert.Equal("MS_LAB5", view.Players[0].Location);
        Assert.Equal("Dead", view.Players[0].State);
        view.AdoptConnection(LiveConnectionStatus.Disconnected, null, true);
        Assert.Empty(view.Players);
        Assert.Equal("Unknown", view.Timeline);
        Assert.True(view.SetupReady);
    }

    [Fact]
    public void Live_connection_does_not_hide_disabled_installation()
    {
        var view = new LiveSessionViewModel();
        view.AdoptSetup(true, false, "1.0.0", "Disabled");
        view.AdoptConnection(LiveConnectionStatus.Connected, new LiveSnapshot(), true);
        Assert.False(view.SetupReady);
        Assert.Equal("Disabled", view.SetupText);
        Assert.Equal("Connected", view.ConnectionText);
    }
}
