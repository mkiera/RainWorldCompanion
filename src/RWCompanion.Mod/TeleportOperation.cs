using System.Collections;
using RainWorldCompanion.LiveProtocol;
using UnityEngine;

namespace RWCompanion.Mod;

internal sealed class TeleportOperation
{
    private static readonly Dictionary<string, string> LastRooms = new();
    private static object? _observedGame;
    internal static Action<string>? Log { get; set; }
    private readonly LiveCommand _command;
    private readonly object _game;
    private readonly object _creature;
    private readonly object _sourceWorld;
    private readonly object _overWorld;
    private readonly float _started = Time.unscaledTime;
    private object? _loader;
    private object? _destination;
    private bool _crossRegion;
    private Vector2 _spawnPosition;
    private readonly bool _group;
    private readonly bool _recover;
    private bool _moveStarted;
    private object[] _onlineObjects = Array.Empty<object>();
    private object[] _carriedObjects = Array.Empty<object>();
    private object?[] _movingStates = Array.Empty<object?>();
    private readonly string _destinationName;
    internal bool Ready { get; private set; }
    internal bool Committed { get; set; }

    internal TeleportOperation(LiveCommand command, object game, bool group = false)
    {
        _group = group;
        _recover = command.Recover;
        _command = command;
        _game = game;
        if (GameAccess.Get(game, "IsStorySession") is not true) throw new InvalidOperationException("Teleport is available during campaign gameplay.");
        _overWorld = GameAccess.Get(game, "overWorld") ?? throw new InvalidOperationException("World is unavailable.");
        _sourceWorld = GameAccess.Get(_overWorld, "activeWorld") ?? throw new InvalidOperationException("World is unavailable.");
        _creature = FindPlayer(game, command.PlayerId) ?? throw new InvalidOperationException("The local player is unavailable or ownership is changing.");
        if (!_recover) ValidatePlayer(_creature);
        CheckWorldTransition();
        var activeRoomNames = GameAccess.Items(GameAccess.Get(_sourceWorld, "abstractRooms")).Select(room => GameAccess.Text(room, "name")).ToArray();
        var currentRoom = GameAccess.Get(GameAccess.Get(_creature, "realizedCreature"), "room") == null
            ? "" : GameAccess.Text(GameAccess.Get(_creature, "Room"), "name");
        _destinationName = _recover
            ? RecoveryDestination.Choose(currentRoom, LastRooms.TryGetValue(command.PlayerId, out var observed) ? observed : null, activeRoomNames)
                ?? throw new InvalidOperationException("No recent room is available for recovery in the active region.")
            : command.RoomId;
        _destination = FindRoom(_sourceWorld, _destinationName);
        if (_destination != null)
        {
            WriteLog((_group ? "group " : "") + (_recover ? "recovery " : "teleport ")
                + GameAccess.Text(_sourceWorld, "name") + "/" + currentRoom + " -> " + _destinationName);
            return;
        }
        if (_recover) throw new InvalidOperationException("Recovery is limited to the active region.");
        if (MeadowPlayers.IsOnline && !group)
            throw new InvalidOperationException("Rain Meadow teleport currently supports rooms in your loaded region. Cross-region travel needs a synchronized lobby transition.");
        var region = GameAccess.Call(_overWorld, "GetRegion", command.Region.ToUpperInvariant());
        if (region == null) throw new InvalidOperationException("This region is unavailable in the current campaign.");
        if (!_recover) foreach (var player in LocalCreatures(game)) ValidatePlayer(player);
        _crossRegion = true;
        _loader = Activator.CreateInstance(GameAccess.FindType("WorldLoader")!, game,
            GameAccess.Get(_overWorld, "PlayerCharacterNumber"), GameAccess.Get(game, "TimelinePoint"), false,
            command.Region.ToUpperInvariant(), region, GameAccess.Get(game, "setupValues"));
        GameAccess.Call(_loader!, "NextActivity");
    }

