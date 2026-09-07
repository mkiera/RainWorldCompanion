using BepInEx;
using RainWorldCompanion.LiveProtocol;
using UnityEngine;

namespace RWCompanion.Mod;

[BepInPlugin("rwcompanion", "Companion Game Hook", ProtocolInfo.ModVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    private readonly LiveTransport _transport = new();
    private readonly string _session = Guid.NewGuid().ToString("N");
    private UnityEngine.Object? _world;
    private Type? _rainWorldType;
    private Type? _gameType;
    private float _nextSample;
    private long _sequence;
    private bool _reportedFailure;
    private object? _game;
    private string _gameplayId = "";
    private TeleportOperation? _teleport;
    private LiveCommandResult? _commandResult;
    private string? _lastCommandId;
    private BepInEx.Configuration.ConfigEntry<bool>? _allowHostControl;
    private string[] _enabledMods = Array.Empty<string>();
    private MeadowHostControl? _hostControl;

    public void OnEnable()
    {
        _allowHostControl = Config.Bind("Map", "AllowHostControl", false, "Allow the current Rain Meadow host to request actions on your local player.");
        _hostControl = new MeadowHostControl();
        _transport.Start();
    }
    public void OnDisable() { _hostControl?.Dispose(); _transport.Stop(); }

    public void Update()
    {
        try
        {
            if (Time.unscaledTime >= _nextSample)
                _enabledMods = GameAccess.Items(GameAccess.Get(GameAccess.FindType("ModManager"), "ActiveMods")).Select(mod => GameAccess.Text(mod, "id")).ToArray();
            if (!_enabledMods.Contains("rwcompanion", StringComparer.OrdinalIgnoreCase))
            {
                _hostControl?.Suspend();
                _teleport = null;
                _game = null;
                _gameplayId = "";
                return;
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
            if (_teleport?.Update(game) is { } result) { _commandResult = result; _teleport = null; }
            if (_transport.TakeCommand() is { } command && command.Id != _lastCommandId)
            {
                _lastCommandId = command.Id;
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
                        if (!ValidIdentifier(command.RoomId, 100) || !ValidIdentifier(command.Region, 20))
                            throw new InvalidOperationException("Invalid teleport destination.");
                        if (command.TeleportAll) _hostControl!.RequestAll(command);
                        else if (MeadowPlayers.IsOnline && MeadowPlayers.FindLocal(command.PlayerId) == null) _hostControl!.Request(command);
                        else _teleport = new TeleportOperation(command, game);
                    }
                }
                catch (Exception exception) { _commandResult = new() { Id = command.Id, Message = exception.GetBaseException().Message }; }
            }
            if (_hostControl?.Update(game, _gameplayId, _allowHostControl?.Value == true, _teleport != null) is { } hostResult)
                _commandResult = hostResult;
            if (Time.unscaledTime < _nextSample) return;
            _nextSample = Time.unscaledTime + 0.2f;
            var session = GameAccess.Get(game, "session");
            var snapshot = new LiveSnapshot
            {
                SessionId = _session,
                GameplayId = _gameplayId,
                CommandVersion = 1,
                CommandResult = _commandResult,
                IsOnline = MeadowPlayers.IsOnline,
                IsHost = MeadowPlayers.IsHost,
                SupportsTeleportAll = true,
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
                Players = MeadowPlayers.Read() ?? (game == null ? Array.Empty<LivePlayer>() : ReadPlayers(game))
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
            _transport.Publish(new LiveSnapshot
            {
                SessionId = _session,
                CommandVersion = 1,
                Sequence = ++_sequence,
                GameVersion = GameAccess.Text(_rainWorldType, "GAME_VERSION_STRING"),
                GameInstallPath = Path.GetDirectoryName(Application.dataPath) ?? "",
                State = "unavailable"
            });
        }
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
            Dead = GameAccess.Get(GameAccess.Get(creature, "state"), "dead") as bool?
        };
    }
}
