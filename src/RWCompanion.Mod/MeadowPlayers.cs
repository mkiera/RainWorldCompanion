using System.Collections;
using System.Reflection;
using RainWorldCompanion.LiveProtocol;

namespace RWCompanion.Mod;

internal static class MeadowPlayers
{
    private static readonly Dictionary<string, object> LocalCreatures = new();
    private static readonly Dictionary<string, object> Owners = new();
    internal static object? Lobby => ReadMember(GameAccess.FindType("RainMeadow.OnlineManager"), "lobby");
    internal static bool IsHost => ReadMember(ReadMember(Lobby, "owner"), "isMe") is true;
    internal static object? Owner(string id) { Read(); return Owners.TryGetValue(id, out var owner) ? owner : null; }
    internal static bool IsOnline => ReadMember(GameAccess.FindType("RainMeadow.OnlineManager"), "lobby") != null;
    internal static object? FindLocal(string id)
    {
        Read();
        return LocalCreatures.TryGetValue(id, out var creature) ? creature : null;
    }

    internal static void RestoreRecoveryRegistration(string id, object game)
    {
        var creature = FindLocal(id) ?? throw new InvalidOperationException("Your local player is not available for recovery yet.");
        if (!ReferenceEquals(GameAccess.Get(GameAccess.Get(creature, "world"), "game"), game))
            throw new InvalidOperationException("The player belongs to an earlier game session.");
        var extensions = GameAccess.FindType("RainMeadow.Extensions")!;
        if (GameAccess.Call(extensions, "GetOnlineObject", creature) != null) return;
        var owner = Owners[id];
        var avatar = GameAccess.Items(GameAccess.Get(GameAccess.Get(Lobby, "gameMode"), "avatars"))
            .FirstOrDefault(value => ReferenceEquals(GameAccess.Get(value, "apo"), creature)
                && ReferenceEquals(GameAccess.Get(value, "owner"), owner));
        if (avatar == null || GameAccess.Get(avatar, "isMine") is not true || GameAccess.Get(avatar, "isPending") is not false
            || GameAccess.Get(avatar, "primaryResource") != null)
            throw new InvalidOperationException("Wait for the player's previous network registration to finish closing.");
        var entityId = GameAccess.Get(avatar, "id")!;
        var entities = (IDictionary)GameAccess.Get(GameAccess.FindType("RainMeadow.OnlineManager"), "recentEntities")!;
        if (entities.Contains(entityId))
            throw new InvalidOperationException("The player is still being removed from the previous room. Try recovery again.");
        var map = GameAccess.Get(GameAccess.FindType("RainMeadow.OnlinePhysicalObject"), "map")!;
        GameAccess.Call(map, "Add", creature, avatar);
        try { entities.Add(entityId, avatar); }
        catch { GameAccess.Call(map, "Remove", creature); throw; }
        GameAccess.Set(avatar, "realized", false);
    }
    private static object? ReadMember(object? target, string name)
    {
        if (target == null) return null;
        try
        {
            var type = target as Type ?? target.GetType();
            var instance = target is Type ? null : target;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            return type.GetField(name, flags)?.GetValue(instance) ?? type.GetProperty(name, flags)?.GetValue(instance, null);
        }
        catch (Exception) { return null; }
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
            string steamId = UniqueId(owner);
            ushort? peerId = ReadMember(owner, "inLobbyId") is ushort value ? value : null;
            int index = 0;
            if (ReadMember(entry.Value, "avatars") is IEnumerable avatars)
            {
                foreach (var avatarId in avatars)
                {
                    object? entity = null;
                    try { entity = avatarId.GetType().GetMethod("FindEntity")?.Invoke(avatarId, new object[] { true }); }
                    catch (Exception error) when (error is TargetInvocationException or ArgumentException
                        or MissingMethodException or InvalidOperationException) { }
                    if (entity == null && local)
                    {
                        entity = GameAccess.Items(ReadMember(ReadMember(lobby, "gameMode"), "avatars"))
                            .FirstOrDefault(avatar => Equals(ReadMember(avatar, "id"), avatarId)
                                && ReferenceEquals(ReadMember(avatar, "owner"), owner)
                                && ReadMember(avatar, "isMine") is true
                                && ReadMember(ReadMember(ReadMember(avatar, "apo"), "state"), "dead") is true);
                    }
                    var creature = ReadMember(entity, "apo");
                    string avatarText = Convert.ToString(avatarId) ?? "";
                    string boundedAvatarText = BoundText(avatarText, ProtocolInfo.MaximumMeadowAvatarIdLength);
                    string id = "meadow:" + ownerId + ":" + boundedAvatarText;
                    Owners[id] = owner!;
                    if (local && creature != null && ReadMember(entity, "isMine") is true && ReadMember(entity, "isPending") is false)
                        LocalCreatures[id] = creature;
                    var player = Plugin.FromCreature(creature, id, name + (index == 0 ? "" : " (" + (index + 1) + ")"), local);
                    player.MeadowAvatarId = boundedAvatarText;
                    if (ReadMember(creature, "slatedForDeletion") is true) player.RoomId = null;
                    DescribeNative(player, steamId, peerId, entry.Value, entity, creature);
                    if (player.Dead == null && ClientIsDead(entry.Value)) player.Dead = true;
                    result.Add(player);
                    index++;
                }
            }
            if (index == 0)
            {
                Owners["meadow:" + ownerId] = owner!;
                var player = Plugin.FromCreature(null, "meadow:" + ownerId, name, local);
                DescribeNative(player, steamId, peerId, entry.Value, null, null);
                result.Add(player);
            }
        }
        foreach (var owner in GameAccess.Items(ReadMember(lobby, "participants")))
        {
            if (ReadMember(owner, "hasLeft") is true || Owners.Values.Any(value => ReferenceEquals(value, owner))) continue;
            string id = "meadow:" + Convert.ToString(ReadMember(owner, "inLobbyId"));
            Owners[id] = owner;
            string name = Convert.ToString(ReadMember(ReadMember(owner, "id"), "DisplayName")) ?? "";
            var player = Plugin.FromCreature(null, id, string.IsNullOrEmpty(name) ? id : name, ReadMember(owner, "isMe") is true);
            DescribeNative(player, UniqueId(owner), ReadMember(owner, "inLobbyId") is ushort value ? value : null,
                null, null, null);
            result.Add(player);
        }
        foreach (var player in result) player.IsHost = ReferenceEquals(Owners[player.Id], ReadMember(lobby, "owner"));
        return result.ToArray();
    }

    private static bool ClientIsDead(object? settings)
    {
        if (ReadMember(settings, "inGame") is not true || GameAccess.FindType("RainMeadow.StoryClientSettingsData") is not { } dataType) return false;
        object?[] arguments = { dataType, null };
        try { return GameAccess.Call(settings!, "TryGetData", arguments) is true && ReadMember(arguments[1], "isDead") is true; }
        catch (Exception) { return false; }
    }

    private static void DescribeNative(
        LivePlayer player,
        string steamId,
        ushort? peerId,
        object? settings,
        object? entity,
        object? creature)
    {
        player.MeadowSteamId = steamId;
        player.MeadowPeerId = peerId;
        player.NativeEntityAvailable = entity != null && creature != null;
        player.NativeLocationAvailability = settings == null
            ? "client-settings-unavailable"
            : ReadMember(settings, "inGame") is not true
                ? "not-in-game"
                : creature == null
                    ? player.MeadowAvatarId == null ? "avatar-not-declared" : "entity-unresolved"
                    : string.IsNullOrWhiteSpace(player.RoomId)
                        ? "room-unknown"
                        : "available";
        player.InDen = creature == null ? null : ReadMember(creature, "InDen") as bool?
            ?? ReadMember(creature, "inDen") as bool?;
    }

    private static string UniqueId(object owner)
    {
        try { return Convert.ToString(GameAccess.Call(owner, "GetUniqueID")) ?? ""; }
        catch (Exception) { return ""; }
    }

    private static string BoundText(string value, int maximum)
    {
        string bounded = new(value.Where(character => !char.IsControl(character)).Take(maximum).ToArray());
        return bounded.Length > 0 && char.IsHighSurrogate(bounded[bounded.Length - 1])
            ? bounded.Substring(0, bounded.Length - 1)
            : bounded;
    }
}
