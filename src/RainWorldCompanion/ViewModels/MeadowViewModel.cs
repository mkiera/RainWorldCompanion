using System.Collections.ObjectModel;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using RainWorldCompanion.Core.Mods;
using RainWorldCompanion.Core.System;

namespace RainWorldCompanion.ViewModels;

public sealed partial class MeadowLobbyRowViewModel : ObservableObject
{
    public MeadowLobbyRowViewModel(MeadowLobby lobby, MeadowModMatch match, bool policyRead)
    {
        Lobby = lobby;
        Match = match;
        PolicyRead = policyRead;
    }

    public MeadowLobby Lobby { get; }

    public MeadowModMatch Match { get; }

    public bool PolicyRead { get; }

    public string Name => Lobby.Name.Length == 0 ? "Unnamed lobby" : Lobby.Name;

    public string ModeText => Lobby.Mode.Length == 0 ? "Online" : Lobby.Mode;

    public string PlayersText => Lobby.MaxPlayers > 0
        ? string.Format(CultureInfo.CurrentCulture, "{0} of {1}", Lobby.Players, Lobby.MaxPlayers)
        : Lobby.Players.ToString(CultureInfo.CurrentCulture);

    public bool HasPassword => Lobby.HasPassword;

    public bool HasFriend => Lobby.HasFriend;

    public string FriendText => Lobby.HasFriend ? Lobby.FriendName + " is here" : "";

    public bool IsFull => Lobby.MaxPlayers > 0 && Lobby.Players >= Lobby.MaxPlayers;

    public string CampaignText => Lobby.Campaign;

    public bool CanJoin => Match.CanJoinCleanly && !IsFull;

    // Rain Meadow makes the change itself at the door, so a lobby that needs one cannot be joined
    // with the mods left as they are.
    public bool CanJoinAsIs => CanJoin && Match.NothingToDo;

    public string ChangeText
    {
        get
        {
            if (Match.Missing.Count > 0)
            {
                return Missing(Match.Missing.Count) + " you do not have: " + Names(Match.Missing);
            }

            if (Match.NothingToDo)
            {
                return "Your mods already match this lobby.";
            }

            var parts = new List<string>();
            if (Match.Enable.Count > 0)
            {
                parts.Add("turns on " + Names(Match.Enable));
            }

            if (Match.Disable.Count > 0)
            {
                parts.Add("turns off " + Names(Match.Disable));
            }

            if (parts.Count == 0)
            {
                return "Puts your mods in the order this lobby loads them.";
            }

            string text = "Joining " + string.Join(", ", parts) + ".";
            return Match.Reorders ? text + " It also changes their load order." : text;
        }
    }

    public string DetailText
    {
        get
        {
            var parts = new List<string> { ModeText, PlayersText + " players" };
            if (Lobby.Campaign.Length > 0)
            {
                parts.Add(Lobby.Campaign);
            }

            if (HasPassword)
            {
                parts.Add("password");
            }

            return string.Join(", ", parts);
        }
    }

    // What Search looks through: the name, the mode, the campaign, who is there, and the mods the
    // lobby needs, so "watcher" finds the lobbies that need it. The banned list is left out: a
    // Story lobby banning an arena mod is not what somebody typing "arena" is after.
    public bool Matches(string search)
    {
        if (search.Length == 0)
        {
            return true;
        }

        return Contains(Lobby.Name, search)
            || Contains(ModeText, search)
            || Contains(Lobby.Campaign, search)
            || Contains(Lobby.FriendName, search)
            || Lobby.Mods.Required.Any(id => Contains(id, search));
    }

    private static bool Contains(string text, string search) =>
        text.Contains(search, StringComparison.CurrentCultureIgnoreCase);

    private static string Missing(int count) => count == 1 ? "1 mod" : count + " mods";

    private static string Names(IReadOnlyList<string> ids)
    {
        const int Limit = 4;
        if (ids.Count <= Limit)
        {
            return string.Join(", ", ids);
        }

        return string.Join(", ", ids.Take(Limit)) + " and " + (ids.Count - Limit) + " more";
    }
}

public sealed partial class MeadowViewModel : ObservableObject
{
    public const string WorkshopUrl =
        ModListDiffViewModel.WorkshopUrlPrefix + MeadowReadiness.WorkshopId;

    // What the mod is, for somebody who has never had it. The window shows this before it shows
    // anything about lobbies, so it has to stand on its own.
    public const string AboutText =
        "Rain Meadow is the multiplayer mod for Rain World. It adds online story co-op, arena "
        + "matches, and a free roaming mode, and it gives each save slot a second save that it "
        + "uses while you are in a lobby.";

    private static readonly TimeSpan SteamTimeout = TimeSpan.FromSeconds(12);

    private readonly ModSyncService _mods;
    private readonly Func<ModSyncRequest, bool> _syncMods;
    private readonly Func<MeadowStart, bool> _launch;
    private readonly Action<string> _openUrl;
    private readonly Func<string?, string?, TimeSpan, MeadowLobbyList> _read;
    private readonly Func<CurrentMods> _readCurrent;

