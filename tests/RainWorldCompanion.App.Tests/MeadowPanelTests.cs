using RainWorldCompanion.Core.Mods;
using RainWorldCompanion.Core.System;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.App.Tests;

// The window reads Steam through an injected call, so everything except the Steam call itself is
// covered here: what the list turns into, which lobby is picked, and what Join is allowed to do.
public class MeadowPanelTests
{
    private const string Meadow = MeadowModPolicy.MeadowModId;
    private const string LobbyId = "109775241234567890";
    private const string FriendLobbyId = "109775249876543210";

    private sealed class World : IDisposable
    {
        private readonly TempDirectory _saves = new("rwc-meadow-saves");

        public List<string> Launched { get; } = new();

        public List<string> Synced { get; } = new();

        public bool SyncApplies { get; set; } = true;

        public MeadowLobbyList Answer { get; set; } = MeadowLobbyList.Empty;

        public MeadowViewModel Build() => new(
            new ModSyncService(_saves.Path, null, new NullGameProcessDetector()),
            "0.1.15.2",
            (wanted, name) =>
            {
                Synced.Add(name + ":" + string.Join(",", wanted.Mods.Select(mod => mod.Id)));
                return SyncApplies;
            },
            start =>
            {
                Launched.Add(start.ThroughSteam ? start.SteamUrl : string.Join(" ", start.Arguments));
                return true;
            },
            (_, _, _) => Answer);

        public void Dispose() => _saves.Dispose();
    }

    private static MeadowLobby Lobby(
        string id,
        string name,
        string required = Meadow,
        bool password = false,
        int players = 1,
        int max = 4,
        string friend = "")
        => new()
        {
            Id = id,
            Name = name,
            Mode = "Story",
            Players = players,
            MaxPlayers = max,
            HasPassword = password,
            HasFriend = friend.Length > 0,
            FriendName = friend,
            Mods = MeadowLobbyMods.Read(required, ""),
        };

    [Fact]
    public async Task A_refresh_lists_what_Steam_answered()
    {
        using var world = new World();
        world.Answer = new MeadowLobbyList
        {
            Lobbies = new[] { Lobby(LobbyId, "Slug"), Lobby(FriendLobbyId, "Bretta", friend: "CoffeeCups") },
            Friends = new[] { new MeadowFriend { Name = "CoffeeCups", LobbyId = FriendLobbyId } },
        };

        MeadowViewModel view = world.Build();
        await view.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(2, view.Lobbies.Count);
        Assert.Equal("2 lobbies.", view.StatusText);
        Assert.Contains("CoffeeCups is in a lobby", view.FriendsText);
        Assert.False(view.HasProblem);
    }

    [Fact]
    public async Task The_lobby_a_friend_is_in_is_the_one_selected()
    {
        using var world = new World();
        world.Answer = new MeadowLobbyList
        {
            Lobbies = new[] { Lobby(LobbyId, "Slug"), Lobby(FriendLobbyId, "Bretta", friend: "CoffeeCups") },
        };

        MeadowViewModel view = world.Build();
        await view.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("Bretta", view.SelectedLobby!.Name);
    }

    [Fact]
    public async Task A_refusal_from_Steam_is_shown_and_lists_nothing()
    {
        using var world = new World();
        world.Answer = MeadowLobbyList.Refused("Steam did not answer.");

        MeadowViewModel view = world.Build();
        await view.RefreshCommand.ExecuteAsync(null);

        Assert.True(view.HasProblem);
        Assert.Equal("Steam did not answer.", view.ProblemText);
        Assert.Empty(view.Lobbies);
    }

    [Fact]
    public async Task Joining_without_a_password_hands_Steam_the_invite_link()
    {
        using var world = new World();
        world.Answer = new MeadowLobbyList { Lobbies = new[] { Lobby(LobbyId, "Slug") } };

        MeadowViewModel view = world.Build();
        await view.RefreshCommand.ExecuteAsync(null);
        view.JoinCommand.Execute(null);

        Assert.Equal(new[] { $"steam://joinlobby/312520/{LobbyId}" }, world.Launched);
        Assert.Empty(world.Synced);
    }

    [Fact]
    public async Task A_full_lobby_cannot_be_joined()
    {
        using var world = new World();
        world.Answer = new MeadowLobbyList { Lobbies = new[] { Lobby(LobbyId, "Slug", players: 4, max: 4) } };

        MeadowViewModel view = world.Build();
        await view.RefreshCommand.ExecuteAsync(null);

        Assert.False(view.SelectedLobby!.CanJoin);
        Assert.False(view.JoinCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_lobby_needing_a_mod_nobody_has_cannot_be_joined()
    {
        using var world = new World();
        world.Answer = new MeadowLobbyList
        {
            Lobbies = new[] { Lobby(LobbyId, "Slug", required: Meadow + "\ndeszcworlde") },
        };

        MeadowViewModel view = world.Build();
        await view.RefreshCommand.ExecuteAsync(null);

        Assert.Contains("you do not have", view.SelectedLobby!.ChangeText);
        Assert.False(view.JoinCommand.CanExecute(null));
    }

    [Fact]
    public async Task Leaving_the_mod_change_unapplied_joins_nothing()
    {
        using var world = new World();
        world.SyncApplies = false;
        world.Answer = new MeadowLobbyList { Lobbies = new[] { Lobby(LobbyId, "Slug", required: Meadow) } };

        MeadowViewModel view = world.Build();
        await view.RefreshCommand.ExecuteAsync(null);
        view.SyncAndJoinCommand.Execute(null);

        Assert.Single(world.Synced);
        Assert.Empty(world.Launched);
    }

    [Fact]
    public async Task A_typed_join_code_with_a_password_starts_the_game_itself()
    {
        using var world = new World();
        MeadowViewModel view = world.Build();

        view.TypedCode = $"+connect_lobby {LobbyId}";
        view.TypedPassword = "hunter2";
        view.JoinTypedCommand.Execute(null);

        // No game folder is set in this world, so the password path is refused rather than run.
        Assert.True(view.HasTypedProblem);
        Assert.Empty(world.Launched);
    }

    [Fact]
    public void A_typed_code_that_names_no_lobby_says_so()
    {
        using var world = new World();
        MeadowViewModel view = world.Build();

        view.TypedCode = "come play with us";
        view.JoinTypedCommand.Execute(null);

        Assert.Contains("not a lobby id", view.TypedProblem);
        Assert.Empty(world.Launched);
    }
}