    internal LiveCommandResult? Update(object? currentGame)
    {
        try
        {
            if (!ReferenceEquals(currentGame, _game)) throw new InvalidOperationException("Gameplay ended before teleport completed.");
            if (Time.unscaledTime - _started > 45) throw new InvalidOperationException(_moveStarted
                ? "The region transition did not settle before the timeout." : "Destination loading timed out. The player has not been moved.");
            if (!_moveStarted && !ReferenceEquals(GameAccess.Get(_overWorld, "activeWorld"), _sourceWorld))
                throw new InvalidOperationException("The region changed before teleport completed.");
            if (!_moveStarted) CheckWorldTransition();
            if (!_recover && !_moveStarted) ValidatePlayer(_creature);
            if (!_moveStarted && !ReferenceEquals(FindPlayer(_game, _command.PlayerId), _creature))
                throw new InvalidOperationException("Player ownership changed before teleport completed.");
            if (_loader != null)
            {
                if (GameAccess.Get(_loader, "Finished") is not true) GameAccess.Call(_loader, "Update");
                if (GameAccess.Get(_loader, "Finished") is not true) return null;
                var world = GameAccess.Call(_loader, "ReturnWorld")!;
                _destination = FindRoom(world, _destinationName)
                    ?? throw new InvalidOperationException("This room does not exist in the current campaign.");
            }
            if (_destination == null) throw new InvalidOperationException("Destination room is unavailable.");
            var destinationWorld = GameAccess.Get(_destination, "world")!;
            if (GameAccess.Get(_destination, "realizedRoom") == null)
                GameAccess.Call(_destination, "RealizeRoom", destinationWorld, _game);
            var room = GameAccess.Get(_destination, "realizedRoom")!;
            if (!_crossRegion && GameAccess.Get(room, "ReadyForPlayer") is not true) return null;
            if (!Ready)
            {
                var tile = GameAccess.Call(GameAccess.FindType("Room")!, "DetermineSafeSpawnTile", room, true)!;
                if (GameAccess.Get(GameAccess.Call(room, "GetTile", tile), "Solid") is true)
                    throw new InvalidOperationException("No open spawn tile was found in this room.");
                _spawnPosition = (Vector2)GameAccess.Call(room, "MiddleOfTile", tile)!;
                Ready = true;
            }
            if (_group && !Committed) return null;
            var position = _spawnPosition;
            if (_crossRegion)
            {
                if (!_moveStarted)
                {
                    MoveRegion(position);
                    _moveStarted = true;
                    return null;
                }
                if (!RegionMoveSettled()) return null;
                RestoreMovingFlags();
            }
            else MovePlayer(room, position);
            WriteLog((_recover ? "recovery complete " : "teleport complete ") + _destinationName);
            return new() { Id = _command.Id, Success = true, Message = (_recover ? "Recovered in " : _crossRegion ? "Local players teleported to " : "Teleported to ") + _destinationName + "." };
        }
        catch (Exception exception)
        {
            RestoreMovingFlags();
            WriteLog("teleport failed " + exception.GetBaseException().Message);
            return new() { Id = _command.Id, Message = exception.GetBaseException().Message };
        }
    }

    private void CheckWorldTransition()
    {
        if (MeadowPlayers.IsOnline && GameAccess.Get(GameAccess.Call(GameAccess.FindType("RainMeadow.Extensions")!, "GetResource", _sourceWorld), "transitionInProgress") is true)
            throw new InvalidOperationException("Wait for the current Meadow region transition to finish.");
        if (GameAccess.Get(_overWorld, "worldLoader") != null || GameAccess.Get(_overWorld, "warpWorldLoader") != null
            || GameAccess.Get(_overWorld, "reportBackToGate") != null || GameAccess.Get(_overWorld, "readyForWarp") is true
            || GameAccess.Get(_overWorld, "warpingPreload") is true)
            throw new InvalidOperationException("Wait for the current region transition to finish.");
    }

