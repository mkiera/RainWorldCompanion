using System.Text.Json;
using System.Text.Json.Serialization;
using RainWorldCompanion.LiveProtocol;

namespace RainWorldCompanion.Core.LogStreaming;

internal sealed class LogStreamTelemetryRecorder(string appVersion)
{
    public const string EventFileId = "Companion/events.jsonl";
    public const string DeepTraceFileId = "Companion/deep-trace.jsonl";
    private const int MaximumDeepTracePlayers = 32;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _appVersion = Text(appVersion, 64);
    private SnapshotState? _previous;
    private bool _connected;
    private DateTimeOffset _lastHitch;

    public void Reset()
    {
        _previous = null;
        _connected = false;
        _lastHitch = default;
    }

    public IReadOnlyList<byte[]> Observe(LiveSnapshot? snapshot, DateTimeOffset now)
    {
        var records = new List<byte[]>();
        if (snapshot is null)
        {
            if (_connected && _previous is { } previous)
                records.Add(Record(now, "connection-lost", previous, new { }));
            _connected = false;
            return records;
        }

        var current = SnapshotState.From(snapshot);
        if (_previous is null || !string.Equals(_previous.SessionId, current.SessionId, StringComparison.Ordinal))
        {
            records.Add(Record(now, "session-start", current, new
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
            }));
            foreach (var player in current.Players.Values.OrderBy(player => player.Id, StringComparer.Ordinal))
                records.Add(PlayerRecord(now, "player-present", current, player, null));
        }
        else
        {
            SnapshotState previous = _previous;
            if (!_connected) records.Add(Record(now, "connection-restored", current, new { }));
            if (!string.Equals(previous.GameplayId, current.GameplayId, StringComparison.Ordinal))
                records.Add(Record(now, current.GameplayId.Length == 0 ? "gameplay-ended" : "gameplay-started", current,
                    new { previousGameplayId = EmptyToNull(previous.GameplayId) }));
            if (!string.Equals(previous.Process, current.Process, StringComparison.Ordinal))
                records.Add(Record(now, "process-changed", current, new { previous = previous.Process, current = current.Process }));
            if (!string.Equals(previous.State, current.State, StringComparison.Ordinal))
                records.Add(Record(now, "game-state-changed", current, new { previous = previous.State, current = current.State }));
            if (!string.Equals(previous.Campaign, current.Campaign, StringComparison.Ordinal)
                || !string.Equals(previous.Timeline, current.Timeline, StringComparison.Ordinal))
                records.Add(Record(now, "campaign-changed", current, new
                {
                    previousCampaign = previous.Campaign,
                    currentCampaign = current.Campaign,
                    previousTimeline = previous.Timeline,
                    currentTimeline = current.Timeline,
                }));
            if (previous.Cycle != current.Cycle || previous.Karma != current.Karma || previous.KarmaCap != current.KarmaCap)
                records.Add(Record(now, "progression-changed", current, new
                {
                    previousCycle = previous.Cycle,
                    currentCycle = current.Cycle,
                    previousKarma = previous.Karma,
                    currentKarma = current.Karma,
                    previousKarmaCap = previous.KarmaCap,
                    currentKarmaCap = current.KarmaCap,
                }));
            if (previous.AllowHostControl != current.AllowHostControl)
                records.Add(Record(now, "host-control-changed", current, new { enabled = current.AllowHostControl }));
            if (previous.IsHost != current.IsHost)
                records.Add(Record(now, "local-host-role-changed", current, new { isHost = current.IsHost }));
            if (!previous.EnabledExpansions.SequenceEqual(current.EnabledExpansions, StringComparer.Ordinal))
                records.Add(Record(now, "expansions-changed", current, new { enabledExpansions = current.EnabledExpansions }));
            if (previous.ActiveModsTruncated != current.ActiveModsTruncated
                || !previous.ActiveMods.SequenceEqual(current.ActiveMods))
                records.Add(Record(now, "active-mods-changed", current, new
                {
                    previous = previous.ActiveMods,
                    current = current.ActiveMods,
                    current.ActiveModsTruncated,
                }));
            if (current.CommandResult is { } command
                && !string.Equals(previous.CommandResult?.Id, command.Id, StringComparison.Ordinal))
                records.Add(Record(now, "companion-action-result", current, new
                {
                    commandId = command.Id,
                    command.Success,
                    message = command.Message,
                }));
            if (current.HostActionText.Length > 0
                && !string.Equals(previous.HostActionText, current.HostActionText, StringComparison.Ordinal))
                records.Add(Record(now, "rain-meadow-host-action", current, new { message = current.HostActionText }));
            if (current.UnscaledDeltaSeconds >= 0.25f && now - _lastHitch >= TimeSpan.FromSeconds(5))
            {
                _lastHitch = now;
                records.Add(Record(now, "frame-hitch", current, new
                {
                    durationMilliseconds = Math.Round(current.UnscaledDeltaSeconds * 1000, 1),
                    current.Frame,
                    current.ManagedMemoryBytes,
                }));
            }
            ComparePlayers(previous, current, now, records);
        }

