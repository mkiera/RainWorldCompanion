using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RainWorldCompanion.LiveProtocol;

namespace RainWorldCompanion.Core.LogStreaming;

internal sealed record LogStreamTelemetryRecord(string FileId, byte[] Data);

internal sealed class LogStreamTelemetryRecorder(string appVersion)
{
    public const string EventFileId = "Companion/events.jsonl";
    public const string MeadowNativeFileId = "Companion/meadow-native.jsonl";
    public const string DeepTraceFileId = "Companion/deep-trace.jsonl";
    public const string MeadowNativeDeepFileId = "Companion/meadow-native-deep.jsonl";
    private const int MaximumDeepTracePlayers = 32;
    private static readonly TimeSpan NativeNetworkInterval = TimeSpan.FromSeconds(1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _appVersion = Text(appVersion, 64);
    private SnapshotState? _previous;
    private bool _connected;
    private DateTimeOffset _lastHitch;
    private DateTimeOffset _lastNativeNetworkSample;
    private DateTimeOffset _lastPerformanceSample;

    public void Reset()
    {
        _previous = null;
        _connected = false;
        _lastHitch = default;
        _lastNativeNetworkSample = default;
        _lastPerformanceSample = default;
    }

    public IReadOnlyList<LogStreamTelemetryRecord> Observe(LiveSnapshot? snapshot, DateTimeOffset now, bool recordPerformance = true)
    {
        var records = new List<LogStreamTelemetryRecord>();
        if (snapshot is null)
        {
            if (_connected && _previous is { } previous)
                records.Add(Event(Record(now, "connection-lost", previous, new { })));
            _connected = false;
            return records;
        }

        var current = SnapshotState.From(snapshot);
        bool newSession = _previous is null
            || !string.Equals(_previous.SessionId, current.SessionId, StringComparison.Ordinal);
        if (newSession)
        {
            records.Add(Event(Record(now, "session-start", current, new
            {
                appVersion = _appVersion,
                gameVersion = current.GameVersion,
                gameHookVersion = current.ModVersion,
                current.Campaign,
                current.Timeline,
                current.State,
                current.Process,
                current.IsOnline,
                current.IsHost,
                current.Cycle,
                current.Karma,
                current.KarmaCap,
                enabledExpansions = current.EnabledExpansions,
                activeMods = current.ActiveMods,
                activeModsTruncated = current.ActiveModsTruncated,
            })));
            foreach (var player in EventPlayers(current).OrderBy(player => player.Id, StringComparer.Ordinal))
                records.Add(Event(PlayerRecord(now, "player-present", current, player, null)));
        }
        else
        {
            SnapshotState previous = _previous!;
            if (!_connected) records.Add(Event(Record(now, "connection-restored", current, new { })));
            if (!string.Equals(previous.GameplayId, current.GameplayId, StringComparison.Ordinal))
                records.Add(Event(Record(now, current.GameplayId.Length == 0 ? "gameplay-ended" : "gameplay-started", current,
                    new { previousGameplayId = EmptyToNull(previous.GameplayId) })));
            if (!string.Equals(previous.Process, current.Process, StringComparison.Ordinal))
                records.Add(Event(Record(now, "process-changed", current, new { previous = previous.Process, current = current.Process })));
            if (!string.Equals(previous.State, current.State, StringComparison.Ordinal))
                records.Add(Event(Record(now, "game-state-changed", current, new { previous = previous.State, current = current.State })));
            if (!string.Equals(previous.Campaign, current.Campaign, StringComparison.Ordinal)
                || !string.Equals(previous.Timeline, current.Timeline, StringComparison.Ordinal))
                records.Add(Event(Record(now, "campaign-changed", current, new
                {
                    previousCampaign = previous.Campaign,
                    currentCampaign = current.Campaign,
                    previousTimeline = previous.Timeline,
                    currentTimeline = current.Timeline,
                })));
            if (previous.Cycle != current.Cycle || previous.Karma != current.Karma || previous.KarmaCap != current.KarmaCap)
                records.Add(Event(Record(now, "progression-changed", current, new
                {
                    previousCycle = previous.Cycle,
                    currentCycle = current.Cycle,
                    previousKarma = previous.Karma,
                    currentKarma = current.Karma,
                    previousKarmaCap = previous.KarmaCap,
                    currentKarmaCap = current.KarmaCap,
                })));
            if (previous.AllowHostControl != current.AllowHostControl)
                records.Add(Event(Record(now, "host-control-changed", current, new { enabled = current.AllowHostControl })));
            if (previous.IsHost != current.IsHost)
                records.Add(Event(Record(now, "local-host-role-changed", current, new { isHost = current.IsHost })));
            if (!previous.EnabledExpansions.SequenceEqual(current.EnabledExpansions, StringComparer.Ordinal))
                records.Add(Event(Record(now, "expansions-changed", current, new { enabledExpansions = current.EnabledExpansions })));
            if (previous.ActiveModsTruncated != current.ActiveModsTruncated
                || !previous.ActiveMods.SequenceEqual(current.ActiveMods))
                records.Add(Event(Record(now, "active-mods-changed", current, new
                {
                    previous = previous.ActiveMods,
                    current = current.ActiveMods,
                    current.ActiveModsTruncated,
                })));
            if (current.CommandResult is { } command
                && !string.Equals(previous.CommandResult?.Id, command.Id, StringComparison.Ordinal))
                records.Add(Event(Record(now, "companion-action-result", current, new
                {
                    commandId = command.Id,
                    command.Success,
                    message = command.Message,
                })));
            if (current.HostActionText.Length > 0
                && !string.Equals(previous.HostActionText, current.HostActionText, StringComparison.Ordinal))
                records.Add(Event(Record(now, "rain-meadow-host-action", current, new { message = current.HostActionText })));
            if (current.UnscaledDeltaSeconds >= 0.25f && now - _lastHitch >= TimeSpan.FromSeconds(5))
            {
                _lastHitch = now;
                records.Add(Event(Record(now, "frame-hitch", current, new
                {
                    durationMilliseconds = Math.Round(current.UnscaledDeltaSeconds * 1000, 1),
                    current.Frame,
                    current.ManagedMemoryBytes,
                })));
            }
            ComparePlayers(previous, current, now, records);
        }

        if (recordPerformance && snapshot.Trace is not null && (newSession || now - _lastPerformanceSample >= TimeSpan.FromSeconds(1)))
        {
            records.Add(Event(Record(now, "performance-sample", current, new
            {
                snapshot.State,
                snapshot.GameplayId,
                trace = snapshot.Trace,
                roomId = snapshot.Players.FirstOrDefault(player => player.IsLocal)?.RoomId
            })));
            _lastPerformanceSample = now;
        }
        ObserveMeadow(_previous?.Meadow, current, now, newSession, records);
        _previous = current;
        _connected = true;
        return records;
    }

    public IReadOnlyList<LogStreamTelemetryRecord> DeepTrace(LiveSnapshot snapshot, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var state = SnapshotState.From(snapshot);
        int playerCount = state.IsOnline
            ? state.Players.Values.Count(player => player.IsLocal)
            : state.Players.Count;
        var detailedPlayers = state.Players.Values
            .Where(player => !state.IsOnline || player.IsLocal)
            .OrderBy(player => player.Id, StringComparer.Ordinal)
            .Take(MaximumDeepTracePlayers)
            .Select(player => new
            {
                player.Id,
                player.Name,
                player.IsLocal,
                player.IsHost,
                player.RoomId,
                player.Region,
                player.Dead,
                player.Trace,
            })
            .ToArray();
        var records = new List<LogStreamTelemetryRecord>
        {
            new(DeepTraceFileId, JsonLine(new
            {
                schemaVersion = 1,
                timestampUtc = now,
                kind = "deep-trace",
                sessionId = state.SessionId,
                gameplayId = EmptyToNull(state.GameplayId),
                state = state.State,
                campaign = state.Campaign,
                timeline = state.Timeline,
                trace = snapshot.Trace,
                playerCount,
                playersTruncated = playerCount > detailedPlayers.Length,
                players = detailedPlayers,
            }))
        };
        if (state.Meadow is { } meadow)
        {
            records.Add(new(MeadowNativeDeepFileId, NativeRecord(now, "native-deep-sample", state, meadow, new
            {
                meadow.ConfigurationTruncated,
                meadow.RosterTruncated,
                peers = meadow.Peers.Values.OrderBy(peer => peer.LobbyPeerId)
                    .Select(peer => NativePeer(state, peer, true)).ToArray(),
                avatars = meadow.Avatars.Values.OrderBy(avatar => avatar.Id, StringComparer.Ordinal)
                    .Select(avatar => NativeAvatar(state, avatar, true)).ToArray(),
            })));
        }
        return records;
    }

    public LogStreamTelemetryRecord CompanionAction(
        DateTimeOffset now,
        string phase,
        string action,
        string? playerId,
        string? roomId,
        string? region,
        bool? success = null,
        string? message = null)
    {
        SnapshotState current = _previous ?? SnapshotState.Empty;
        return Event(Record(now, "companion-action-" + Text(phase, 24), current, new
        {
            action = Text(action, 32),
            playerId = OptionalText(playerId, 160),
            roomId = OptionalText(roomId, 100),
            region = OptionalText(region, 20),
            success,
            message = OptionalText(message, 512),
        }));
    }

    private void ObserveMeadow(
        MeadowState? previous,
        SnapshotState current,
        DateTimeOffset now,
        bool newSession,
        ICollection<LogStreamTelemetryRecord> records)
    {
        MeadowState? meadow = current.Meadow;
        if (meadow == null)
        {
            if (previous != null)
                records.Add(Native(NativeRecord(now, "lobby-observation-ended", current, previous, new { })));
            _lastNativeNetworkSample = default;
            return;
        }

        bool newLobby = newSession || previous == null
            || !string.Equals(previous.LobbyId, meadow.LobbyId, StringComparison.Ordinal);
        if (newLobby)
        {
            records.Add(Native(NativeRecord(now, "lobby-observation-started", current, meadow, new
            {
                meadow.GameMode,
                meadow.Timeline,
                meadow.RequiredMods,
                meadow.BannedMods,
                meadow.WhitelistMode,
                meadow.CheatsEnabled,
                meadow.ConfigurationTruncated,
                meadow.RosterTruncated,
                lobbyOptions = meadow.LobbyOptions,
            })));
            foreach (var peer in meadow.Peers.Values.OrderBy(peer => peer.LobbyPeerId))
                records.Add(Native(NativeRecord(now, "peer-present", current, meadow, NativePeer(current, peer, false))));
            foreach (var avatar in meadow.Avatars.Values.OrderBy(avatar => avatar.Id, StringComparer.Ordinal))
                records.Add(Native(NativeRecord(now, "avatar-present", current, meadow, NativeAvatar(current, avatar, false))));
            _lastNativeNetworkSample = default;
        }
        else
        {
            CompareMeadow(previous!, current, meadow, now, records);
        }

        if (_lastNativeNetworkSample == default || now - _lastNativeNetworkSample >= NativeNetworkInterval)
        {
            records.Add(Native(NativeRecord(now, "network-sample", current, meadow, new
            {
                peers = meadow.Peers.Values.OrderBy(peer => peer.LobbyPeerId)
                    .Select(peer => NativeNetworkPeer(current, peer)).ToArray()
            })));
            _lastNativeNetworkSample = now;
        }
    }

    private static void CompareMeadow(
        MeadowState previous,
        SnapshotState current,
        MeadowState meadow,
        DateTimeOffset now,
        ICollection<LogStreamTelemetryRecord> records)
    {
        if (!previous.RequiredMods.SequenceEqual(meadow.RequiredMods, StringComparer.Ordinal)
            || !previous.BannedMods.SequenceEqual(meadow.BannedMods, StringComparer.Ordinal)
            || previous.WhitelistMode != meadow.WhitelistMode || previous.CheatsEnabled != meadow.CheatsEnabled
            || previous.ConfigurationTruncated != meadow.ConfigurationTruncated
            || !previous.LobbyOptions.SequenceEqual(meadow.LobbyOptions))
            records.Add(Native(NativeRecord(now, "lobby-settings-changed", current, meadow, new
            {
                meadow.RequiredMods,
                meadow.BannedMods,
                meadow.WhitelistMode,
                meadow.CheatsEnabled,
                meadow.ConfigurationTruncated,
                lobbyOptions = meadow.LobbyOptions,
            })));

        if (previous.RosterTruncated != meadow.RosterTruncated)
            records.Add(Native(NativeRecord(now, "roster-observation-limit-changed", current, meadow, new
            {
                meadow.RosterTruncated,
            })));

        foreach (var removed in previous.Peers.Values.Where(peer => !meadow.Peers.ContainsKey(peer.SteamId)))
            records.Add(Native(NativeRecord(now, "peer-left", current, meadow, NativePeer(current, removed, false))));
        foreach (var peer in meadow.Peers.Values)
        {
            if (!previous.Peers.TryGetValue(peer.SteamId, out var old))
            {
                records.Add(Native(NativeRecord(now, "peer-joined", current, meadow, NativePeer(current, peer, false))));
                continue;
            }
            if (old.IsHost != peer.IsHost)
                records.Add(Native(NativeRecord(now, "peer-host-role-changed", current, meadow, new
                {
                    subjectId = Subject(current, peer.SteamId), peer.DisplayName, peer.LobbyPeerId, peer.IsHost
                })));
            if (old.InGame != peer.InGame || old.EnteringChat != peer.EnteringChat || old.IsSpectating != peer.IsSpectating)
                records.Add(Native(NativeRecord(now, "peer-client-state-changed", current, meadow, new
                {
                    subjectId = Subject(current, peer.SteamId), peer.DisplayName, peer.LobbyPeerId,
                    previousInGame = old.InGame, currentInGame = peer.InGame,
                    previousEnteringChat = old.EnteringChat, currentEnteringChat = peer.EnteringChat,
                    previousSpectating = old.IsSpectating, currentSpectating = peer.IsSpectating,
                })));
            if (old.SupportsGameHookPackets != peer.SupportsGameHookPackets)
                records.Add(Native(NativeRecord(now, "peer-game-hook-capability-changed", current, meadow, new
                {
                    subjectId = Subject(current, peer.SteamId), peer.DisplayName, peer.LobbyPeerId,
                    supportsGameHookPackets = peer.SupportsGameHookPackets,
                })));
            if (old.StoryReadyForWin != peer.StoryReadyForWin
                || old.StoryReadyForTransition != peer.StoryReadyForTransition || old.StoryDead != peer.StoryDead)
                records.Add(Native(NativeRecord(now, "peer-story-state-changed", current, meadow, new
                {
                    subjectId = Subject(current, peer.SteamId), peer.DisplayName, peer.LobbyPeerId,
                    peer.StoryReadyForWin, peer.StoryReadyForTransition, peer.StoryDead,
                })));
            if (old.AvatarCount != peer.AvatarCount || !old.AvatarIds.SequenceEqual(peer.AvatarIds, StringComparer.Ordinal))
                records.Add(Native(NativeRecord(now, "peer-avatars-changed", current, meadow, new
                {
                    subjectId = Subject(current, peer.SteamId), peer.DisplayName, peer.LobbyPeerId,
                    peer.AvatarCount, observedAvatarCount = peer.AvatarIds.Length,
                })));
        }

        foreach (var removed in previous.Avatars.Where(avatar => !meadow.Avatars.ContainsKey(avatar.Key)))
            records.Add(Native(NativeRecord(now, "avatar-unavailable", current, meadow,
                NativeAvatar(current, removed.Value, false))));
        foreach (var entry in meadow.Avatars)
        {
            PlayerState avatar = entry.Value;
            if (!previous.Avatars.TryGetValue(entry.Key, out var old))
            {
                records.Add(Native(NativeRecord(now, "avatar-available", current, meadow, NativeAvatar(current, avatar, false))));
                continue;
            }
            if (!string.Equals(old.RoomId, avatar.RoomId, StringComparison.Ordinal)
                || !string.Equals(old.Region, avatar.Region, StringComparison.Ordinal)
                || !string.Equals(old.NativeLocationAvailability, avatar.NativeLocationAvailability, StringComparison.Ordinal))
                records.Add(Native(NativeRecord(now, "avatar-location-changed", current, meadow, new
                {
                    subjectId = Subject(current, avatar.MeadowSteamId),
                    avatarId = Avatar(current, avatar.MeadowAvatarId), avatar.Name,
                    previousRoom = EmptyToNull(old.RoomId), currentRoom = EmptyToNull(avatar.RoomId),
                    previousRegion = EmptyToNull(old.Region), currentRegion = EmptyToNull(avatar.Region),
                    previousAvailability = EmptyToNull(old.NativeLocationAvailability),
                    currentAvailability = EmptyToNull(avatar.NativeLocationAvailability),
                })));
            if (old.Dead != avatar.Dead)
                records.Add(Native(NativeRecord(now, "avatar-life-state-changed", current, meadow, new
                {
                    subjectId = Subject(current, avatar.MeadowSteamId), avatarId = Avatar(current, avatar.MeadowAvatarId),
                    avatar.Name, previousDead = old.Dead, currentDead = avatar.Dead,
                })));
            if (old.NativeEntityAvailable != avatar.NativeEntityAvailable
                || old.Trace?.Realized != avatar.Trace?.Realized || old.Trace?.InShortcut != avatar.Trace?.InShortcut
                || old.Trace?.SlatedForDeletion != avatar.Trace?.SlatedForDeletion)
                records.Add(Native(NativeRecord(now, "avatar-entity-state-changed", current, meadow,
                    NativeAvatar(current, avatar, true))));
            if (old.InDen != avatar.InDen)
                records.Add(Native(NativeRecord(now, "avatar-den-state-changed", current, meadow, new
                {
                    subjectId = Subject(current, avatar.MeadowSteamId),
                    avatarId = Avatar(current, avatar.MeadowAvatarId),
                    avatar.Name,
                    previousInDen = old.InDen,
                    currentInDen = avatar.InDen,
                })));
        }
    }

    private static void ComparePlayers(
        SnapshotState previous,
        SnapshotState current,
        DateTimeOffset now,
        ICollection<LogStreamTelemetryRecord> records)
    {
        var previousPlayers = EventPlayers(previous).ToDictionary(player => player.Id, StringComparer.Ordinal);
        var currentPlayers = EventPlayers(current).ToDictionary(player => player.Id, StringComparer.Ordinal);
        foreach (var removed in previousPlayers.Values.Where(player => !currentPlayers.ContainsKey(player.Id)))
            records.Add(Event(PlayerRecord(now, "player-left", current, removed, null)));
        foreach (var player in currentPlayers.Values)
        {
            if (!previousPlayers.TryGetValue(player.Id, out var old))
            {
                records.Add(Event(PlayerRecord(now, "player-joined", current, player, null)));
                continue;
            }
            if (!string.Equals(old.RoomId, player.RoomId, StringComparison.Ordinal)
                || !string.Equals(old.Region, player.Region, StringComparison.Ordinal))
                records.Add(Event(PlayerRecord(now, "room-changed", current, player, new
                {
                    previousRoom = EmptyToNull(old.RoomId),
                    currentRoom = EmptyToNull(player.RoomId),
                    previousRegion = EmptyToNull(old.Region),
                    currentRegion = EmptyToNull(player.Region),
                })));
            if (old.Dead != player.Dead)
                records.Add(Event(PlayerRecord(now, player.Dead == true ? "player-died"
                    : player.Dead == false ? "player-revived" : "player-life-state-unknown", current, player,
                    new { previousDead = old.Dead, currentDead = player.Dead })));
            if (old.IsHost != player.IsHost)
                records.Add(Event(PlayerRecord(now, "player-host-role-changed", current, player, new { isHost = player.IsHost })));
            if (old.Trace?.Realized != player.Trace?.Realized)
                records.Add(Event(PlayerRecord(now, player.Trace?.Realized == true ? "player-realized" : "player-unrealized",
                    current, player, null)));
            if (old.Trace?.InShortcut != player.Trace?.InShortcut)
                records.Add(Event(PlayerRecord(now, player.Trace?.InShortcut == true ? "shortcut-entered" : "shortcut-exited",
                    current, player, null)));
            if (old.Trace?.SlatedForDeletion != true && player.Trace?.SlatedForDeletion == true)
                records.Add(Event(PlayerRecord(now, "player-marked-for-deletion", current, player, null)));
        }
    }

    private static IEnumerable<PlayerState> EventPlayers(SnapshotState state)
        => state.IsOnline ? state.Players.Values.Where(player => player.IsLocal) : state.Players.Values;

    private static object NativePeer(SnapshotState state, MeadowPeerState peer, bool detailed) => new
    {
        subjectId = Subject(state, peer.SteamId),
        peer.DisplayName,
        peer.LobbyPeerId,
        peer.IsLocal,
        peer.IsHost,
        peer.SupportsGameHookPackets,
        peer.InGame,
        peer.EnteringChat,
        peer.AvatarCount,
        observedAvatarCount = peer.AvatarIds.Length,
        peer.StoryReadyForWin,
        peer.StoryReadyForTransition,
        peer.StoryDead,
        peer.IsSpectating,
        network = detailed ? NativeNetworkPeer(state, peer) : null,
    };

    private static object NativeNetworkPeer(SnapshotState state, MeadowPeerState peer) => new
    {
        subjectId = Subject(state, peer.SteamId),
        peer.DisplayName,
        peer.LobbyPeerId,
        peer.IsLocal,
        peer.IsHost,
        peer.PingMilliseconds,
        peer.IncomingBytesPerSecond,
        peer.OutgoingBytesPerSecond,
        peer.RemoteTick,
        peer.LatestAcknowledgedTick,
        peer.OutgoingEventCount,
        peer.OutgoingStateCount,
        peer.NeedsAcknowledgement,
        peer.EventsRead,
        peer.StatesRead,
        peer.EventsWritten,
        peer.StatesWritten,
        steamConnection = peer.Connection,
    };

    private static object NativeAvatar(SnapshotState state, PlayerState avatar, bool detailed) => new
    {
        subjectId = Subject(state, avatar.MeadowSteamId),
        avatarId = Avatar(state, avatar.MeadowAvatarId),
        avatar.Name,
        avatar.IsLocal,
        avatar.IsHost,
        roomId = EmptyToNull(avatar.RoomId),
        region = EmptyToNull(avatar.Region),
        avatar.Dead,
        entityAvailable = avatar.NativeEntityAvailable,
        locationAvailability = EmptyToNull(avatar.NativeLocationAvailability),
        avatar.InDen,
        realized = detailed ? avatar.Trace?.Realized : null,
        inShortcut = detailed ? avatar.Trace?.InShortcut : null,
        slatedForDeletion = detailed ? avatar.Trace?.SlatedForDeletion : null,
        abstractNode = detailed ? avatar.Trace?.AbstractNode : null,
    };

    private static byte[] NativeRecord(DateTimeOffset now, string kind, SnapshotState state, MeadowState meadow, object details)
        => JsonLine(new
        {
            schemaVersion = 1,
            timestampUtc = now,
            kind,
            sessionId = EmptyToNull(state.SessionId),
            gameplayId = EmptyToNull(state.GameplayId),
            source = "rain-meadow-native",
            observation = "local-client-observed",
            observerId = Subject(state, meadow.ObserverSteamId),
            lobbyId = Pseudonym(state.SessionId, "lobby", meadow.LobbyId),
            details,
        });

    private static byte[] PlayerRecord(
        DateTimeOffset now,
        string kind,
        SnapshotState state,
        PlayerState player,
        object? details)
        => Record(now, kind, state, new
        {
            playerId = player.Id,
            playerName = player.Name,
            player.IsLocal,
            player.IsHost,
            roomId = EmptyToNull(player.RoomId),
            region = EmptyToNull(player.Region),
            player.Dead,
            details,
        });

    private static byte[] Record(DateTimeOffset now, string kind, SnapshotState state, object details)
        => JsonLine(new
        {
            schemaVersion = 1,
            timestampUtc = now,
            kind,
            sessionId = EmptyToNull(state.SessionId),
            gameplayId = EmptyToNull(state.GameplayId),
            details,
        });

    private static LogStreamTelemetryRecord Event(byte[] data) => new(EventFileId, data);
    private static LogStreamTelemetryRecord Native(byte[] data) => new(MeadowNativeFileId, data);

    private static string Subject(SnapshotState state, string value) => Pseudonym(state.SessionId, "peer", value);
    private static string Avatar(SnapshotState state, string value) => Pseudonym(state.SessionId, "avatar", value);
    private static string Pseudonym(string sessionId, string kind, string value)
    {
        if (value.Length == 0) return "unavailable";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sessionId + "\n" + kind + "\n" + value));
        return kind + "-" + Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    private static byte[] JsonLine<T>(T value)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        var line = new byte[json.Length + 1];
        json.CopyTo(line, 0);
        line[^1] = (byte)'\n';
        return line;
    }

    private static string? EmptyToNull(string value) => value.Length == 0 ? null : value;
    private static string? OptionalText(string? value, int maximum) => string.IsNullOrWhiteSpace(value) ? null : Text(value, maximum);
    private static string Text(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        string text = new(value.Trim().Where(character => !char.IsControl(character)).ToArray());
        return text.Length <= maximum ? text : text[..maximum];
    }

    private sealed record SnapshotState(
        string SessionId,
        string GameplayId,
        string State,
        string Campaign,
        string Timeline,
        string GameVersion,
        string ModVersion,
        string Process,
        bool IsOnline,
        bool IsHost,
        bool AllowHostControl,
        int? Cycle,
        int? Karma,
        int? KarmaCap,
        int Frame,
        float UnscaledDeltaSeconds,
        long ManagedMemoryBytes,
        string HostActionText,
        LiveCommandResult? CommandResult,
        string[] EnabledExpansions,
        ModState[] ActiveMods,
        bool ActiveModsTruncated,
        Dictionary<string, PlayerState> Players,
        MeadowState? Meadow)
    {
        public static SnapshotState Empty { get; } = new(
            "", "", "", "", "", "", "", "", false, false, false,
            null, null, null, 0, 0, 0, "", null, [], [], false, new(StringComparer.Ordinal), null);

        public static SnapshotState From(LiveSnapshot snapshot)
        {
            var players = (snapshot.Players ?? [])
                .Where(player => player is not null && !string.IsNullOrWhiteSpace(player.Id))
                .Take(256)
                .Select(PlayerState.From)
                .GroupBy(player => player.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var trace = snapshot.Trace;
            return new(
                Text(snapshot.SessionId, 128),
                Text(snapshot.GameplayId, 128),
                Text(snapshot.State, 32),
                Text(snapshot.Campaign, 64),
                Text(snapshot.Timeline, 64),
                Text(snapshot.GameVersion, 64),
                Text(snapshot.ModVersion, 64),
                Text(trace?.Process, 96),
                snapshot.IsOnline,
                snapshot.IsHost,
                snapshot.AllowHostControl,
                trace?.Cycle,
                trace?.Karma,
                trace?.KarmaCap,
                trace?.Frame ?? 0,
                Normalize(trace?.UnscaledDeltaSeconds),
                Math.Max(0, trace?.ManagedMemoryBytes ?? 0),
                Text(snapshot.HostActionText, 512),
                snapshot.CommandResult is null ? null : new()
                {
                    Id = Text(snapshot.CommandResult.Id, 128),
                    Success = snapshot.CommandResult.Success,
                    Message = Text(snapshot.CommandResult.Message, 512),
                },
                TextArray(snapshot.EnabledExpansions, 32, 64),
                (snapshot.ActiveMods ?? []).Where(mod => mod is not null)
                    .Select(ModState.From)
                    .Where(mod => mod.Id.Length > 0)
                    .GroupBy(mod => mod.Id, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .OrderBy(mod => mod.Id, StringComparer.Ordinal)
                    .Take(ProtocolInfo.MaximumActiveMods).ToArray(),
                snapshot.ActiveModsTruncated || (snapshot.ActiveMods?.Length ?? 0) > ProtocolInfo.MaximumActiveMods,
                players,
                MeadowState.From(snapshot.Meadow, players));
        }
    }

    private sealed record ModState(
        string Id,
        string DisplayName,
        string Version,
        string CodeFingerprint,
        string FingerprintStatus)
    {
        public static ModState From(LiveModInfo mod) => new(
            Text(mod.Id, 128),
            Text(mod.DisplayName, 128),
            Text(mod.Version, 64),
            Text(mod.CodeFingerprint, 128),
            Text(mod.FingerprintStatus, 32));
    }

    private sealed record PlayerState(
        string Id,
        string Name,
        string RoomId,
        string Region,
        bool? Dead,
        bool IsLocal,
        bool IsHost,
        string MeadowSteamId,
        ushort? MeadowPeerId,
        string MeadowAvatarId,
        bool? NativeEntityAvailable,
        string NativeLocationAvailability,
        bool? InDen,
        LivePlayerTrace? Trace)
    {
        public static PlayerState From(LivePlayer player) => new(
            Text(player.Id, 160),
            Text(player.Name, 80),
            Text(player.RoomId, 100),
            Text(player.Region, 20),
            player.Dead,
            player.IsLocal,
            player.IsHost,
            Text(player.MeadowSteamId, 32),
            player.MeadowPeerId,
            Text(player.MeadowAvatarId, ProtocolInfo.MaximumMeadowAvatarIdLength),
            player.NativeEntityAvailable,
            Text(player.NativeLocationAvailability, 48),
            player.InDen,
            player.Trace);
    }

    private sealed record MeadowState(
        string LobbyId,
        string ObserverSteamId,
        string GameMode,
        string Timeline,
        string[] RequiredMods,
        string[] BannedMods,
        bool? WhitelistMode,
        bool? CheatsEnabled,
        bool ConfigurationTruncated,
        bool RosterTruncated,
        MeadowOptionState[] LobbyOptions,
        Dictionary<string, MeadowPeerState> Peers,
        Dictionary<string, PlayerState> Avatars)
    {
        public static MeadowState? From(LiveMeadowSnapshot? meadow, IReadOnlyDictionary<string, PlayerState> players)
        {
            if (meadow == null || string.IsNullOrWhiteSpace(meadow.LobbyId)) return null;
            var peers = (meadow.Peers ?? []).Where(peer => peer is not null && !string.IsNullOrWhiteSpace(peer.SteamId))
                .Take(ProtocolInfo.MaximumMeadowPeers).Select(MeadowPeerState.From)
                .GroupBy(peer => peer.SteamId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var avatars = players.Values.Where(player => player.MeadowSteamId.Length > 0
                    && player.MeadowAvatarId.Length > 0 && peers.ContainsKey(player.MeadowSteamId))
                .Take(ProtocolInfo.MaximumMeadowPeers * ProtocolInfo.MaximumMeadowAvatarsPerPeer)
                .GroupBy(player => player.MeadowAvatarId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            return new(
                Text(meadow.LobbyId, ProtocolInfo.MaximumMeadowLabelLength),
                Text(meadow.ObserverSteamId, 32),
                Text(meadow.GameMode, ProtocolInfo.MaximumMeadowLabelLength),
                Text(meadow.Timeline, ProtocolInfo.MaximumMeadowLabelLength),
                TextArray(meadow.RequiredMods, ProtocolInfo.MaximumMeadowModIds,
                    ProtocolInfo.MaximumMeadowModIdLength),
                TextArray(meadow.BannedMods, ProtocolInfo.MaximumMeadowModIds,
                    ProtocolInfo.MaximumMeadowModIdLength),
                meadow.WhitelistMode,
                meadow.CheatsEnabled,
                meadow.ConfigurationTruncated,
                meadow.RosterTruncated,
                (meadow.LobbyOptions ?? []).Where(option => option is not null)
                    .Select(MeadowOptionState.From).Where(option => option.Name.Length > 0)
                    .Distinct().OrderBy(option => option.Name, StringComparer.Ordinal)
                    .Take(ProtocolInfo.MaximumMeadowLobbyOptions).ToArray(),
                peers,
                avatars);
        }
    }

    private sealed record MeadowOptionState(string Name, string Value)
    {
        public static MeadowOptionState From(LiveMeadowLobbyOption option)
            => new(Text(option.Name, ProtocolInfo.MaximumMeadowLobbyOptionNameLength),
                Text(option.Value, ProtocolInfo.MaximumMeadowLobbyOptionValueLength));
    }

    private sealed record MeadowPeerState(
        string SteamId,
        ushort LobbyPeerId,
        string DisplayName,
        bool IsLocal,
        bool IsHost,
        bool SupportsGameHookPackets,
        bool? InGame,
        bool? EnteringChat,
        int? AvatarCount,
        string[] AvatarIds,
        bool? StoryReadyForWin,
        bool? StoryReadyForTransition,
        bool? StoryDead,
        bool? IsSpectating,
        bool? NeedsAcknowledgement,
        int? PingMilliseconds,
        int? IncomingBytesPerSecond,
        int? OutgoingBytesPerSecond,
        uint? RemoteTick,
        uint? LatestAcknowledgedTick,
        int? OutgoingEventCount,
        int? OutgoingStateCount,
        bool? EventsRead,
        bool? StatesRead,
        bool? EventsWritten,
        bool? StatesWritten,
        ConnectionState? Connection)
    {
        public static MeadowPeerState From(LiveMeadowPeer peer) => new(
            Text(peer.SteamId, 32),
            peer.LobbyPeerId,
            Text(peer.DisplayName, ProtocolInfo.MaximumMeadowDisplayNameLength),
            peer.IsLocal,
            peer.IsHost,
            peer.SupportsGameHookPackets,
            peer.InGame,
            peer.EnteringChat,
            peer.AvatarCount is { } avatarCount
                ? Math.Clamp(avatarCount, 0, ProtocolInfo.MaximumMeadowAvatarsPerPeer)
                : null,
            TextArray(peer.AvatarIds, ProtocolInfo.MaximumMeadowAvatarsPerPeer,
                ProtocolInfo.MaximumMeadowAvatarIdLength),
            peer.StoryReadyForWin,
            peer.StoryReadyForTransition,
            peer.StoryDead,
            peer.IsSpectating,
            peer.NeedsAcknowledgement,
            peer.PingMilliseconds is >= 0 and <= 600_000 ? peer.PingMilliseconds : null,
            peer.IncomingBytesPerSecond is { } incoming ? Math.Max(0, incoming) : null,
            peer.OutgoingBytesPerSecond is { } outgoing ? Math.Max(0, outgoing) : null,
            peer.RemoteTick,
            peer.LatestAcknowledgedTick,
            peer.OutgoingEventCount is { } events ? Math.Max(0, events) : null,
            peer.OutgoingStateCount is { } states ? Math.Max(0, states) : null,
            peer.EventsRead,
            peer.StatesRead,
            peer.EventsWritten,
            peer.StatesWritten,
            ConnectionState.From(peer.Connection));
    }

    private sealed record ConnectionState(
        string State,
        int? PingMilliseconds,
        float? LocalDeliveryQuality,
        float? RemoteDeliveryQuality,
        float? IncomingPacketsPerSecond,
        float? OutgoingPacketsPerSecond,
        float? IncomingBytesPerSecond,
        float? OutgoingBytesPerSecond,
        int? EstimatedSendRateBytesPerSecond,
        int? PendingUnreliableBytes,
        int? PendingReliableBytes,
        int? UnacknowledgedReliableBytes,
        int? QueueTimeMicroseconds)
    {
        public static ConnectionState? From(LiveMeadowConnection? value) => value == null ? null : new(
            Text(value.State, ProtocolInfo.MaximumMeadowConnectionStateLength), NonNegative(value.PingMilliseconds),
            Quality(value.LocalDeliveryQuality),
            Quality(value.RemoteDeliveryQuality), Rate(value.IncomingPacketsPerSecond), Rate(value.OutgoingPacketsPerSecond),
            Rate(value.IncomingBytesPerSecond), Rate(value.OutgoingBytesPerSecond),
            NonNegative(value.EstimatedSendRateBytesPerSecond), NonNegative(value.PendingUnreliableBytes),
            NonNegative(value.PendingReliableBytes), NonNegative(value.UnacknowledgedReliableBytes),
            NonNegative(value.QueueTimeMicroseconds));

        private static int? NonNegative(int? value) => value is >= 0 ? value : null;
        private static float? Quality(float? value) => value is >= 0 and <= 1 && float.IsFinite(value.Value) ? value : null;
        private static float? Rate(float? value) => value is >= 0 && float.IsFinite(value.Value) ? value : null;
    }

    private static string[] TextArray(IEnumerable<string>? values, int maximumItems, int maximumLength)
        => (values ?? []).Select(value => Text(value, maximumLength)).Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).Take(maximumItems).ToArray();

    private static float Normalize(float? value)
        => value is { } number && !float.IsNaN(number) && !float.IsInfinity(number) && number >= 0 ? number : 0;
}
