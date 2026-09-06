// Usings sit above the namespace: RainWorldCompanion.Core.System would otherwise shadow System.
using System.Reflection;
using System.Runtime.InteropServices;

using RainWorldCompanion.Core.Mods;

using Steamworks;

namespace RainWorldCompanion.Core.System;

public sealed record MeadowLobby
{
    public string Id { get; init; } = "";

    public string Name { get; init; } = "";

    public string Mode { get; init; } = "";

    public int Players { get; init; }

    public int MaxPlayers { get; init; }

    public bool HasPassword { get; init; }

    public string Campaign { get; init; } = "";

    // True when a friend is in it, which is also the only way a friends-only lobby is ever seen.
    public bool HasFriend { get; init; }

    public string FriendName { get; init; } = "";

    public MeadowLobbyMods Mods { get; init; } = MeadowLobbyMods.None;
}

public sealed record MeadowFriend
{
    public string SteamId { get; init; } = "";

    public string Name { get; init; } = "";

    // Empty when the friend is in Rain World but not in a lobby, which is what single player is.
    public string LobbyId { get; init; } = "";
}

public sealed record MeadowLobbyList
{
    public static MeadowLobbyList Empty { get; } = new();

    public IReadOnlyList<MeadowFriend> Friends { get; init; } = Array.Empty<MeadowFriend>();

    public IReadOnlyList<MeadowLobby> Lobbies { get; init; } = Array.Empty<MeadowLobby>();

    public string? Problem { get; init; }

    public bool Read => Problem is null;

    public static MeadowLobbyList Refused(string problem) => new() { Problem = problem };
}

/// Steam is asked, read and let go inside one call. Holding it open would leave this app
/// registered against Rain World's app id for as long as it is running.
public static class MeadowSteam
{
    public const uint RainWorldAppId = 312520;

    private const string AppIdText = "312520";

    public const string NativeLibraryName = "steam_api64";

    private const string ClientKey = "client";
    private const string ClientPrefix = "Meadow_";
    private const string NameKey = "name";
    private const string ModeKey = "mode";
    private const string ModsKey = "mods";
    private const string BannedModsKey = "banned_mods";
    private const string PasswordKey = "password";
    private const string CampaignKey = "campaign";

    private static readonly object Gate = new();
    private static string? _nativeLibraryFolder;
    private static bool _resolverInstalled;

    /// Blocking, and slow enough to need a worker: Steam answers a lobby list in about a second
    /// and is allowed to take twenty.
    public static MeadowLobbyList Read(
        string? gameInstallPath,
        string? meadowVersion,
        TimeSpan timeout,
        CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(meadowVersion))
        {
            return MeadowLobbyList.Refused(
                "Which Rain Meadow is installed could not be read, and the lobby list is asked for "
                + "by exact version. Open the Mods window to check it is on.");
        }

