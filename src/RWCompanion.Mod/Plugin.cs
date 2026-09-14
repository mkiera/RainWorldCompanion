using BepInEx;
using RainWorldCompanion.LiveProtocol;
using UnityEngine;

namespace RWCompanion.Mod;

[BepInPlugin("rwcompanion", "Companion Game Hook", ProtocolInfo.ModVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    private readonly LiveTransport _transport = new();
    private readonly LogBridgeTransport _logBridge = new();
    private readonly MeadowLogTransport _meadowLogs = new();
    private readonly string _session = Guid.NewGuid().ToString("N");
    private UnityEngine.Object? _world;
    private Type? _rainWorldType;
    private Type? _gameType;
    private float _nextSample;
    private long _sequence;
    private bool _reportedFailure;
    private bool _reportedLogStreamingFailure;
    private object? _game;
    private string _gameplayId = "";
    private TeleportOperation? _teleport;
    private LiveCommandResult? _commandResult;
    private string? _lastCommandId;
    private BepInEx.Configuration.ConfigEntry<bool>? _allowHostControl;
    private string[] _enabledMods = Array.Empty<string>();
    private MeadowHostControl? _hostControl;
    private ActiveModInventory? _activeModInventory;

    public void OnEnable()
    {
        TeleportOperation.Log = message => Logger.LogInfo(message);
        _allowHostControl = Config.Bind("Map", "AllowHostControl", true, "Allow the current Rain Meadow host to request actions on your local player.");
        _hostControl = new MeadowHostControl();
        _activeModInventory?.Dispose();
        _activeModInventory = new ActiveModInventory();
        _transport.Start();
        _logBridge.Start();
    }
    public void OnDisable()
    {
        _teleport?.Cancel();
        _teleport = null;
        _hostControl?.Dispose();
        _activeModInventory?.Dispose();
        _activeModInventory = null;
        _meadowLogs.Dispose();
        _logBridge.Stop();
        _transport.Stop();
        TeleportOperation.Log = null;
    }

    public void Update()
    {
        try
        {
            if (Time.unscaledTime >= _nextSample)
            {
                object[] activeMods = GameAccess.Items(GameAccess.Get(GameAccess.FindType("ModManager"), "ActiveMods")).ToArray();
                _enabledMods = activeMods.Select(mod => GameAccess.Text(mod, "id")).ToArray();
                _activeModInventory?.Observe(activeMods);
            }
            if (!_enabledMods.Contains("rwcompanion", StringComparer.OrdinalIgnoreCase))
            {
                _hostControl?.Suspend();
                _teleport?.Cancel();
                _teleport = null;
                _game = null;
                _gameplayId = "";
                _logBridge.PublishLobby(new());
                return;
            }
            try
            {
                UpdateLogStreaming();
                _reportedLogStreamingFailure = false;
            }
            catch (Exception exception)
            {
                if (!_reportedLogStreamingFailure) Logger.LogWarning("Log streaming update failed: " + exception.Message);
                _reportedLogStreamingFailure = true;
                _logBridge.PublishLobby(new());
            }
            _rainWorldType ??= GameAccess.FindType("RainWorld");
            _gameType ??= GameAccess.FindType("RainWorldGame");
            if (_rainWorldType == null || _gameType == null) return;
            if (_world == null) _world = UnityEngine.Object.FindObjectOfType(_rainWorldType);
            var loop = GameAccess.Get(GameAccess.Get(_world, "processManager"), "currentMainLoop");
            var game = loop != null && _gameType.IsInstanceOfType(loop) ? loop : null;
            if (!ReferenceEquals(game, _game))
            {
                _game = game;
                _gameplayId = game == null ? "" : Guid.NewGuid().ToString("N");
            }
            TeleportOperation.Observe(game);
            if (_teleport?.Update(game) is { } result) { _commandResult = result; _teleport = null; }
            if (_transport.TakeCommand() is { } command && command.Id != _lastCommandId)
            {
                _lastCommandId = command.Id;
                Logger.LogInfo("Live command " + command.Id + " action=" + (command.Recover ? "recover" : command.TeleportAll ? "teleport-all" : command.AllowHostControl.HasValue ? "host-control" : "teleport")
                    + " player=" + command.PlayerId + " destination=" + command.Region + "/" + command.RoomId);
                try
                {
                    if (command.SessionId != _session || command.ExpiresUtcTicks < DateTime.UtcNow.Ticks)
                        throw new InvalidOperationException("The teleport request expired or gameplay changed.");
                    if (command.AllowHostControl is { } allowed)
                    {
                        _allowHostControl!.Value = allowed;
                        Config.Save();
                        _commandResult = new() { Id = command.Id, Success = true, Message = allowed ? "Host control allowed." : "Host control disabled." };
                    }
                    else
                    {
                        if (_teleport != null || _hostControl?.Busy == true) throw new InvalidOperationException("A teleport is already in progress.");
                        if (game == null || command.GameplayId != _gameplayId) throw new InvalidOperationException("Gameplay changed before teleport started.");
                        if (!command.Recover && (!ValidIdentifier(command.RoomId, 100) || !ValidIdentifier(command.Region, 20)))
                            throw new InvalidOperationException("Invalid teleport destination.");
                        if (command.Recover && (command.TeleportAll || MeadowPlayers.IsOnline && MeadowPlayers.FindLocal(command.PlayerId) == null))
                            throw new InvalidOperationException("Recovery is available for your local player only.");
                        if (command.TeleportAll) _hostControl!.RequestAll(command);
                        else if (MeadowPlayers.IsOnline && MeadowPlayers.FindLocal(command.PlayerId) == null) _hostControl!.Request(command);
                        else _teleport = new TeleportOperation(command, game);
                    }
                }
                catch (Exception exception)
                {
                    _commandResult = new() { Id = command.Id, Message = exception.GetBaseException().Message };
                    Logger.LogWarning("Live command " + command.Id + " rejected: " + _commandResult.Message);
                }
            }
            if (_hostControl?.Update(game, _gameplayId, _allowHostControl?.Value == true, _teleport != null) is { } hostResult)
                _commandResult = hostResult;
            if (Time.unscaledTime < _nextSample) return;
            _nextSample = Time.unscaledTime + 0.2f;
            var session = GameAccess.Get(game, "session");
            var modInventory = _activeModInventory?.Capture() ?? new(Array.Empty<LiveModInfo>(), false);
            var players = MeadowPlayers.Read() ?? (game == null ? Array.Empty<LivePlayer>() : ReadPlayers(game));
            var snapshot = new LiveSnapshot
            {
                SessionId = _session,
                GameplayId = _gameplayId,
                CommandVersion = 1,
                CommandResult = _commandResult,
                IsOnline = MeadowPlayers.IsOnline,
                IsHost = MeadowPlayers.IsHost,
                SupportsTeleportAll = true,
                SupportsRecovery = true,
                TeleportAllUnavailableReason = _hostControl?.AllUnavailableReason() ?? "Host control is unavailable.",
                HostActionText = _hostControl?.LastAction ?? "",
                AllowHostControl = _allowHostControl?.Value == true,
                Sequence = ++_sequence,
                GameVersion = GameAccess.Text(_rainWorldType, "GAME_VERSION_STRING"),
                GameInstallPath = Path.GetDirectoryName(Application.dataPath) ?? "",
                State = game == null ? "menu" : GameAccess.Get(game, "GamePaused") is true ? "paused" : "gameplay",
                Campaign = GameAccess.EnumValue(GameAccess.Get(session, "saveStateNumber")),
                Timeline = GameAccess.EnumValue(GameAccess.Get(game, "TimelinePoint")),
                EnabledExpansions = _enabledMods.Where(id => id is "moreslugcats" or "watcher").ToArray(),
                ActiveMods = modInventory.Mods,
                ActiveModsTruncated = modInventory.Truncated,
                Players = players,
                Meadow = _meadowLogs.CaptureTelemetry(players),
                Trace = ReadTrace(loop, game, session)
            };
            VisitedRooms.Read(session, snapshot);
            _hostControl?.Describe(snapshot.Players);
            _transport.Publish(snapshot);
            _reportedFailure = false;
        }
        catch (Exception exception)
        {
            if (!_reportedFailure) Logger.LogWarning("Live data sampling failed: " + exception.Message);
            _reportedFailure = true;
            var modInventory = _activeModInventory?.Capture() ?? new(Array.Empty<LiveModInfo>(), false);
            _transport.Publish(new LiveSnapshot
            {
                SessionId = _session,
                CommandVersion = 1,
                Sequence = ++_sequence,
                GameVersion = GameAccess.Text(_rainWorldType, "GAME_VERSION_STRING"),
                GameInstallPath = Path.GetDirectoryName(Application.dataPath) ?? "",
                ActiveMods = modInventory.Mods,
                ActiveModsTruncated = modInventory.Truncated,
                State = "unavailable"
            });
        }
    }

    private void UpdateLogStreaming()
    {
        var advertisement = _logBridge.Advertisement;
        _meadowLogs.SetLocalAdvertisement(advertisement.Available, advertisement.CaptureActive,
            advertisement.CapturePaused, advertisement.DeepTraceEnabled, advertisement.CaptureId, advertisement.CaptureToken,
            ProtocolInfo.LogStreamingVersion);
        _meadowLogs.Update();
        while (_meadowLogs.TryReceive(out var packet) && packet != null)
            _logBridge.PublishReceived(new() { PeerSteamId = packet.SenderSteamId, Payload = packet.Payload });

        var local = _meadowLogs.Peers.FirstOrDefault(peer => peer.IsLocal);
        _logBridge.PublishLobby(new()
        {
            IsConnected = _meadowLogs.IsSteamLobby && _meadowLogs.CurrentLobbyId.Length > 0,
            IsSteam = _meadowLogs.IsSteamLobby,
            LobbyId = _meadowLogs.CurrentLobbyId,
            LocalSteamId = local?.SteamId ?? "",
            LocalDisplayName = local?.DisplayName ?? "",
            LocalIsHost = local?.IsHost == true,
            Peers = _meadowLogs.Peers.Where(peer => !peer.IsLocal).Select(peer => new LogLobbyPeer
            {
                SteamId = peer.SteamId,
                DisplayName = peer.DisplayName,
                IsHost = peer.IsHost,
                SupportsLogStreaming = peer.SupportsLogStreaming
                    && peer.ProtocolVersion == ProtocolInfo.LogStreamingVersion,
                ProtocolVersion = peer.ProtocolVersion,
                ReceiverAvailable = peer.Available,
                CaptureActive = peer.CaptureActive,
                CapturePaused = peer.CapturePaused,
                DeepTraceEnabled = peer.DeepTraceEnabled,
                CaptureId = peer.CaptureId,
                CaptureToken = peer.CaptureToken,
                LastSeenUtcTicks = peer.LastSeenUtc == DateTime.MinValue ? 0 : peer.LastSeenUtc.Ticks
            }).ToArray()
        });

        var retry = new List<LogRelayPacket>();
        foreach (var packet in _logBridge.TakeOutgoing())
            if (!_meadowLogs.TrySend(packet.PeerSteamId, packet.Payload)) retry.Add(packet);
        _logBridge.RetryOutgoing(retry);
    }

    private static bool ValidIdentifier(string? value, int maximum) => !string.IsNullOrWhiteSpace(value)
        && value!.Length <= maximum && value.All(c => char.IsLetterOrDigit(c) || c == '_');

    private static LivePlayer[] ReadPlayers(object game)
    {
        var meadow = MeadowPlayers.Read();
        if (meadow != null) return meadow;
        var options = GameAccess.Get(GameAccess.Get(game, "rainWorld"), "options");
        var playerOptions = GameAccess.Items(GameAccess.Get(options, "jollyPlayerOptionsArray")).ToArray();
        return GameAccess.Items(GameAccess.Get(game, "Players")).Select((creature, index) =>
        {
            int number = GameAccess.Get(GameAccess.Get(creature, "state"), "playerNumber") is int playerNumber ? playerNumber : index;
            string name = number >= 0 && number < playerOptions.Length ? GameAccess.Text(playerOptions[number], "customPlayerName") : "";
            return FromCreature(creature, "local:" + number, string.IsNullOrWhiteSpace(name) ? "Player " + (number + 1) : name, true);
        }).ToArray();
    }

    internal static LivePlayer FromCreature(object? creature, string id, string name, bool local)
    {
        string? room = GameAccess.Get(GameAccess.Get(creature, "Room"), "name") as string;
        string? region = GameAccess.Get(GameAccess.Get(GameAccess.Get(creature, "world"), "region"), "name") as string;
        return new LivePlayer
        {
            Id = id,
            Name = name,
            IsLocal = local,
            RoomId = room,
            Region = region ?? (room?.Contains("_") == true ? room.Substring(0, room.IndexOf('_')) : null),
            Dead = GameAccess.Get(GameAccess.Get(creature, "state"), "dead") as bool?,
            Trace = ReadPlayerTrace(creature, local)
        };
    }

    private static LiveTrace ReadTrace(object? loop, object? game, object? session)
    {
        object? saveState = GameAccess.Get(session, "saveState");
        object? persistent = GameAccess.Get(saveState, "deathPersistentSaveData");
        object? rain = GameAccess.Get(GameAccess.Get(game, "world"), "rainCycle");
        return new()
        {
            Process = loop?.GetType().Name ?? "None",
            Frame = Time.frameCount,
            UnscaledDeltaSeconds = Time.unscaledDeltaTime,
            TimeScale = Time.timeScale,
            ManagedMemoryBytes = GC.GetTotalMemory(false),
            Cycle = Integer(saveState, "cycleNumber"),
            Karma = Integer(persistent, "karma"),
            KarmaCap = Integer(persistent, "karmaCap"),
            RainTimer = Integer(rain, "timer"),
            RainCycleLength = Integer(rain, "cycleLength")
        };
    }

    private static LivePlayerTrace ReadPlayerTrace(object? creature, bool includeExactState)
    {
        object? realized = GameAccess.Get(creature, "realizedCreature");
        if (realized is null && GameAccess.Get(creature, "firstChunk") is not null) realized = creature;
        object? abstractCreature = GameAccess.Get(realized, "abstractCreature") ?? creature;
        object? coordinate = GameAccess.Get(abstractCreature, "pos");
        var trace = new LivePlayerTrace
        {
            Realized = realized is not null,
            SlatedForDeletion = GameAccess.Get(creature, "slatedForDeletion") is true
                || GameAccess.Get(realized, "slatedForDeletion") is true,
            InShortcut = GameAccess.Get(realized, "inShortcut") is true
                || GameAccess.Get(realized, "enteringShortCut") is not null,
            AbstractNode = Integer(coordinate, "abstractNode")
        };
        if (!includeExactState) return trace;

        object? chunk = GameAccess.Get(realized, "firstChunk");
        object? position = GameAccess.Get(chunk, "pos");
        object? velocity = GameAccess.Get(chunk, "vel");
        object? input = GameAccess.Items(GameAccess.Get(realized, "input")).FirstOrDefault();
        trace.PositionX = Number(position, "x");
        trace.PositionY = Number(position, "y");
        trace.VelocityX = Number(velocity, "x");
        trace.VelocityY = Number(velocity, "y");
        trace.AbstractX = Integer(coordinate, "x");
        trace.AbstractY = Integer(coordinate, "y");
        trace.Stun = Integer(realized, "stun");
        trace.AirInLungs = Number(realized, "airInLungs");
        trace.FoodInStomach = Integer(realized, "FoodInStomach");
        trace.Input = input is null ? null : new()
        {
            X = Integer(input, "x") ?? 0,
            Y = Integer(input, "y") ?? 0,
            Jump = GameAccess.Get(input, "jmp") is true,
            Throw = GameAccess.Get(input, "thrw") is true,
            Pickup = GameAccess.Get(input, "pckp") is true,
            Map = GameAccess.Get(input, "mp") is true
        };
        return trace;
    }

    private static int? Integer(object? target, string name) => GameAccess.Get(target, name) switch
    {
        byte value => value,
        sbyte value => value,
        short value => value,
        ushort value => value,
        int value => value,
        uint value when value <= int.MaxValue => (int)value,
        long value when value is >= int.MinValue and <= int.MaxValue => (int)value,
        _ => null
    };

    private static float? Number(object? target, string name) => GameAccess.Get(target, name) switch
    {
        float value when !float.IsNaN(value) && !float.IsInfinity(value) => value,
        double value when !double.IsNaN(value) && !double.IsInfinity(value)
            && value is >= float.MinValue and <= float.MaxValue => (float)value,
        int value => value,
        _ => null
    };
}
