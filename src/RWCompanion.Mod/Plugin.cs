using BepInEx;
using RainWorldCompanion.LiveProtocol;
using UnityEngine;

namespace RWCompanion.Mod;

[BepInPlugin("rwcompanion", "RainWorld Companion", ProtocolInfo.ModVersion)]
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

    public void OnEnable() => _transport.Start();
    public void OnDisable() => _transport.Stop();

    public void Update()
    {
        if (Time.unscaledTime < _nextSample) return;
        _nextSample = Time.unscaledTime + 0.2f;
        try
        {
            var enabledMods = GameAccess.Items(GameAccess.Get(GameAccess.FindType("ModManager"), "ActiveMods")).Select(mod => GameAccess.Text(mod, "id")).ToArray();
            if (!enabledMods.Contains("rwcompanion", StringComparer.OrdinalIgnoreCase)) return;
            _rainWorldType ??= GameAccess.FindType("RainWorld");
            _gameType ??= GameAccess.FindType("RainWorldGame");
            if (_rainWorldType == null || _gameType == null) return;
            if (_world == null) _world = UnityEngine.Object.FindObjectOfType(_rainWorldType);
            var loop = GameAccess.Get(GameAccess.Get(_world, "processManager"), "currentMainLoop");
            var game = loop != null && _gameType.IsInstanceOfType(loop) ? loop : null;
            var session = GameAccess.Get(game, "session");
            var snapshot = new LiveSnapshot
            {
                SessionId = _session,
                Sequence = ++_sequence,
                GameVersion = GameAccess.Text(_rainWorldType, "GAME_VERSION_STRING"),
                GameInstallPath = Path.GetDirectoryName(Application.dataPath) ?? "",
                State = game == null ? "menu" : GameAccess.Get(game, "GamePaused") is true ? "paused" : "gameplay",
                Campaign = GameAccess.EnumValue(GameAccess.Get(session, "saveStateNumber")),
                Timeline = GameAccess.EnumValue(GameAccess.Get(game, "TimelinePoint")),
                EnabledExpansions = enabledMods.Where(id => id is "moreslugcats" or "watcher").ToArray(),
                Players = game == null ? Array.Empty<LivePlayer>() : ReadPlayers(game)
            };
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
                Sequence = ++_sequence,
                GameVersion = GameAccess.Text(_rainWorldType, "GAME_VERSION_STRING"),
                GameInstallPath = Path.GetDirectoryName(Application.dataPath) ?? "",
                State = "unavailable"
            });
        }
    }

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