        lock (Gate)
        {
            if (!TryUseNativeLibrary(gameInstallPath, out string? problem))
            {
                return MeadowLobbyList.Refused(problem!);
            }

            // Steam reads the app id from steam_appid.txt beside the exe or from these, and this
            // app ships no such file: it is not the game, and a stray 312520 in its own folder
            // would be read by anything else started from there.
            Environment.SetEnvironmentVariable("SteamAppId", AppIdText);
            Environment.SetEnvironmentVariable("SteamGameId", AppIdText);

            try
            {
                if (!SteamAPI.Init())
                {
                    return MeadowLobbyList.Refused(
                        "Steam did not answer. Start Steam and sign in, then try again.");
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
            {
                return MeadowLobbyList.Refused("Steam's own library could not be loaded: " + ex.Message);
            }

            try
            {
                return new Session(meadowVersion.Trim(), timeout, cancellation).Run();
            }
            catch (Exception ex)
            {
                return MeadowLobbyList.Refused("Steam could not be read: " + ex.Message);
            }
            finally
            {
                try
                {
                    SteamAPI.Shutdown();
                }
                catch (Exception)
                {
                }
            }
        }
    }

    /// The Steamworks package ships the managed wrapper alone, so the native library is taken from
    /// the player's own Rain World folder rather than shipped beside this app.
    internal static bool TryUseNativeLibrary(string? gameInstallPath, out string? problem)
    {
        problem = null;

        if (string.IsNullOrWhiteSpace(gameInstallPath))
        {
            problem = "The game folder is not set, so Steam's own library cannot be found. Set it in Settings.";
            return false;
        }

        string folder = gameInstallPath.Trim();
        string library;
        try
        {
            library = Path.Combine(folder, NativeLibraryName + ".dll");
        }
        catch (ArgumentException)
        {
            problem = "The game folder in Settings is not a usable path.";
            return false;
        }

        if (!File.Exists(library))
        {
            problem = $"'{folder}' holds no {NativeLibraryName}.dll, so Steam cannot be asked for the lobby list.";
            return false;
        }

        _nativeLibraryFolder = folder;

        if (!_resolverInstalled)
        {
            NativeLibrary.SetDllImportResolver(typeof(SteamAPI).Assembly, Resolve);
            _resolverInstalled = true;
        }

        return true;
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (!string.Equals(name, NativeLibraryName, StringComparison.OrdinalIgnoreCase)
            || _nativeLibraryFolder is null)
        {
            return IntPtr.Zero;
        }

        string library = Path.Combine(_nativeLibraryFolder, name + ".dll");
        return NativeLibrary.TryLoad(library, out IntPtr handle) ? handle : IntPtr.Zero;
    }

    private sealed class Session
    {
        private readonly string _version;
        private readonly TimeSpan _timeout;
        private readonly CancellationToken _cancellation;
        private readonly HashSet<ulong> _dataReady = new();

        private bool _listDone;
        private ulong[] _found = Array.Empty<ulong>();

        public Session(string version, TimeSpan timeout, CancellationToken cancellation)
        {
            _version = version;
            _timeout = timeout;
            _cancellation = cancellation;
        }

        public MeadowLobbyList Run()
        {
            using var listCall = CallResult<LobbyMatchList_t>.Create(OnLobbyList);
            using var dataUpdate = Callback<LobbyDataUpdate_t>.Create(OnLobbyData);

            IReadOnlyList<MeadowFriend> friends = ReadFriends();

            SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
            SteamMatchmaking.AddRequestLobbyListStringFilter(
                ClientKey,
                ClientPrefix + _version,
                ELobbyComparison.k_ELobbyComparisonEqual);
            listCall.Set(SteamMatchmaking.RequestLobbyList());

            Pump(() => _listDone);

            var byId = new Dictionary<ulong, MeadowFriend>();
            foreach (MeadowFriend friend in friends)
            {
                if (ulong.TryParse(friend.LobbyId, out ulong id) && id != 0)
                {
                    byId.TryAdd(id, friend);
                }
            }

            // A friends-only lobby never comes back from RequestLobbyList, so its data is asked
            // for by id instead. Everything from the list already carries its data.
            foreach (ulong id in byId.Keys)
            {
                if (!_found.Contains(id))
                {
                    SteamMatchmaking.RequestLobbyData(new CSteamID(id));
                }
            }

            if (byId.Keys.Any(id => !_found.Contains(id)))
            {
                Pump(() => byId.Keys.Where(id => !_found.Contains(id)).All(_dataReady.Contains));
            }

            var lobbies = new List<MeadowLobby>();
            foreach (ulong id in _found.Concat(byId.Keys.Where(id => !_found.Contains(id))))
            {
                byId.TryGetValue(id, out MeadowFriend? friend);
                MeadowLobby? lobby = ReadLobby(id, friend);
                if (lobby is not null)
                {
                    lobbies.Add(lobby);
                }
            }

            return new MeadowLobbyList
            {
                Friends = friends,
                Lobbies = lobbies
                    .OrderByDescending(lobby => lobby.HasFriend)
                    .ThenByDescending(lobby => lobby.Players)
                    .ThenBy(lobby => lobby.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList(),
            };
        }

        private IReadOnlyList<MeadowFriend> ReadFriends()
        {
            var friends = new List<MeadowFriend>();

            int count = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
            for (int index = 0; index < count; index++)
            {
                CSteamID id = SteamFriends.GetFriendByIndex(index, EFriendFlags.k_EFriendFlagImmediate);
                if (!SteamFriends.GetFriendGamePlayed(id, out FriendGameInfo_t info)
                    || info.m_gameID.m_GameID != RainWorldAppId)
                {
                    continue;
                }

                friends.Add(new MeadowFriend
                {
                    SteamId = id.m_SteamID.ToString(global::System.Globalization.CultureInfo.InvariantCulture),
                    Name = SteamFriends.GetFriendPersonaName(id) ?? "",
                    LobbyId = info.m_steamIDLobby.m_SteamID == 0
                        ? ""
                        : info.m_steamIDLobby.m_SteamID.ToString(global::System.Globalization.CultureInfo.InvariantCulture),
                });
            }

            return friends
                .OrderByDescending(friend => friend.LobbyId.Length > 0)
                .ThenBy(friend => friend.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private MeadowLobby? ReadLobby(ulong id, MeadowFriend? friend)
        {
            var lobby = new CSteamID(id);

            string client = SteamMatchmaking.GetLobbyData(lobby, ClientKey);
            string mods = SteamMatchmaking.GetLobbyData(lobby, ModsKey);

            // A friend's lobby is read by id, so its version is checked here rather than by filter.
            if (client.Length > 0 && !string.Equals(client, ClientPrefix + _version, StringComparison.Ordinal))
            {
                return null;
            }

            return new MeadowLobby
            {
                Id = id.ToString(global::System.Globalization.CultureInfo.InvariantCulture),
                Name = SteamMatchmaking.GetLobbyData(lobby, NameKey),
                Mode = SteamMatchmaking.GetLobbyData(lobby, ModeKey),
                Players = SteamMatchmaking.GetNumLobbyMembers(lobby),
                MaxPlayers = SteamMatchmaking.GetLobbyMemberLimit(lobby),
                HasPassword = bool.TryParse(SteamMatchmaking.GetLobbyData(lobby, PasswordKey), out bool locked) && locked,
                Campaign = SteamMatchmaking.GetLobbyData(lobby, CampaignKey),
                HasFriend = friend is not null,
                FriendName = friend?.Name ?? "",
                Mods = MeadowLobbyMods.Read(mods, SteamMatchmaking.GetLobbyData(lobby, BannedModsKey)),
            };
        }

        private void OnLobbyList(LobbyMatchList_t result, bool failed)
        {
            _listDone = true;
            if (failed)
            {
                return;
            }

            var found = new List<ulong>();
            for (int index = 0; index < result.m_nLobbiesMatching; index++)
            {
                found.Add(SteamMatchmaking.GetLobbyByIndex(index).m_SteamID);
            }

            _found = found.ToArray();
        }

        private void OnLobbyData(LobbyDataUpdate_t update) => _dataReady.Add(update.m_ulSteamIDLobby);

        private void Pump(Func<bool> done)
        {
            DateTimeOffset stop = DateTimeOffset.UtcNow + _timeout;
            while (!done() && DateTimeOffset.UtcNow < stop && !_cancellation.IsCancellationRequested)
            {
                SteamAPI.RunCallbacks();
                Thread.Sleep(50);
            }
        }
    }
}
