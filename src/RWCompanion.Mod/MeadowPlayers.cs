using System.Collections;
using System.Reflection;
using RainWorldCompanion.LiveProtocol;

namespace RWCompanion.Mod;

internal static class MeadowPlayers
{
    private static readonly Dictionary<string, object> LocalCreatures = new();
    private static readonly Dictionary<string, object> Owners = new();
    internal static object? Lobby => GameAccess.Get(GameAccess.FindType("RainMeadow.OnlineManager"), "lobby");
    internal static bool IsHost => GameAccess.Get(GameAccess.Get(Lobby, "owner"), "isMe") is true;
    internal static object? Owner(string id) { Read(); return Owners.TryGetValue(id, out var owner) ? owner : null; }
    internal static bool IsOnline => ReadMember(GameAccess.FindType("RainMeadow.OnlineManager"), "lobby") != null;
    internal static object? FindLocal(string id)
    {
        Read();
        return LocalCreatures.TryGetValue(id, out var creature) ? creature : null;
    }
    private static object? ReadMember(object? target, string name)
    {
        if (target == null) return null;
        var type = target as Type ?? target.GetType();
        var instance = target is Type ? null : target;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        return type.GetField(name, flags)?.GetValue(instance) ?? type.GetProperty(name, flags)?.GetValue(instance, null);
    }

    internal static LivePlayer[]? Read()
    {
        LocalCreatures.Clear();
        Owners.Clear();
        var manager = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("RainMeadow.OnlineManager", false)).FirstOrDefault(type => type != null);
        var lobby = ReadMember(manager, "lobby");
        if (lobby == null) return null;
        var result = new List<LivePlayer>();
        var settings = ReadMember(lobby, "clientSettings") as IDictionary ?? new Hashtable();
        foreach (DictionaryEntry entry in settings)
        {
            var owner = entry.Key;
            if (ReadMember(owner, "hasLeft") is true) continue;
            string ownerId = Convert.ToString(ReadMember(owner, "inLobbyId")) ?? "";
            var identity = ReadMember(owner, "id");
            string name = Convert.ToString(ReadMember(identity, "DisplayName")) ?? "Player " + ownerId;
            bool local = ReadMember(owner, "isMe") is true;
            int index = 0;
            if (ReadMember(entry.Value, "avatars") is IEnumerable avatars)
            {
                foreach (var avatarId in avatars)
                {
                    var entity = avatarId.GetType().GetMethod("FindEntity")?.Invoke(avatarId, new object[] { true });
                    var creature = ReadMember(entity, "apo");
                    string id = "meadow:" + ownerId + ":" + avatarId;
                    Owners[id] = owner!;
                    if (local && creature != null && ReadMember(entity, "isMine") is true && ReadMember(entity, "isPending") is false)
                        LocalCreatures[id] = creature;
                    result.Add(Plugin.FromCreature(creature, id, name + (index == 0 ? "" : " (" + (index + 1) + ")"), local));
                    index++;
                }
            }
            if (index == 0)
            {
                Owners["meadow:" + ownerId] = owner!;
                result.Add(Plugin.FromCreature(null, "meadow:" + ownerId, name, local));
            }
        }
        foreach (var owner in GameAccess.Items(GameAccess.Get(lobby, "participants")))
        {
            if (GameAccess.Get(owner, "hasLeft") is true || Owners.Values.Any(value => ReferenceEquals(value, owner))) continue;
            string id = "meadow:" + GameAccess.Text(owner, "inLobbyId");
            Owners[id] = owner;
            string name = GameAccess.Text(GameAccess.Get(owner, "id"), "DisplayName");
            result.Add(Plugin.FromCreature(null, id, string.IsNullOrEmpty(name) ? id : name, GameAccess.Get(owner, "isMe") is true));
        }
        foreach (var player in result) player.IsHost = ReferenceEquals(Owners[player.Id], GameAccess.Get(lobby, "owner"));
        return result.ToArray();
    }
}
