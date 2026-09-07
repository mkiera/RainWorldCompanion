using RainWorldCompanion.LiveProtocol;

namespace RWCompanion.Mod;

internal static class VisitedRooms
{
    internal static void Read(object? session, LiveSnapshot snapshot)
    {
        var save = GameAccess.Get(session, "saveState");
        if (save == null) return;
        var rooms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var states = GameAccess.Items(GameAccess.Get(save, "regionStates")).ToArray();
        var saved = GameAccess.Items(GameAccess.Get(save, "regionLoadStrings")).ToArray();
        for (int index = 0; index < Math.Max(states.Length, saved.Length); index++)
        {
            if (index < states.Length && states[index] is { } state)
            {
                foreach (var room in GameAccess.Items(GameAccess.Get(state, "roomsVisited")))
                    if (room is string name && name.Length > 0) rooms.Add(name);
                continue;
            }
            if (index >= saved.Length || saved[index] is not string text) continue;
            string region = "";
            string visits = "";
            foreach (var field in text.Split(new[] { "<rgA>" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = field.Split(new[] { "<rgB>" }, StringSplitOptions.None);
                if (pair.Length < 2) continue;
                if (pair[0] == "REGIONNAME") region = pair[1];
                if (pair[0] == "ROOMSVISITED") visits = pair[1];
            }
            if (visits.Length == 0) continue;
            if (visits.All(char.IsDigit))
            {
                var legacy = new List<string>();
                GameAccess.Call(GameAccess.FindType("BackwardsCompatibilityRemix")!, "ParseRoomsVisited",
                    GameAccess.Get(save, "worldVersion"), region, visits, legacy);
                rooms.UnionWith(legacy);
            }
            else rooms.UnionWith(visits.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
        }
        snapshot.VisitedRooms = rooms.OrderBy(room => room, StringComparer.OrdinalIgnoreCase).ToArray();
        snapshot.HasExplorationData = true;
    }
}