    private void MoveRegion(Vector2 position)
    {
        var localPlayers = LocalCreatures(_game).ToArray();
        foreach (var player in localPlayers) ValidatePlayer(player);
        var allPlayers = new HashSet<object>(GameAccess.Items(GameAccess.Get(_game, "Players")));
        var movingObjects = localPlayers.SelectMany(player => GameAccess.Items(GameAccess.Call(player, "GetAllConnectedObjects")))
            .Where(item => !allPlayers.Contains(item) || localPlayers.Contains(item)).Distinct().ToArray();
        if (MeadowPlayers.IsOnline)
        {
            foreach (var item in movingObjects)
            {
                var online = GameAccess.Call(GameAccess.FindType("RainMeadow.Extensions")!, "GetOnlineObject", item);
                if (online != null && (GameAccess.Get(online, "isMine") is not true || GameAccess.Get(online, "isPending") is true))
                    throw new InvalidOperationException("Wait for ownership of carried objects to settle, or release them before teleporting.");
            }
        }
        var data = Activator.CreateInstance(GameAccess.FindType("Watcher.WarpPoint+WarpPointData")!, new object?[] { null })!;
        GameAccess.Set(data, "destRoom", GameAccess.Text(_destination, "name"));
        GameAccess.Set(data, "destPos", position);
        GameAccess.Set(data, "destTimeline", GameAccess.Get(_game, "TimelinePoint"));
        GameAccess.Set(data, "RegionString", _command.Region.ToUpperInvariant());
        GameAccess.Set(_overWorld, "specialWarpPointGoal", data);
        GameAccess.Set(_overWorld, "currentSpecialWarp", GameAccess.Get(GameAccess.FindType("OverWorld+SpecialWarpType"), "WARP_WARPPOINT"));
        GameAccess.Set(_overWorld, "worldLoader", _loader);
        _onlineObjects = MeadowPlayers.IsOnline ? movingObjects
            .Select(item => GameAccess.Call(GameAccess.FindType("RainMeadow.Extensions")!, "GetOnlineObject", item)).Where(item => item != null).Cast<object>().ToArray() : Array.Empty<object>();
        _movingStates = _onlineObjects.Select(item => GameAccess.Get(item, "beingMoved")).ToArray();
        var transientOnlineObjects = MeadowPlayers.IsOnline ? GameAccess.Items(GameAccess.Get(_game, "Players"))
            .Select(player => GameAccess.Call(GameAccess.FindType("RainMeadow.Extensions")!, "GetOnlineObject", player))
            .Where(online => online != null && !_onlineObjects.Contains(online)).Cast<object>().ToArray() : Array.Empty<object>();
        var transientMovingStates = transientOnlineObjects.Select(online => GameAccess.Get(online, "beingMoved")).ToArray();
        WriteLog("cross-region teleport " + GameAccess.Text(_sourceWorld, "name") + " -> " + GameAccess.Text(GameAccess.Get(_destination, "world"), "name"));
        try
        {
            foreach (var entity in _onlineObjects) GameAccess.Set(entity, "beingMoved", true);
            foreach (var entity in transientOnlineObjects) GameAccess.Set(entity, "beingMoved", true);
            try { GameAccess.Call(_overWorld, "WorldLoaded", false); }
            finally
            {
                for (int i = 0; i < transientOnlineObjects.Length; i++)
                    GameAccess.Set(transientOnlineObjects[i], "beingMoved", transientMovingStates[i]);
            }
            if (MeadowPlayers.IsOnline)
            {
                var extensions = GameAccess.FindType("RainMeadow.Extensions")!;
                var destinationResource = GameAccess.Call(extensions, "GetResource", GameAccess.Get(_destination, "world")!);
                var players = new HashSet<object>(GameAccess.Items(GameAccess.Get(_game, "Players")));
                _carriedObjects = movingObjects.Where(item => !players.Contains(item)).ToArray();
                if (destinationResource != null) RegisterCarriedObjects(destinationResource);
            }
        }
        catch
        {
            RestoreMovingFlags();
            throw;
        }
        _loader = null;
    }

