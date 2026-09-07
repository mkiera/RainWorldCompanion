using RainWorldCompanion.LiveProtocol;
using UnityEngine;

namespace RWCompanion.Mod;

internal sealed class TeleportOperation
{
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
    internal bool Ready { get; private set; }
    internal bool Committed { get; set; }

    internal TeleportOperation(LiveCommand command, object game, bool group = false)
    {
        _group = group;
        _command = command;
        _game = game;
        if (GameAccess.Get(game, "IsStorySession") is not true) throw new InvalidOperationException("Teleport is available during campaign gameplay.");
        _overWorld = GameAccess.Get(game, "overWorld") ?? throw new InvalidOperationException("World is unavailable.");
        _sourceWorld = GameAccess.Get(_overWorld, "activeWorld") ?? throw new InvalidOperationException("World is unavailable.");
        _creature = FindPlayer(game, command.PlayerId) ?? throw new InvalidOperationException("The local player is unavailable or ownership is changing.");
        ValidatePlayer(_creature);
        CheckWorldTransition();
        _destination = FindRoom(_sourceWorld, command.RoomId);
        if (_destination != null) return;
        if (MeadowPlayers.IsOnline && !group)
            throw new InvalidOperationException("Rain Meadow teleport currently supports rooms in your loaded region. Cross-region travel needs a synchronized lobby transition.");
        var region = GameAccess.Call(_overWorld, "GetRegion", command.Region.ToUpperInvariant());
        if (region == null) throw new InvalidOperationException("This region is unavailable in the current campaign.");
        foreach (var player in LocalCreatures(game)) ValidatePlayer(player);
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
            if (Time.unscaledTime - _started > 45) throw new InvalidOperationException("Destination loading timed out. The player has not been moved.");
            if (!ReferenceEquals(GameAccess.Get(_overWorld, "activeWorld"), _sourceWorld))
                throw new InvalidOperationException("The region changed before teleport completed.");
            CheckWorldTransition();
            ValidatePlayer(_creature);
            if (!ReferenceEquals(FindPlayer(_game, _command.PlayerId), _creature))
                throw new InvalidOperationException("Player ownership changed before teleport completed.");
            if (_loader != null)
            {
                if (GameAccess.Get(_loader, "Finished") is not true) GameAccess.Call(_loader, "Update");
                if (GameAccess.Get(_loader, "Finished") is not true) return null;
                var world = GameAccess.Call(_loader, "ReturnWorld")!;
                _destination = FindRoom(world, _command.RoomId)
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
            if (_crossRegion) MoveRegion(position);
            else MovePlayer(room, position);
            return new() { Id = _command.Id, Success = true, Message = (_crossRegion ? "Local players teleported to " : "Teleported to ") + _command.RoomId + "." };
        }
        catch (Exception exception)
        {
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
        foreach (var player in LocalCreatures(_game)) ValidatePlayer(player);
        var data = Activator.CreateInstance(GameAccess.FindType("Watcher.WarpPoint+WarpPointData")!, new object?[] { null })!;
        GameAccess.Set(data, "destRoom", GameAccess.Text(_destination, "name"));
        GameAccess.Set(data, "destPos", position);
        GameAccess.Set(data, "destTimeline", GameAccess.Get(_game, "TimelinePoint"));
        GameAccess.Set(data, "RegionString", _command.Region.ToUpperInvariant());
        GameAccess.Set(_overWorld, "specialWarpPointGoal", data);
        GameAccess.Set(_overWorld, "currentSpecialWarp", GameAccess.Get(GameAccess.FindType("OverWorld+SpecialWarpType"), "WARP_WARPPOINT"));
        GameAccess.Set(_overWorld, "worldLoader", _loader);
        var onlineObjects = MeadowPlayers.IsOnline ? GameAccess.Items(GameAccess.Get(_game, "Players"))
            .Select(p => GameAccess.Call(GameAccess.FindType("RainMeadow.Extensions")!, "GetOnlineObject", p)).Where(p => p != null).ToArray() : Array.Empty<object>();
        var moving = onlineObjects.Select(p => GameAccess.Get(p, "beingMoved")).ToArray();
        try
        {
            foreach (var entity in onlineObjects) GameAccess.Set(entity!, "beingMoved", true);
            GameAccess.Call(_overWorld, "WorldLoaded", false);
        }
        finally { for (int i = 0; i < onlineObjects.Length; i++) GameAccess.Set(onlineObjects[i]!, "beingMoved", moving[i]); }
        if (!ReferenceEquals(GameAccess.Get(_overWorld, "activeWorld"), GameAccess.Get(_destination, "world")))
            throw new InvalidOperationException("Meadow deferred the region transition. Check the lobby's location before retrying.");
        _loader = null;
    }

    private void MovePlayer(object room, Vector2 position)
    {
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
            if (ReferenceEquals(GameAccess.Get(camera, "followAbstractCreature"), _creature))
                GameAccess.Call(camera, "MoveCamera", room, -1);
        }
    }

    private static object? FindRoom(object world, string name) => GameAccess.Items(GameAccess.Get(world, "abstractRooms"))
        .FirstOrDefault(room => string.Equals(GameAccess.Text(room, "name"), name, StringComparison.OrdinalIgnoreCase));

    private static object? FindPlayer(object game, string id) => MeadowPlayers.IsOnline ? MeadowPlayers.FindLocal(id)
        : GameAccess.Items(GameAccess.Get(game, "Players")).FirstOrDefault(player => id == "local:" + GameAccess.Get(GameAccess.Get(player, "state"), "playerNumber"));

    private static IEnumerable<object> LocalCreatures(object game) => MeadowPlayers.IsOnline
        ? (MeadowPlayers.Read() ?? Array.Empty<LivePlayer>()).Where(p => p.IsLocal).Select(p => MeadowPlayers.FindLocal(p.Id)).Where(p => p != null).Cast<object>()
        : GameAccess.Items(GameAccess.Get(game, "Players"));

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
