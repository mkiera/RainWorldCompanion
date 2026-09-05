using RainWorldCompanion.Core.System;

namespace RainWorldCompanion.Tests;

// Every form here comes from Rain Meadow itself or from Steam, and has to reach
// MatchmakingManager.JoinLobbyUsingCode unchanged.
public class MeadowJoinTests
{
    private const string LobbyId = "109775241234567890";

    private static string FakeInstall(TempDirectory dir)
    {
        string game = System.IO.Path.Combine(dir.Path, "game");
        System.IO.Directory.CreateDirectory(game);
        System.IO.File.WriteAllText(System.IO.Path.Combine(game, MeadowJoin.GameExecutableName), "");
        return game;
    }

    [Fact]
    public void A_bare_lobby_id_is_enough()
    {
        MeadowJoin? join = MeadowJoin.Read(LobbyId, out string? problem);

        Assert.Null(problem);
        Assert.Equal(LobbyId, join!.SteamLobbyId);
        Assert.False(join.HasPassword);
        Assert.False(join.IsLan);
    }

    [Fact]
    public void The_join_code_the_mod_prints_is_read_back()
    {
        MeadowJoin? join = MeadowJoin.Read(
            $"+connect_lobby {LobbyId} +lobby_password hunter2",
            out string? problem);

        Assert.Null(problem);
        Assert.Equal(LobbyId, join!.SteamLobbyId);
        Assert.Equal("hunter2", join.Password);
        Assert.Equal($"+connect_lobby {LobbyId} +lobby_password hunter2", join.JoinCode);
    }

    [Fact]
    public void A_Steam_invite_link_is_read()
    {
        MeadowJoin? join = MeadowJoin.Read(
            $"steam://joinlobby/312520/{LobbyId}/76561198175665721",
            out string? problem);

        Assert.Null(problem);
        Assert.Equal(LobbyId, join!.SteamLobbyId);
    }

    [Fact]
    public void An_invite_link_for_another_game_is_refused()
    {
        MeadowJoin? join = MeadowJoin.Read($"steam://joinlobby/570/{LobbyId}/1", out string? problem);

        Assert.Null(join);
        Assert.Contains("another game", problem);
    }

    [Fact]
    public void A_local_join_code_keeps_the_packing_the_mod_wrote()
    {
        MeadowJoin? join = MeadowJoin.Read("+connect_lan_lobby 16885952 8720", out string? problem);

        Assert.Null(problem);
        Assert.True(join!.IsLan);
        Assert.Equal("16885952", join.LanAddress);
        Assert.Equal("8720", join.LanPort);
        Assert.Equal("192.168.1.1", join.DottedLanAddress);
    }

    [Fact]
    public void A_typed_local_address_is_packed_the_same_way()
    {
        MeadowJoin? join = MeadowJoin.Read("+connect_lan_lobby 192.168.1.1 8720", out string? problem);

        Assert.Null(problem);
        Assert.Equal("16885952", join!.LanAddress);
    }

    [Fact]
    public void A_local_join_code_with_no_port_is_refused()
    {
        MeadowJoin? join = MeadowJoin.Read("+connect_lan_lobby 192.168.1.1", out string? problem);

        Assert.Null(join);
        Assert.Contains("address and port", problem);
    }

    [Fact]
    public void Text_that_names_no_lobby_is_refused()
    {
        MeadowJoin? join = MeadowJoin.Read("come play with us", out string? problem);

        Assert.Null(join);
        Assert.Contains("not a lobby id", problem);
    }

    [Fact]
    public void Nothing_typed_asks_for_something_to_be_typed()
    {
        Assert.Null(MeadowJoin.Read("   ", out string? problem));
        Assert.Contains("Paste a lobby id", problem);
    }

    [Fact]
    public void A_lobby_with_no_password_goes_through_Steam()
    {
        MeadowStart start = MeadowJoin.Read(LobbyId, out _)!.Start(gameInstallPath: null);

        Assert.True(start.CanRun);
        Assert.True(start.ThroughSteam);
        Assert.Equal($"steam://joinlobby/312520/{LobbyId}", start.SteamUrl);
    }

    [Fact]
    public void A_password_starts_the_game_itself_because_Steam_cannot_carry_one()
    {
        using var dir = new TempDirectory();
        string game = FakeInstall(dir);

        MeadowStart start = MeadowJoin.Read(LobbyId, out _)!.WithPassword("hunter2").Start(game);

        Assert.True(start.CanRun);
        Assert.False(start.ThroughSteam);
        Assert.Equal(System.IO.Path.Combine(game, MeadowJoin.GameExecutableName), start.Executable);
        Assert.Equal(
            new[] { "+connect_lobby", LobbyId, "+lobby_password", "hunter2" },
            start.Arguments);
        Assert.Contains("cannot carry a password", start.Headline);
    }

    [Fact]
    public void A_local_lobby_starts_the_game_itself_too()
    {
        using var dir = new TempDirectory();
        string game = FakeInstall(dir);

        MeadowStart start = MeadowJoin.Read("+connect_lan_lobby 192.168.1.1 8720", out _)!.Start(game);

        Assert.True(start.CanRun);
        Assert.Equal(new[] { "+connect_lan_lobby", "16885952", "8720" }, start.Arguments);
        Assert.Contains("local network", start.Headline);
    }

    [Fact]
    public void A_password_holding_a_space_is_refused_rather_than_cut_in_half()
    {
        using var dir = new TempDirectory();
        string game = FakeInstall(dir);

        MeadowStart start = MeadowJoin.Read(LobbyId, out _)!.WithPassword("open sesame").Start(game);

        Assert.False(start.CanRun);
        Assert.Contains("space", start.Problem);
    }

    [Fact]
    public void A_password_with_no_game_folder_says_where_to_set_one()
    {
        MeadowStart start = MeadowJoin.Read(LobbyId, out _)!.WithPassword("hunter2").Start(null);

        Assert.False(start.CanRun);
        Assert.Contains("game folder is not set", start.Problem);
    }

    [Fact]
    public void A_game_folder_holding_no_executable_is_refused()
    {
        using var dir = new TempDirectory();

        MeadowStart start = MeadowJoin.Read(LobbyId, out _)!.WithPassword("hunter2").Start(dir.Path);

        Assert.False(start.CanRun);
        Assert.Contains(MeadowJoin.GameExecutableName, start.Problem);
    }

    [Fact]
    public void A_password_typed_beside_the_code_is_taken_when_the_code_carries_none()
    {
        MeadowJoin join = MeadowJoin.Read($"+connect_lobby {LobbyId}", out _)!.WithPassword("  hunter2 ");

        Assert.Equal("hunter2", join.Password);
    }

    [Fact]
    public void A_password_in_the_code_survives_an_empty_password_box()
    {
        MeadowJoin join = MeadowJoin
            .Read($"+connect_lobby {LobbyId} +lobby_password hunter2", out _)!
            .WithPassword("");

        Assert.Equal("hunter2", join.Password);
    }
}