    private bool RegionMoveSettled()
    {
        var destinationWorld = GameAccess.Get(_destination, "world");
        if (!ReferenceEquals(GameAccess.Get(_overWorld, "activeWorld"), destinationWorld)) return false;
        if (GameAccess.Get(_overWorld, "worldLoader") != null || GameAccess.Get(_overWorld, "reportBackToGate") != null) return false;
        if (MeadowPlayers.IsOnline)
        {
            var extensions = GameAccess.FindType("RainMeadow.Extensions")!;
            var resource = GameAccess.Call(extensions, "GetResource", destinationWorld!);
            if (resource == null) return false;
            RegisterCarriedObjects(resource);
            if (GameAccess.Get(resource, "transitionInProgress") is true || GameAccess.Get(resource, "isActive") is not true) return false;
            foreach (var online in _onlineObjects)
            {
                var entered = GameAccess.Get(online, "currentlyEnteredResource");
                var joined = GameAccess.Get(online, "currentlyJoinedResource");
                if (GameAccess.Get(online, "isPending") is true || entered == null || joined == null) return false;
                if (!InResourceTree(entered, resource!) || !InResourceTree(joined, resource!)) return false;
            }
            if (!ReferenceEquals(FindPlayer(_game, _command.PlayerId), _creature)) return false;
        }
        return ReferenceEquals(GameAccess.Get(_creature, "Room"), _destination)
            && GameAccess.Get(GameAccess.Get(_destination, "realizedRoom"), "ReadyForPlayer") is true;
    }

    private void MovePlayer(object room, Vector2 position)
    {
        if (_recover) PrepareForRecovery(room);
        var connected = GameAccess.Items(GameAccess.Call(_creature, "GetAllConnectedObjects")).ToArray();
        var coordinate = GameAccess.Call(room, "GetWorldCoordinate", position)!;
        if (MeadowPlayers.IsOnline)
        {
            var extensions = GameAccess.FindType("RainMeadow.Extensions")!;
            if (connected.Any(item => GameAccess.Call(extensions, "CanMove", item, coordinate, true) is not true))
                throw new InvalidOperationException("Rain Meadow has not granted ownership of the player or a held object. Release it and try again.");
        }
        GameAccess.Call(_creature, "Move", coordinate);
        if (!ReferenceEquals(GameAccess.Get(_creature, "Room"), _destination))
            throw new InvalidOperationException("The game rejected the room move.");
        foreach (var item in connected)
        {
            if (GameAccess.Get(item, "realizedObject") is not { } physical
                || GameAccess.Get(physical, "room") is not { } oldRoom || ReferenceEquals(oldRoom, room)) continue;
            GameAccess.Call(oldRoom, "RemoveObject", physical);
            GameAccess.Call(oldRoom, "CleanOutObjectNotInThisRoom", physical);
        }
        foreach (var item in connected)
        {
            if (GameAccess.Get(item, "realizedObject") is not { } physical) continue;
            if (!ReferenceEquals(GameAccess.Get(physical, "room"), room))
            {
                GameAccess.Call(physical, "PlaceInRoom", room);
            }
            foreach (var chunk in GameAccess.Items(GameAccess.Get(physical, "bodyChunks")))
            {
                GameAccess.Call(chunk, "HardSetPosition", position);
                GameAccess.Set(chunk, "vel", Vector2.zero);
            }
        }
        foreach (var camera in GameAccess.Items(GameAccess.Get(_game, "cameras")))
        {
            if (_recover)
            {
                var hud = GameAccess.Get(camera, "hud");
                foreach (var part in GameAccess.Items(GameAccess.Get(hud, "parts")))
                    if (part.GetType().FullName == "RainMeadow.SpectatorHud") GameAccess.Call(part, "ClearSpectatee");
                if (GameAccess.Get(hud, "textPrompt") is { } prompt) GameAccess.Set(prompt, "gameOverMode", false);
            }
            if (ReferenceEquals(GameAccess.Get(camera, "followAbstractCreature"), _creature)
                || _recover && ReferenceEquals(camera, GameAccess.Items(GameAccess.Get(_game, "cameras")).FirstOrDefault()))
            {
                if (_recover) GameAccess.Set(camera, "followAbstractCreature", _creature);
                GameAccess.Call(camera, "MoveCamera", room, -1);
            }
        }
    }

    private void PrepareForRecovery(object room)
    {
        ReviveAndDetach();
        var physical = GameAccess.Get(_creature, "realizedCreature");
        if (physical != null) return;
        var coordinate = GameAccess.Call(room, "GetWorldCoordinate", _spawnPosition)!;
        GameAccess.Call(_creature, "Move", coordinate);
        GameAccess.Call(_creature, "RealizeInRoom");
    }

