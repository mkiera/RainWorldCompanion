using RainWorldCompanion.LiveProtocol;

namespace RWCompanion.Mod;

internal sealed class GroupTeleportOperation
{
    private readonly List<TeleportOperation> _operations;
    private readonly string _id;
    internal bool Ready => _operations.All(operation => operation.Ready);
    internal bool Committed { get; set; }

    internal GroupTeleportOperation(string id, string room, string region, object game)
    {
        _id = id;
        var players = (MeadowPlayers.Read() ?? Array.Empty<LivePlayer>()).Where(p => p.IsLocal).ToArray();
        if (players.Length == 0) throw new InvalidOperationException("No local player is in gameplay.");
        if (players.Any(p => p.Dead != false || string.IsNullOrEmpty(p.RoomId))) throw new InvalidOperationException("Every local player must be alive and in a room.");
        bool crossing = players.Any(p => !string.Equals(p.Region, region, StringComparison.OrdinalIgnoreCase));
        _operations = (crossing ? players.Take(1) : players).Select(p => new TeleportOperation(new()
            { Id = id, PlayerId = p.Id, RoomId = room, Region = region }, game, group: true)).ToList();
    }

    internal LiveCommandResult? Update(object? game)
    {
        foreach (var operation in _operations.ToArray())
        {
            operation.Committed = Committed;
            if (operation.Update(game) is not { } result) continue;
            if (!result.Success) return result;
            _operations.Remove(operation);
        }
        return _operations.Count == 0 ? new() { Id = _id, Success = true, Message = "All local players arrived." } : null;
    }
}
