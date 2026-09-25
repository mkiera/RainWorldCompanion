using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace RWCompanion.Mod;

internal static class MeadowRpc
{
    // This key identifies version 1 of the optional host-control wire format across mod builds.
    private static readonly Guid Key = new("2bed0721-cf5e-4465-b423-27bfce070439");
    internal static Action<object, string>? Received;
    private static readonly Action<object, string> Handler = Receive;

    internal static bool Register()
    {
        var manager = GameAccess.FindType("RainMeadow.RPCManager");
        if (manager == null) return false;
        var clients = (IDictionary)GameAccess.Get(manager, "softHandlerClients")!;
        if (clients.Contains(Key)) return true;
        var definitionType = GameAccess.FindType("RainMeadow.RPCManager+SoftRPCDefinition")!;
        var definition = Activator.CreateInstance(definitionType)!;
        var rpcType = GameAccess.FindType("RainMeadow.RPCEvent")!;
        var serializerType = GameAccess.FindType("RainMeadow.Serializer")!;
        var rpc = Expression.Parameter(rpcType);
        var serializer = Expression.Parameter(serializerType);
        var serialize = Expression.Lambda(typeof(Action<,>).MakeGenericType(rpcType, serializerType),
            Expression.Call(typeof(MeadowRpc).GetMethod(nameof(Serialize), BindingFlags.Static | BindingFlags.NonPublic)!,
                Expression.Convert(rpc, typeof(object)), Expression.Convert(serializer, typeof(object))), rpc, serializer).Compile();
        GameAccess.Set(definition, "key", Key);
        GameAccess.Set(definition, "index", (ushort)0);
        GameAccess.Set(definition, "method", Handler.Method);
        GameAccess.Set(definition, "serialize", serialize);
        GameAccess.Set(definition, "eventArgIndex", 0);
        GameAccess.Set(definition, "isStatic", true);
        GameAccess.Set(definition, "runDeferred", true);
        GameAccess.Set(definition, "security", Enum.Parse(GameAccess.FindType("RainMeadow.RPCSecurity")!, "InLobby"));
        GameAccess.Set(definition, "summary", "rwcompanion host control");
        var handlers = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(ushort), definitionType))!;
        handlers.Add((ushort)0, definition);
        ((IDictionary)GameAccess.Get(manager, "defsByMethod")!)[Handler.Method] = definition;
        clients.Add(Key, handlers);
        return true;
    }

    internal static void Send(object peer, string message)
    {
        if (Encoding.UTF8.GetByteCount(message) > 4096) throw new IOException("Host request exceeds the size limit.");
        GameAccess.Call(peer, "InvokeRPC", Handler, new object[] { message });
    }

    private static void Receive(object rpc, string message)
    {
        if (message.Length <= 4096 && GameAccess.Get(rpc, "from") is { } sender) Received?.Invoke(sender, message);
    }

    private static void Serialize(object rpc, object serializer)
    {
        if (GameAccess.Get(serializer, "IsReading") is true)
        {
            var reader = (BinaryReader)GameAccess.Get(serializer, "reader")!;
            int length = reader.ReadUInt16();
            if (length > 4096) throw new IOException("Host request exceeds the size limit.");
            byte[] bytes = reader.ReadBytes(length);
            if (bytes.Length != length) throw new EndOfStreamException();
            GameAccess.Set(rpc, "args", new object[] { Encoding.UTF8.GetString(bytes) });
        }
        else
        {
            var bytes = Encoding.UTF8.GetBytes((string)((object[])GameAccess.Get(rpc, "args")!)[0]);
            if (bytes.Length > 4096) throw new IOException("Host request exceeds the size limit.");
            var writer = (BinaryWriter)GameAccess.Get(serializer, "writer")!;
            writer.Write((ushort)bytes.Length);
            writer.Write(bytes);
        }
    }
}