        _previous = current;
        _connected = true;
        return records;
    }

    public byte[] DeepTrace(LiveSnapshot snapshot, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var state = SnapshotState.From(snapshot);
        var players = state.Players.Values
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
        return JsonLine(new
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
            playerCount = state.Players.Count,
            playersTruncated = state.Players.Count > players.Length,
            players,
        });
    }

    public byte[] CompanionAction(
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
        return Record(now, "companion-action-" + Text(phase, 24), current, new
        {
            action = Text(action, 32),
            playerId = OptionalText(playerId, 160),
            roomId = OptionalText(roomId, 100),
            region = OptionalText(region, 20),
            success,
            message = OptionalText(message, 512),
        });
    }

    private static void ComparePlayers(
        SnapshotState previous,
        SnapshotState current,
        DateTimeOffset now,
        ICollection<byte[]> records)
    {
        foreach (var removed in previous.Players.Values.Where(player => !current.Players.ContainsKey(player.Id)))
            records.Add(PlayerRecord(now, "player-left", current, removed, null));
        foreach (var player in current.Players.Values)
        {
            if (!previous.Players.TryGetValue(player.Id, out var old))
            {
                records.Add(PlayerRecord(now, "player-joined", current, player, null));
                continue;
            }
            if (!string.Equals(old.RoomId, player.RoomId, StringComparison.Ordinal)
                || !string.Equals(old.Region, player.Region, StringComparison.Ordinal))
                records.Add(PlayerRecord(now, "room-changed", current, player, new
                {
                    previousRoom = EmptyToNull(old.RoomId),
                    currentRoom = EmptyToNull(player.RoomId),
                    previousRegion = EmptyToNull(old.Region),
                    currentRegion = EmptyToNull(player.Region),
                }));
            if (old.Dead != player.Dead)
                records.Add(PlayerRecord(now, player.Dead == true ? "player-died"
                    : player.Dead == false ? "player-revived" : "player-life-state-unknown", current, player,
                    new { previousDead = old.Dead, currentDead = player.Dead }));
            if (old.IsHost != player.IsHost)
                records.Add(PlayerRecord(now, "player-host-role-changed", current, player, new { isHost = player.IsHost }));
            if (old.Trace?.Realized != player.Trace?.Realized)
                records.Add(PlayerRecord(now, player.Trace?.Realized == true ? "player-realized" : "player-unrealized",
                    current, player, null));
            if (old.Trace?.InShortcut != player.Trace?.InShortcut)
                records.Add(PlayerRecord(now, player.Trace?.InShortcut == true ? "shortcut-entered" : "shortcut-exited",
                    current, player, null));
            if (old.Trace?.SlatedForDeletion != true && player.Trace?.SlatedForDeletion == true)
                records.Add(PlayerRecord(now, "player-marked-for-deletion", current, player, null));
        }
    }

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
        Dictionary<string, PlayerState> Players)
    {
        public static SnapshotState Empty { get; } = new(
            "", "", "", "", "", "", "", "", false, false, false,
            null, null, null, 0, 0, 0, "", null, [], [], false, new(StringComparer.Ordinal));

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
                (snapshot.EnabledExpansions ?? []).Select(value => Text(value, 64))
                    .Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal)
                    .Take(32).ToArray(),
                (snapshot.ActiveMods ?? []).Where(mod => mod is not null)
                    .Select(ModState.From)
                    .Where(mod => mod.Id.Length > 0)
                    .GroupBy(mod => mod.Id, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .OrderBy(mod => mod.Id, StringComparer.Ordinal)
                    .Take(ProtocolInfo.MaximumActiveMods).ToArray(),
                snapshot.ActiveModsTruncated || (snapshot.ActiveMods?.Length ?? 0) > ProtocolInfo.MaximumActiveMods,
                players);
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
            player.Trace);
    }

    private static float Normalize(float? value)
        => value is { } number && !float.IsNaN(number) && !float.IsInfinity(number) && number >= 0
            ? number
            : 0;
}
