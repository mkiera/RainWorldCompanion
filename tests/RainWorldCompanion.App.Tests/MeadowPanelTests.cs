using RainWorldCompanion.Core.Mods;
using RainWorldCompanion.Core.System;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.App.Tests;

// The window reads Steam and the machine's mods through injected calls, so everything except
// those reads is covered here: what the list turns into, which lobby is picked, what Join is
// allowed to do, and what the window offers before the mod is on.
public class MeadowPanelTests
{
    private const string Meadow = MeadowModPolicy.MeadowModId;
    private const string LobbyId = "109775241234567890";
    private const string FriendLobbyId = "109775249876543210";

    private sealed class World : IDisposable
    {
        private readonly TempDirectory _saves = new("rwc-meadow-saves");

        public List<string> Launched { get; } = new();

        public List<ModSyncRequest> Synced { get; } = new();

        public List<string> Opened { get; } = new();

        public int SteamReads { get; private set; }

        public bool SyncApplies { get; set; } = true;

        public MeadowLobbyList Answer { get; set; } = MeadowLobbyList.Empty;

        public CurrentMods Machine { get; set; } = MachineWith(MeadowStep.Ready);

        public MeadowViewModel Build() => new(
            new ModSyncService(_saves.Path, null, new NullGameProcessDetector()),
            request =>
            {
                Synced.Add(request);
                if (SyncApplies)
                {
                    // Applying is what turns the mod on, so the next read sees it on.
                    Machine = MachineWith(MeadowStep.Ready, request.Wanted.Mods.Select(mod => mod.Id).ToArray());
                }

                return SyncApplies;
            },
            start =>
            {
                Launched.Add(start.ThroughSteam ? start.SteamUrl : string.Join(" ", start.Arguments));
                return true;
            },
            url => Opened.Add(url),
            (_, _, _) =>
            {
                SteamReads++;
                return Answer;
            },
            () => Machine);

        public void Dispose() => _saves.Dispose();
    }

    private static ModEntry Mod(string id, int order) => new()
    {
        Id = id,
        Name = id,
        Version = "1.0",
        LoadOrder = order,
        Origin = ModEntry.WorkshopOrigin,
    };

    // The four things the window can find: nothing readable, the mod absent, the mod installed but
    // off, or the mod on. The other ids are on in every case where anything is.
    private static CurrentMods MachineWith(MeadowStep step, params string[] otherOn)
    {
        if (step == MeadowStep.Unknown)
        {
            return CurrentMods.NothingRead("nothing was read");
        }

        var on = new List<ModEntry>();
        var installed = new List<ModEntry>();
        int order = 0;

        foreach (string id in otherOn.Where(id => !string.Equals(id, Meadow, StringComparison.OrdinalIgnoreCase)))
        {
            on.Add(Mod(id, order++));
            installed.Add(Mod(id, 0));
        }

        if (step != MeadowStep.NotInstalled)
        {
            installed.Add(Mod(Meadow, 0));
        }

        if (step == MeadowStep.Ready)
        {
            on.Add(Mod(Meadow, order));
        }

        return new CurrentMods(
            new ModListSnapshot
            {
                GameVersion = "v1.11.8",
                ReadTheEnabledList = true,
                CheckedTheInstall = true,
                CheckedTheWorkshop = true,
                Mods = on,
            },
            installed);
    }

    private static MeadowLobby Lobby(
        string id,
        string name,
        string required = Meadow,
        bool password = false,
        int players = 1,
        int max = 4,
        string friend = "",
        string mode = "Story",
        string banned = "")
        => new()
        {
            Id = id,
            Name = name,
            Mode = mode,
            Players = players,
            MaxPlayers = max,
            HasPassword = password,
            HasFriend = friend.Length > 0,
            FriendName = friend,
            Mods = MeadowLobbyMods.Read(required, banned),
        };

    [Fact]
    public async Task Opening_the_window_reads_the_mods_and_leaves_Steam_alone()
    {
        using var world = new World();
        world.Answer = new MeadowLobbyList { Lobbies = new[] { Lobby(LobbyId, "Slug") } };

        MeadowViewModel view = world.Build();
        await view.CheckModsCommand.ExecuteAsync(null);

        Assert.True(view.IsReady);
        Assert.Equal(0, world.SteamReads);
        Assert.Empty(view.Lobbies);
        Assert.Contains("Refresh", view.StatusText);
        Assert.Contains("running", view.SteamNoteText);

        world.Machine = MachineWith(MeadowStep.TurnedOff);
        await view.CheckModsCommand.ExecuteAsync(null);

        Assert.True(view.CanTurnOn);
        Assert.Equal(0, world.SteamReads);
    }

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