    private readonly List<MeadowLobbyRowViewModel> _all = new();

    public MeadowViewModel(
        ModSyncService mods,
        Func<ModSyncRequest, bool> syncMods,
        Func<MeadowStart, bool> launch,
        Action<string> openUrl,
        Func<string?, string?, TimeSpan, MeadowLobbyList>? read = null,
        Func<CurrentMods>? readCurrent = null)
    {
        _mods = mods ?? throw new ArgumentNullException(nameof(mods));
        _syncMods = syncMods ?? throw new ArgumentNullException(nameof(syncMods));
        _launch = launch ?? throw new ArgumentNullException(nameof(launch));
        _openUrl = openUrl ?? throw new ArgumentNullException(nameof(openUrl));
        _read = read ?? ((game, version, timeout) => MeadowSteam.Read(game, version, timeout));
        _readCurrent = readCurrent ?? mods.ReadCurrent;
    }

    // The rows Search lets through. The full answer from Steam is kept aside, so narrowing and
    // widening the search costs nothing and never asks Steam again.
    public ObservableCollection<MeadowLobbyRowViewModel> Lobbies { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(JoinCommand))]
    [NotifyCanExecuteChangedFor(nameof(SyncAndJoinCommand))]
    [NotifyCanExecuteChangedFor(nameof(JoinTypedCommand))]
    [NotifyCanExecuteChangedFor(nameof(TurnOnCommand))]
    private bool isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(NeedsPassword))]
    [NotifyCanExecuteChangedFor(nameof(JoinCommand))]
    [NotifyCanExecuteChangedFor(nameof(SyncAndJoinCommand))]
    private MeadowLobbyRowViewModel? selectedLobby;

    [ObservableProperty]
    private string statusText = "Refresh to ask Steam which lobbies are up.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    private string problemText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFriendsText))]
    private string friendsText = "";

    [ObservableProperty]
    private string lobbyPassword = "";

    [ObservableProperty]
    private string searchText = "";

    [ObservableProperty]
    private string typedCode = "";

    [ObservableProperty]
    private string typedPassword = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTypedProblem))]
    private string typedProblem = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoteAboutPolicy))]
    private string policyNote = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    [NotifyPropertyChangedFor(nameof(NeedsSetup))]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyPropertyChangedFor(nameof(CanTurnOn))]
    [NotifyPropertyChangedFor(nameof(SetupText))]
    [NotifyCanExecuteChangedFor(nameof(TurnOnCommand))]
    private MeadowStep step = MeadowStep.Ready;

    public string AboutMeadowText => AboutText;

    public bool IsReady => Step == MeadowStep.Ready;

    public bool NeedsSetup => Step != MeadowStep.Ready;

    public bool CanInstall => Step is MeadowStep.NotInstalled or MeadowStep.Unknown;

    public bool CanTurnOn => Step == MeadowStep.TurnedOff;

    public string SetupText => Step switch
    {
        MeadowStep.TurnedOff =>
            "You have Rain Meadow, but the game has it turned off, so there is nothing to join "
            + "yet. Turn it on, then refresh.",
        MeadowStep.NotInstalled =>
            "Rain Meadow is not installed. Subscribe to it on the Steam Workshop, let Steam "
            + "download it, then refresh.",
        _ =>
            "Which mods you have could not be read, so whether Rain Meadow is here is unknown. "
            + "Check the game folder in Settings, then refresh.",
    };

    public bool HasSelection => SelectedLobby is not null;

    public bool NeedsPassword => SelectedLobby is { HasPassword: true };

    public bool HasProblem => ProblemText.Length > 0;

    public bool HasFriendsText => FriendsText.Length > 0;

    public bool HasTypedProblem => TypedProblem.Length > 0;

    public bool HasNoteAboutPolicy => PolicyNote.Length > 0;

    public bool HasNoLobbies => Lobbies.Count == 0;

    public string EmptyText => _all.Count == 0
        ? "No lobbies listed. Refresh, or paste a join code below."
        : "No lobby matches the search.";

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        ProblemText = "";
        StatusText = "Asking Steam...";

        try
        {
            string? game = _mods.GameInstallPath;

            // What the machine has decides both whether there is anything to show and which
            // version the lobby list is asked for, so it is read before Steam is touched.
            CurrentMods current = await Task.Run(_readCurrent);
            MeadowReadiness readiness = MeadowReadiness.From(current);

            _all.Clear();
            Step = readiness.Step;

            if (readiness.Step != MeadowStep.Ready)
            {
                StatusText = "";
                FriendsText = "";
                PolicyNote = "";
                ShowMatching();
                return;
            }

            string? version = readiness.Version;

            (MeadowLobbyList list, MeadowModPolicy policy) = await Task.Run(() =>
                (_read(game, version, SteamTimeout), MeadowModPolicy.ReadFrom(game)));

            if (!list.Read)
            {
                ProblemText = list.Problem ?? "Steam could not be read.";
                StatusText = "";
                FriendsText = "";
                ShowMatching();
                return;
            }

            foreach (MeadowLobby lobby in list.Lobbies)
            {
                _all.Add(new MeadowLobbyRowViewModel(
                    lobby,
                    MeadowModMatch.Build(lobby.Mods, policy, current),
                    policy.Read));
            }

            FriendsText = DescribeFriends(list.Friends);
            PolicyNote = policy.Read
                ? ""
                : "Rain Meadow has not written its mod lists yet, so what it would turn off cannot "
                    + "be worked out here. Start the game once and refresh.";
            StatusText = Describe(_all.Count);
            ShowMatching();
        }
        catch (Exception ex)
        {
            ProblemText = "The lobby list could not be read: " + ex.Message;
            StatusText = "";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanJoinSelectedAsIs))]
    private void Join() => Start(SelectedLobby!, syncFirst: false);

    [RelayCommand(CanExecute = nameof(CanSyncSelected))]
    private void SyncAndJoin() => Start(SelectedLobby!, syncFirst: true);

    [RelayCommand]
    private void Install() => _openUrl(WorkshopUrl);

    // Goes through the Mods window like every other mod change, then looks again, so the window
    // moves on to the lobby list by itself once the mod is on.
    [RelayCommand(CanExecute = nameof(CanTurnOnNow))]
    private async Task TurnOnAsync()
    {
        CurrentMods current = _readCurrent();
        bool applied = _syncMods(new ModSyncRequest(
            MeadowReadiness.TurnOn(current),
            "Turning on Rain Meadow.",
            "Turn on Rain Meadow",
            "Before turning on Rain Meadow"));

        if (applied)
        {
            await RefreshAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private void JoinTyped()
    {
        TypedProblem = "";

        MeadowJoin? join = MeadowJoin.Read(TypedCode, out string? problem);
        if (join is null)
        {
            TypedProblem = problem ?? "That is not a lobby.";
            return;
        }

        MeadowStart start = join.WithPassword(TypedPassword).Start(_mods.GameInstallPath);
        if (!start.CanRun)
        {
            TypedProblem = start.Problem!;
            return;
        }

        _launch(start);
    }

    partial void OnSearchTextChanged(string value) => ShowMatching();

    private void ShowMatching()
    {
        string search = SearchText.Trim();
        MeadowLobbyRowViewModel? keep = SelectedLobby;

        Lobbies.Clear();
        foreach (MeadowLobbyRowViewModel row in _all)
        {
            if (row.Matches(search))
            {
                Lobbies.Add(row);
            }
        }

        SelectedLobby = keep is not null && Lobbies.Contains(keep)
            ? keep
            : Lobbies.FirstOrDefault(row => row.HasFriend) ?? Lobbies.FirstOrDefault();

        OnPropertyChanged(nameof(HasNoLobbies));
        OnPropertyChanged(nameof(EmptyText));
    }

    private void Start(MeadowLobbyRowViewModel row, bool syncFirst)
    {
        ProblemText = "";

        MeadowJoin? join = MeadowJoin.Read(row.Lobby.Id, out string? problem);
        if (join is null)
        {
            ProblemText = problem ?? "That lobby cannot be joined.";
            return;
        }

        join = join.WithPassword(row.HasPassword ? LobbyPassword : "");
        MeadowStart start = join.Start(_mods.GameInstallPath);
        if (!start.CanRun)
        {
            ProblemText = start.Problem!;
            return;
        }

        if (syncFirst)
        {
            CurrentMods current = _readCurrent();
            bool applied = _syncMods(new ModSyncRequest(
                row.Match.WantedList(current),
                $"Matching the mods \"{row.Name}\" needs.",
                "Match the lobby",
                $"Before joining \"{row.Name}\""));

            if (!applied)
            {
                StatusText = "The mods were left alone, so nothing was joined.";
                return;
            }
        }

        _launch(start);
    }

    private bool NotBusy() => !IsBusy;

    private bool CanTurnOnNow() => !IsBusy && CanTurnOn;

    private bool CanJoinSelectedAsIs() => !IsBusy && SelectedLobby is { CanJoinAsIs: true };

    private bool CanSyncSelected() => !IsBusy && SelectedLobby is { CanJoin: true, Match.NothingToDo: false };

    private static string Describe(int count) => count switch
    {
        0 => "No lobbies are up on this version of Rain Meadow.",
        1 => "1 lobby.",
        _ => count + " lobbies.",
    };

    private static string DescribeFriends(IReadOnlyList<MeadowFriend> friends)
    {
        if (friends.Count == 0)
        {
            return "";
        }

        string[] inLobby = friends.Where(friend => friend.LobbyId.Length > 0).Select(friend => friend.Name).ToArray();
        string[] playing = friends.Where(friend => friend.LobbyId.Length == 0).Select(friend => friend.Name).ToArray();

        var parts = new List<string>();
        if (inLobby.Length > 0)
        {
            parts.Add(string.Join(", ", inLobby) + (inLobby.Length == 1 ? " is in a lobby" : " are in lobbies"));
        }

        if (playing.Length > 0)
        {
            parts.Add(string.Join(", ", playing) + (playing.Length == 1 ? " is playing" : " are playing"));
        }

        return string.Join(". ", parts) + ".";
    }
}
