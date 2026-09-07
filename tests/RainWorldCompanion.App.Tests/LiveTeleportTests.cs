using RainWorldCompanion.Core.Live;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.LiveProtocol;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.App.Tests;

public class LiveTeleportTests
{
    [Fact]
    public async Task Host_requests_require_peer_opt_in_and_target_that_peer()
    {
        string? target = null;
        var view = new LiveSessionViewModel(teleport: (_, player, _, _) =>
        {
            target = player;
            return Task.FromResult(new LiveCommandResult { Success = true, Message = "Peer teleported." });
        });
        var snapshot = Snapshot();
        snapshot.IsOnline = true;
        snapshot.IsHost = true;
        snapshot.Players[1].CompanionVersion = "1.0.4";
        view.AdoptConnection(LiveConnectionStatus.Connected, snapshot, true);
        Assert.NotNull(view.HostTeleportUnavailableReason(Room(), "remote"));
        snapshot.Players[1].AllowsHostControl = true;
        view.AdoptConnection(LiveConnectionStatus.Connected, snapshot, true);
        Assert.Null(view.HostTeleportUnavailableReason(Room(), "remote"));
        await view.TeleportPlayerHereAsync(Room(), "remote", "game");
        Assert.Equal("remote", target);
        Assert.Equal("Peer teleported.", view.MapActionText);
        snapshot.IsHost = false;
        view.AdoptConnection(LiveConnectionStatus.Connected, snapshot, true);
        Assert.NotNull(view.HostTeleportUnavailableReason(Room(), "remote"));
    }

    [Fact]
    public async Task A_context_menu_from_an_earlier_game_does_not_teleport()
    {
        var view = new LiveSessionViewModel(teleport: (_, _, _, _) => throw new Exception("Must not send."));
        view.AdoptConnection(LiveConnectionStatus.Connected, Snapshot(), true);
        await view.TeleportHereAsync(Room(), "earlier-game");
        Assert.Equal("Gameplay changed. Right-click the room again.", view.MapActionText);
    }

    [Fact]
    public async Task Selecting_a_remote_player_still_teleports_the_local_player()
    {
        string? target = null;
        var view = new LiveSessionViewModel(teleport: (game, player, room, region) =>
        {
            target = player;
            Assert.Equal("game", game);
            Assert.Equal("SU_A43", room, ignoreCase: true);
            return Task.FromResult(new LiveCommandResult { Success = true, Message = "Teleported." });
        });
        view.AdoptConnection(LiveConnectionStatus.Connected, Snapshot(), true);
        view.MapView.SelectedPlayer = view.MapView.Players.Single(p => !p.IsLocal);
        await view.TeleportHereAsync(Room());
        Assert.Equal("local:0", target);
        Assert.Equal("Teleported.", view.MapActionText);
        Assert.False(view.IsMapActionRunning);
    }

    [Fact]
    public void Offline_and_old_mods_disable_teleport()
    {
        var view = new LiveSessionViewModel(teleport: (_, _, _, _) => throw new Exception("Must not send."));
        Assert.NotNull(view.TeleportUnavailableReason(Room()));
        var snapshot = Snapshot();
        snapshot.CommandVersion = 0;
        view.AdoptConnection(LiveConnectionStatus.Connected, snapshot, true);
        Assert.NotNull(view.TeleportUnavailableReason(Room()));
    }

    [Fact]
    public async Task Host_control_changes_only_after_the_mod_accepts_it()
    {
        var completion = new TaskCompletionSource<LiveCommandResult>();
        var view = new LiveSessionViewModel(setHostControl: _ => completion.Task);
        view.AdoptConnection(LiveConnectionStatus.Connected, Snapshot(), true);
        Assert.False(view.AllowHostControl);
        var action = view.SetHostControlAsync(true);
        Assert.False(view.AllowHostControl);
        completion.SetResult(new() { Success = true });
        await action;
        Assert.True(view.AllowHostControl);
    }

    [Fact]
    public async Task Teleport_all_shows_peer_blockers_and_sends_group_destination()
    {
        string? destination = null;
        var view = new LiveSessionViewModel(teleportAll: (_, room, _) =>
        {
            destination = room;
            return Task.FromResult(new LiveCommandResult { Success = true, Message = "Everyone arrived." });
        });
        var snapshot = Snapshot();
        snapshot.IsOnline = snapshot.IsHost = snapshot.AllowHostControl = snapshot.SupportsTeleportAll = true;
        snapshot.TeleportAllUnavailableReason = "Guest: Allow host control is off";
        view.AdoptConnection(LiveConnectionStatus.Connected, snapshot, true);
        view.MapView.SelectedRoom = Room();
        Assert.False(view.CanTeleportAll);
        Assert.Contains("Guest", view.TeleportAllReason);
        await view.TeleportAllHereAsync(Room(), "game");
        Assert.Null(destination);
        snapshot.TeleportAllUnavailableReason = "";
        view.AdoptConnection(LiveConnectionStatus.Connected, snapshot, true);
        Assert.True(view.CanTeleportAll);
        await view.TeleportAllHereAsync(Room(), "game");
        Assert.Equal(Room().RoomId, destination);
        Assert.Equal("Everyone arrived.", view.MapActionText);
        snapshot.IsHost = false;
        view.AdoptConnection(LiveConnectionStatus.Connected, snapshot, true);
        Assert.False(view.CanTeleportAll);
    }

    [Theory]
    [InlineData(null, false, "#E05C64")]
    [InlineData("1.0.5", false, "#E8BD52")]
    [InlineData("1.0.5", true, "#58C785")]
    public void Meadow_lights_show_permission_independently_of_location(string? version, bool allowed, string color)
    {
        var view = new LiveSessionViewModel();
        var snapshot = Snapshot();
        snapshot.IsOnline = true;
        snapshot.Players[1].CompanionVersion = version;
        snapshot.Players[1].AllowsHostControl = allowed;
        snapshot.Players[1].IsHost = true;
        snapshot.Players[1].RoomId = null;
        view.AdoptConnection(LiveConnectionStatus.Connected, snapshot, true);
        var player = view.MapView.Players.Single(p => !p.IsLocal);
        Assert.Equal(color, player.ControlLight);
        Assert.True(player.IsHost);
    }

    private static MappedRoom Room() => RoomMapCatalog.Find("Downpour", "SU_A43")!;
    private static LiveSnapshot Snapshot() => new()
    {
        SessionId = "session", GameplayId = "game", CommandVersion = 1, Campaign = "White", Timeline = "White", State = "gameplay",
        EnabledExpansions = ["moreslugcats"],
        Players = [new() { Id = "local:0", IsLocal = true, Dead = false, RoomId = "SU_A43", Region = "SU" },
            new() { Id = "remote", Dead = false, RoomId = "SU_A43", Region = "SU" }]
    };
}