    private void ReviveAndDetach()
    {
        if (MeadowPlayers.IsOnline) MeadowPlayers.RestoreRecoveryRegistration(_command.PlayerId, _game);
        var state = GameAccess.Get(_creature, "state") ?? throw new InvalidOperationException("The player state is unavailable.");
        RecoveryState.Revive(state);
        SetIfPresent(state, "permaDead", false);
        GameAccess.Set(_creature, "slatedForDeletion", false);
        foreach (var stick in GameAccess.Items(GameAccess.Get(_creature, "stuckObjects")).ToArray())
            if (ReferenceEquals(GameAccess.Get(stick, "B"), _creature)) GameAccess.Call(stick, "Deactivate");
        var physical = GameAccess.Get(_creature, "realizedCreature");
        var abstractRoom = GameAccess.Get(_creature, "Room");
        if (abstractRoom != null && GameAccess.Get(_creature, "InDen") is true)
            GameAccess.Call(abstractRoom, "MoveEntityOutOfDen", _creature);
        else if (abstractRoom != null && Contains(GameAccess.Get(abstractRoom, "entitiesInDens"), _creature))
        {
            GameAccess.Call(abstractRoom, "RemoveEntityFromDen", _creature);
            GameAccess.Call(abstractRoom, "AddEntity", _creature);
        }
        else if (abstractRoom != null && !Contains(GameAccess.Get(abstractRoom, "entities"), _creature))
            GameAccess.Call(abstractRoom, "AddEntity", _creature);
        if (physical != null)
        {
            GameAccess.Set(physical, "dead", false);
            RemoveFromShortcuts(physical);
            GameAccess.Set(physical, "inShortcut", false);
            SetIfPresent(physical, "inShortcutVessel", null);
            foreach (var grasp in GameAccess.Items(GameAccess.Get(physical, "grabbedBy")).ToArray())
            {
                var grabber = GameAccess.Get(grasp, "grabber");
                if (grabber != null) GameAccess.Call(grabber, "ReleaseGrasp", Convert.ToInt32(GameAccess.Get(grasp, "graspUsed")));
            }
            SetIfPresent(physical, "stun", 0);
            SetIfPresent(physical, "airInLungs", 1f);
            SetIfPresent(physical, "lungsExhausted", false);
            SetIfPresent(physical, "drown", 0f);
            SetIfPresent(physical, "exhausted", false);
            SetIfPresent(physical, "aerobicLevel", 0f);
            SetIfPresent(physical, "dangerGrasp", null);
            SetIfPresent(physical, "dangerGraspTime", 0);
            if (GameAccess.Get(physical, "slatedForDeletetion") is true)
            {
                if (GameAccess.Get(physical, "room") is { } oldRoom) GameAccess.Call(oldRoom, "RemoveObject", physical);
                GameAccess.Set(_creature, "realizedCreature", null);
            }
        }
    }

    private void RemoveFromShortcuts(object physical)
    {
        var shortcuts = GameAccess.Get(_game, "shortcuts");
        RemoveVessels(GameAccess.Get(shortcuts, "transportVessels"), physical);
        RemoveVessels(GameAccess.Get(shortcuts, "borderTravelVessels"), physical);
        RemoveVessels(GameAccess.Get(shortcuts, "betweenRoomsWaitingLobby"), physical);
    }

    private static void RemoveVessels(object? list, object physical)
    {
        if (list is not IList vessels) return;
        for (int i = vessels.Count - 1; i >= 0; i--)
            if (ReferenceEquals(GameAccess.Get(vessels[i], "creature"), physical)) vessels.RemoveAt(i);
    }

    private static bool Contains(object? list, object item) => list is IList values && values.Contains(item);

    private static void SetIfPresent(object target, string name, object? value)
    {
        for (var type = target.GetType(); type != null; type = type.BaseType)
        {
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            if (type.GetField(name, flags) != null || type.GetProperty(name, flags) != null)
            {
                GameAccess.Set(target, name, value);
                return;
            }
        }
    }