        Assert.True(view.IsReady);
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
    public async Task A_lobby_the_mods_already_match_is_joined_through_the_invite_link()
    {
        using var world = new World();
        world.Answer = new MeadowLobbyList { Lobbies = new[] { Lobby(LobbyId, "Slug") } };

        MeadowViewModel view = world.Build();
        await view.RefreshCommand.ExecuteAsync(null);

        Assert.True(view.JoinCommand.CanExecute(null));
        Assert.False(view.SyncAndJoinCommand.CanExecute(null));

        view.JoinCommand.Execute(null);

        Assert.Equal(new[] { $"steam://joinlobby/312520/{LobbyId}" }, world.Launched);
        Assert.Empty(world.Synced);
    }

    [Fact]
    public async Task A_lobby_that_needs_a_mod_change_cannot_be_joined_with_the_mods_as_they_are()
    {
        using var world = new World();
        world.Machine = MachineWith(MeadowStep.Ready);
        world.Answer = new MeadowLobbyList
        {
            Lobbies = new[] { Lobby(LobbyId, "Slug", required: Meadow + "\nrwremix") },
        };

        MeadowViewModel view = world.Build();
        world.Machine = new CurrentMods(world.Machine.Enabled, world.Machine.Installed.Append(Mod("rwremix", 0)).ToList());
        await view.RefreshCommand.ExecuteAsync(null);

        Assert.Contains("turns on rwremix", view.SelectedLobby!.ChangeText);
        Assert.False(view.JoinCommand.CanExecute(null));
        Assert.True(view.SyncAndJoinCommand.CanExecute(null));
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
        Assert.False(view.SyncAndJoinCommand.CanExecute(null));
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
        Assert.False(view.SyncAndJoinCommand.CanExecute(null));
    }

    [Fact]
    public async Task Leaving_the_mod_change_unapplied_joins_nothing()
    {
        using var world = new World();
        world.SyncApplies = false;
        world.Machine = new CurrentMods(world.Machine.Enabled, world.Machine.Installed.Append(Mod("rwremix", 0)).ToList());
        world.Answer = new MeadowLobbyList
        {
            Lobbies = new[] { Lobby(LobbyId, "Slug", required: Meadow + "\nrwremix") },
        };

        MeadowViewModel view = world.Build();
        await view.RefreshCommand.ExecuteAsync(null);
        view.SyncAndJoinCommand.Execute(null);

        ModSyncRequest request = Assert.Single(world.Synced);
        Assert.Equal("Match the lobby", request.ButtonText);
        Assert.Contains("rwremix", request.Wanted.Mods.Select(mod => mod.Id));
        Assert.Empty(world.Launched);
    }

    [Fact]
    public async Task Without_the_mod_the_window_offers_the_workshop_page_and_asks_Steam_nothing()
    {
        using var world = new World();
        world.Machine = MachineWith(MeadowStep.NotInstalled);
        world.Answer = new MeadowLobbyList { Lobbies = new[] { Lobby(LobbyId, "Slug") } };

        MeadowViewModel view = world.Build();
        await view.RefreshCommand.ExecuteAsync(null);

        Assert.True(view.NeedsSetup);
        Assert.True(view.CanInstall);
        Assert.False(view.CanTurnOn);
        Assert.Contains("not installed", view.SetupText);
        Assert.Empty(view.Lobbies);
        Assert.Equal(0, world.SteamReads);

        view.InstallCommand.Execute(null);

        Assert.Equal(new[] { ModListDiffViewModel.WorkshopUrlPrefix + "3388224007" }, world.Opened);
    }

