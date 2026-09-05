using RainWorldCompanion.Core.Mods;
using RainWorldCompanion.Core.System;

namespace RainWorldCompanion.Tests;

// The read runs in a second copy of the program and comes back as one JSON document on its
// standard output, so the arguments going out and the answer coming back are what is tested.
public class MeadowSteamProcessTests
{
    private static MeadowLobbyList SampleList() => new()
    {
        Friends = new[]
        {
            new MeadowFriend { SteamId = "76561198000000001", Name = "CoffeeCups", LobbyId = "109775249876543210" },
            new MeadowFriend { SteamId = "76561198000000002", Name = "Tiny", LobbyId = "" },
        },
        Lobbies = new[]
        {
            new MeadowLobby
            {
                Id = "109775249876543210",
                Name = "青鸟",
                Mode = "Story",
                Players = 2,
                MaxPlayers = 6,
                HasPassword = true,
                Campaign = "White",
                HasFriend = true,
                FriendName = "CoffeeCups",
                Mods = MeadowLobbyMods.Read("rwremix\nhenpemaz_rainmeadow", "maxi-mol.mousedrag\nfyre.BeastMaster"),
            },
        },
    };

    [Fact]
    public void The_switch_is_the_first_argument_and_nothing_else_counts()
    {
        Assert.True(MeadowSteamProcess.IsHelperCall(new[] { MeadowSteamProcess.Switch }));
        Assert.False(MeadowSteamProcess.IsHelperCall(Array.Empty<string>()));
        Assert.False(MeadowSteamProcess.IsHelperCall(new[] { "open", MeadowSteamProcess.Switch }));
    }

    [Fact]
    public void Arguments_come_back_through_the_parser_unchanged()
    {
        IReadOnlyList<string> args = MeadowSteamProcess.Arguments(@"C:\Games\Rain World", "0.1.15.2", TimeSpan.FromSeconds(12));

        Assert.True(MeadowSteamProcess.TryParse(args, out string? game, out string? version, out TimeSpan timeout));
        Assert.Equal(@"C:\Games\Rain World", game);
        Assert.Equal("0.1.15.2", version);
        Assert.Equal(TimeSpan.FromSeconds(12), timeout);
    }

    [Fact]
    public void An_unset_game_folder_and_version_travel_as_empty_and_come_back_null()
    {
        IReadOnlyList<string> args = MeadowSteamProcess.Arguments(null, null, TimeSpan.FromMilliseconds(1));

        Assert.True(MeadowSteamProcess.TryParse(args, out string? game, out string? version, out TimeSpan timeout));
        Assert.Null(game);
        Assert.Null(version);
        Assert.Equal(TimeSpan.FromSeconds(1), timeout);
    }

    [Theory]
    [InlineData("--read-lobbies")]
    [InlineData("--read-lobbies", "game", "1.0")]
    [InlineData("--read-lobbies", "game", "1.0", "soon")]
    [InlineData("--read-lobbies", "game", "1.0", "0")]
    [InlineData("--read-lobbies", "game", "1.0", "-5")]
    [InlineData("--something", "game", "1.0", "12")]
    public void Wrong_arguments_do_not_parse(params string[] args)
    {
        Assert.False(MeadowSteamProcess.TryParse(args, out _, out _, out _));
    }

    [Fact]
    public void A_lobby_list_survives_the_trip_through_json()
    {
        MeadowLobbyList? back = MeadowSteamProcess.FromJson(MeadowSteamProcess.ToJson(SampleList()));

        Assert.NotNull(back);
        Assert.True(back.Read);
        Assert.Equal(2, back.Friends.Count);
        Assert.Equal("CoffeeCups", back.Friends[0].Name);
        Assert.Equal("109775249876543210", back.Friends[0].LobbyId);
        Assert.Equal("", back.Friends[1].LobbyId);

        MeadowLobby lobby = Assert.Single(back.Lobbies);
        Assert.Equal("青鸟", lobby.Name);
        Assert.Equal("Story", lobby.Mode);
        Assert.Equal(2, lobby.Players);
        Assert.Equal(6, lobby.MaxPlayers);
        Assert.True(lobby.HasPassword);
        Assert.Equal("White", lobby.Campaign);
        Assert.True(lobby.HasFriend);
        Assert.Equal("CoffeeCups", lobby.FriendName);
        Assert.Equal(new[] { "rwremix", "henpemaz_rainmeadow" }, lobby.Mods.Required);
        Assert.Equal(new[] { "maxi-mol.mousedrag", "fyre.BeastMaster" }, lobby.Mods.Banned);
    }