    internal static void Observe(object? game)
    {
        if (game == null)
        {
            LastRooms.Clear();
            _observedGame = null;
            return;
        }
        if (!ReferenceEquals(game, _observedGame))
        {
            LastRooms.Clear();
            _observedGame = game;
        }
        var activeWorld = GameAccess.Get(GameAccess.Get(game, "overWorld"), "activeWorld");
        foreach (var player in ReadLocalPlayers(game))
        {
            var room = GameAccess.Get(player.Creature, "Room");
            var physicalRoom = GameAccess.Get(GameAccess.Get(player.Creature, "realizedCreature"), "room");
            if (room != null && physicalRoom != null && GameAccess.Get(player.Creature, "InDen") is not true
                && ReferenceEquals(GameAccess.Get(room, "world"), activeWorld))
                LastRooms[player.Id] = GameAccess.Text(room, "name");
        }
    }

    private void RestoreMovingFlags()
    {
        for (int i = 0; i < _onlineObjects.Length; i++) GameAccess.Set(_onlineObjects[i], "beingMoved", _movingStates[i]);
        _onlineObjects = Array.Empty<object>();
        _carriedObjects = Array.Empty<object>();
        _movingStates = Array.Empty<object?>();
    }

    internal void Cancel()
    {
        RestoreMovingFlags();
        WriteLog("teleport cancelled " + _destinationName);
    }

    private void WriteLog(string message) => Log?.Invoke("Live command " + _command.Id + " " + message);

    private static bool InResourceTree(object candidate, object resource) => ReferenceEquals(candidate, resource)
        || GameAccess.Call(candidate, "IsSubresourceOf", resource) is true;

    private void RegisterCarriedObjects(object resource)
    {
        foreach (var item in _carriedObjects) GameAccess.Call(resource, "ApoEnteringWorld", item);
    }

    private static object? FindRoom(object world, string name) => GameAccess.Items(GameAccess.Get(world, "abstractRooms"))
        .FirstOrDefault(room => string.Equals(GameAccess.Text(room, "name"), name, StringComparison.OrdinalIgnoreCase));

    private static object? FindPlayer(object game, string id) => MeadowPlayers.IsOnline ? MeadowPlayers.FindLocal(id)
        : GameAccess.Items(GameAccess.Get(game, "Players")).FirstOrDefault(player => id == "local:" + GameAccess.Get(GameAccess.Get(player, "state"), "playerNumber"));

    private static IEnumerable<object> LocalCreatures(object game) => MeadowPlayers.IsOnline
        ? (MeadowPlayers.Read() ?? Array.Empty<LivePlayer>()).Where(p => p.IsLocal).Select(p => MeadowPlayers.FindLocal(p.Id)).Where(p => p != null).Cast<object>()
        : GameAccess.Items(GameAccess.Get(game, "Players"));

    private static IEnumerable<(string Id, object Creature)> ReadLocalPlayers(object game)
    {
        if (MeadowPlayers.IsOnline)
        {
            foreach (var player in MeadowPlayers.Read() ?? Array.Empty<LivePlayer>())
                if (player.IsLocal && MeadowPlayers.FindLocal(player.Id) is { } creature) yield return (player.Id, creature);
            yield break;
        }
        foreach (var creature in GameAccess.Items(GameAccess.Get(game, "Players")))
            yield return ("local:" + GameAccess.Get(GameAccess.Get(creature, "state"), "playerNumber"), creature);
    }

    private static void ValidatePlayer(object creature)
    {
        if (GameAccess.Get(GameAccess.Get(creature, "state"), "dead") is not false)
            throw new InvalidOperationException("The player must be alive to teleport.");
        var physical = GameAccess.Get(creature, "realizedCreature");
        if (physical == null || GameAccess.Get(physical, "room") == null || GameAccess.Get(physical, "inShortcut") is true)
            throw new InvalidOperationException("Wait until the player has left the shortcut and entered a room.");
        if (GameAccess.Items(GameAccess.Get(physical, "grabbedBy")).Any())
            throw new InvalidOperationException("The player must be released before teleporting.");
    }
}