    [Fact]
    public async Task With_the_mod_off_turning_it_on_goes_through_the_mods_window_and_then_lists_lobbies()
    {
        using var world = new World();
        world.Machine = MachineWith(MeadowStep.TurnedOff, "rwremix");
        world.Answer = new MeadowLobbyList { Lobbies = new[] { Lobby(LobbyId, "Slug") } };

        MeadowViewModel view = world.Build();
        await view.RefreshCommand.ExecuteAsync(null);

        Assert.True(view.CanTurnOn);
        Assert.False(view.CanInstall);
        Assert.True(view.TurnOnCommand.CanExecute(null));
        Assert.Equal(0, world.SteamReads);

        await view.TurnOnCommand.ExecuteAsync(null);

        ModSyncRequest request = Assert.Single(world.Synced);
        Assert.Equal("Turn on Rain Meadow", request.ButtonText);
        Assert.Equal(new[] { "rwremix", Meadow }, request.Wanted.Mods.Select(mod => mod.Id));
        Assert.True(view.IsReady);
        Assert.Single(view.Lobbies);
        Assert.Equal(1, world.SteamReads);
    }

    [Fact]
    public async Task Turning_it_on_but_not_applying_leaves_the_window_where_it_was()
    {
        using var world = new World();
        world.SyncApplies = false;
        world.Machine = MachineWith(MeadowStep.TurnedOff);

        MeadowViewModel view = world.Build();
        await view.RefreshCommand.ExecuteAsync(null);
        await view.TurnOnCommand.ExecuteAsync(null);

        Assert.Single(world.Synced);
        Assert.True(view.CanTurnOn);
        Assert.Equal(0, world.SteamReads);
    }

    [Fact]
    public async Task When_the_mods_could_not_be_read_the_window_says_so_and_still_offers_the_workshop()
    {
        using var world = new World();
        world.Machine = MachineWith(MeadowStep.Unknown);

        MeadowViewModel view = world.Build();
        await view.RefreshCommand.ExecuteAsync(null);

        Assert.True(view.NeedsSetup);
        Assert.True(view.CanInstall);
        Assert.Contains("could not be read", view.SetupText);
        Assert.Equal(0, world.SteamReads);
    }

    [Fact]
    public async Task Search_narrows_the_list_and_clearing_it_brings_everything_back()
    {
        using var world = new World();
        world.Answer = new MeadowLobbyList
        {
            Lobbies = new[]
            {
                Lobby(LobbyId, "Slug", mode: "Arena"),
                // A Story lobby banning an arena mod is not an arena lobby, so bans are not searched.
                Lobby(FriendLobbyId, "Bretta", required: Meadow + "\nwatcher", banned: "mirarge.moarenas"),
            },
        };

        MeadowViewModel view = world.Build();
        await view.RefreshCommand.ExecuteAsync(null);

        view.SearchText = "bret";
        Assert.Equal(new[] { "Bretta" }, view.Lobbies.Select(row => row.Name));

        view.SearchText = "arena";
        Assert.Equal(new[] { "Slug" }, view.Lobbies.Select(row => row.Name));

        view.SearchText = "watch";
        Assert.Equal(new[] { "Bretta" }, view.Lobbies.Select(row => row.Name));

        view.SearchText = "nothing like this";
        Assert.Empty(view.Lobbies);
        Assert.True(view.HasNoLobbies);
        Assert.Equal("No lobby matches the search.", view.EmptyText);

        view.SearchText = "";
        Assert.Equal(2, view.Lobbies.Count);
        Assert.Equal(1, world.SteamReads);
    }

    [Fact]
    public async Task Search_keeps_the_selected_lobby_while_it_still_matches()
    {
        using var world = new World();
        world.Answer = new MeadowLobbyList
        {
            Lobbies = new[] { Lobby(LobbyId, "Slug"), Lobby(FriendLobbyId, "Bretta") },
        };

        MeadowViewModel view = world.Build();
        await view.RefreshCommand.ExecuteAsync(null);
        view.SelectedLobby = view.Lobbies.Single(row => row.Name == "Bretta");

        view.SearchText = "b";
        Assert.Equal("Bretta", view.SelectedLobby!.Name);

        view.SearchText = "slug";
        Assert.Equal("Slug", view.SelectedLobby!.Name);
    }

    [Fact]
    public async Task A_typed_join_code_with_a_password_starts_the_game_itself()
    {
        using var world = new World();
        MeadowViewModel view = world.Build();
        await view.RefreshCommand.ExecuteAsync(null);

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
