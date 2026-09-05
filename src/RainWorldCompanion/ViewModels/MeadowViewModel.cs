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
    private static readonly TimeSpan SteamTimeout = TimeSpan.FromSeconds(12);

    private readonly ModSyncService _mods;
    private readonly string? _meadowVersion;
    private readonly Func<ModListSnapshot, string, bool> _syncMods;
    private readonly Func<MeadowStart, bool> _launch;
    private readonly Func<string?, string?, TimeSpan, MeadowLobbyList> _read;

    public MeadowViewModel(
        ModSyncService mods,
        string? meadowVersion,
        Func<ModListSnapshot, string, bool> syncMods,
        Func<MeadowStart, bool> launch,
        Func<string?, string?, TimeSpan, MeadowLobbyList>? read = null)
    {
        _mods = mods ?? throw new ArgumentNullException(nameof(mods));
        _meadowVersion = meadowVersion;
        _syncMods = syncMods ?? throw new ArgumentNullException(nameof(syncMods));
        _launch = launch ?? throw new ArgumentNullException(nameof(launch));
        _read = read ?? ((game, version, timeout) => MeadowSteam.Read(game, version, timeout));
    }

    public ObservableCollection<MeadowLobbyRowViewModel> Lobbies { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(JoinCommand))]
    [NotifyCanExecuteChangedFor(nameof(SyncAndJoinCommand))]
    [NotifyCanExecuteChangedFor(nameof(JoinTypedCommand))]
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
    private string typedCode = "";

    [ObservableProperty]
    private string typedPassword = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTypedProblem))]
    private string typedProblem = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoteAboutPolicy))]
    private string policyNote = "";

    public bool HasSelection => SelectedLobby is not null;

    public bool NeedsPassword => SelectedLobby is { HasPassword: true };

    public bool HasProblem => ProblemText.Length > 0;

    public bool HasFriendsText => FriendsText.Length > 0;

    public bool HasTypedProblem => TypedProblem.Length > 0;

    public bool HasNoteAboutPolicy => PolicyNote.Length > 0;

    public bool HasNoLobbies => Lobbies.Count == 0;

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        ProblemText = "";
        StatusText = "Asking Steam...";

        try
        {
            string? game = _mods.GameInstallPath;
            string? version = _meadowVersion;

            (MeadowLobbyList list, CurrentMods current, MeadowModPolicy policy) = await Task.Run(() =>
            {
                MeadowLobbyList read = _read(game, version, SteamTimeout);
                return (read, _mods.ReadCurrent(), MeadowModPolicy.ReadFrom(game));
            });

            Lobbies.Clear();
            SelectedLobby = null;

            if (!list.Read)
            {
                ProblemText = list.Problem ?? "Steam could not be read.";
                StatusText = "";
                FriendsText = "";
                OnPropertyChanged(nameof(HasNoLobbies));
                return;
            }

            foreach (MeadowLobby lobby in list.Lobbies)
            {
                Lobbies.Add(new MeadowLobbyRowViewModel(
                    lobby,
                    MeadowModMatch.Build(lobby.Mods, policy, current),
                    policy.Read));
            }

            FriendsText = DescribeFriends(list.Friends);
            PolicyNote = policy.Read
                ? ""
                : "Rain Meadow has not written its mod lists yet, so what it would turn off cannot "
                    + "be worked out here. Start the game once and refresh.";
            StatusText = Describe(Lobbies.Count);
            SelectedLobby = Lobbies.FirstOrDefault(row => row.HasFriend) ?? Lobbies.FirstOrDefault();
            OnPropertyChanged(nameof(HasNoLobbies));
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

    [RelayCommand(CanExecute = nameof(CanJoinSelected))]
    private void Join() => Start(SelectedLobby!, syncFirst: false);

    [RelayCommand(CanExecute = nameof(CanSyncSelected))]
    private void SyncAndJoin() => Start(SelectedLobby!, syncFirst: true);

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
            CurrentMods current = _mods.ReadCurrent();
            if (!_syncMods(row.Match.WantedList(current), row.Name))
            {
                StatusText = "The mods were left alone, so nothing was joined.";
                return;
            }
        }

        _launch(start);
    }

    private bool NotBusy() => !IsBusy;

    private bool CanJoinSelected() => !IsBusy && SelectedLobby is { CanJoin: true };

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
