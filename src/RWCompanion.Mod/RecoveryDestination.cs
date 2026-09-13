namespace RWCompanion.Mod;

internal static class RecoveryDestination
{
    internal static string? Choose(string? currentRoom, string? observedRoom, IEnumerable<string> activeRooms)
    {
        var rooms = new HashSet<string>(activeRooms, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(currentRoom) && rooms.Contains(currentRoom!)) return currentRoom;
        return !string.IsNullOrEmpty(observedRoom) && rooms.Contains(observedRoom!) ? observedRoom : null;
    }
}