    [Fact]
    public void A_refusal_survives_the_trip_too()
    {
        MeadowLobbyList? back = MeadowSteamProcess.FromJson(
            MeadowSteamProcess.ToJson(MeadowLobbyList.Refused("Steam did not answer.")));

        Assert.NotNull(back);
        Assert.False(back.Read);
        Assert.Equal("Steam did not answer.", back.Problem);
        Assert.Empty(back.Lobbies);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{\"Lobbies\": [")]
    public void Anything_that_is_not_a_list_reads_as_nothing(string? text)
    {
        Assert.Null(MeadowSteamProcess.FromJson(text));
    }

    [Fact]
    public void The_helper_writes_the_answer_as_json_and_exits_zero()
    {
        var output = new StringWriter();
        string? sawGame = null;
        string? sawVersion = null;
        TimeSpan sawTimeout = TimeSpan.Zero;

        int code = MeadowSteamProcess.RunHelper(
            MeadowSteamProcess.Arguments(@"C:\Games\Rain World", "0.1.15.2", TimeSpan.FromSeconds(12)),
            output,
            (game, version, timeout) =>
            {
                sawGame = game;
                sawVersion = version;
                sawTimeout = timeout;
                return SampleList();
            });

        Assert.Equal(0, code);
        Assert.Equal(@"C:\Games\Rain World", sawGame);
        Assert.Equal("0.1.15.2", sawVersion);
        Assert.Equal(TimeSpan.FromSeconds(12), sawTimeout);

        MeadowLobbyList? back = MeadowSteamProcess.FromJson(output.ToString());
        Assert.NotNull(back);
        Assert.Equal("青鸟", Assert.Single(back.Lobbies).Name);
    }

    [Fact]
    public void The_helper_turns_a_crash_in_the_read_into_a_refusal()
    {
        var output = new StringWriter();

        int code = MeadowSteamProcess.RunHelper(
            MeadowSteamProcess.Arguments(null, "0.1.15.2", TimeSpan.FromSeconds(1)),
            output,
            (_, _, _) => throw new InvalidOperationException("steam_api64.dll went missing"));

        Assert.Equal(0, code);
        MeadowLobbyList? back = MeadowSteamProcess.FromJson(output.ToString());
        Assert.NotNull(back);
        Assert.False(back.Read);
        Assert.Contains("steam_api64.dll went missing", back.Problem);
    }

    [Fact]
    public void The_helper_refuses_wrong_arguments_in_json_as_well()
    {
        var output = new StringWriter();

        int code = MeadowSteamProcess.RunHelper(
            new[] { MeadowSteamProcess.Switch },
            output,
            (_, _, _) => throw new InvalidOperationException("should not be asked"));

        Assert.Equal(2, code);
        MeadowLobbyList? back = MeadowSteamProcess.FromJson(output.ToString());
        Assert.NotNull(back);
        Assert.Contains("wrong arguments", back.Problem);
    }

    [Fact]
    public void A_program_that_is_not_there_is_refused_without_starting_anything()
    {
        MeadowLobbyList list = MeadowSteamProcess.ReadThrough(
            @"C:\nowhere\RainWorldCompanion.exe",
            null,
            "0.1.15.2",
            TimeSpan.FromSeconds(1));

        Assert.False(list.Read);
        Assert.Contains("program file", list.Problem);

        Assert.False(MeadowSteamProcess.ReadThrough(null, null, null, TimeSpan.FromSeconds(1)).Read);
    }
}
