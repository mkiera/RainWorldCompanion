using System.Collections;
using System.Reflection;
using RainWorldCompanion.LiveProtocol;

namespace RWCompanion.Mod;

internal static class MeadowPlayers
{
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
        var manager = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("RainMeadow.OnlineManager", false)).FirstOrDefault(type => type != null);
        var lobby = ReadMember(manager, "lobby");
        if (lobby == null) return null;
        var result = new List<LivePlayer>();
        if (ReadMember(lobby, "clientSettings") is not IDictionary settings) return result.ToArray();
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
                    result.Add(Plugin.FromCreature(creature, "meadow:" + ownerId + ":" + avatarId, name + (index == 0 ? "" : " (" + (index + 1) + ")"), local));
                    index++;
                }
            }
            if (index == 0) result.Add(Plugin.FromCreature(null, "meadow:" + ownerId, name, local));
        }
        return result.ToArray();
    }
}
