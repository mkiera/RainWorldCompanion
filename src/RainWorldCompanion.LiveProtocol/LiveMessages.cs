using System.Runtime.Serialization.Json;
using System.Text;

namespace RainWorldCompanion.LiveProtocol;

public static partial class ProtocolInfo
{
    public const int Version = 1;
    public const int MaximumMessageLength = 262144;
    public static string DiscoveryDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RainWorldCompanion", "live");
}

public sealed class LiveDiscovery
{
    public int Port { get; set; }
    public string Token { get; set; } = "";
    public int ProtocolVersion { get; set; } = ProtocolInfo.Version;
}

public sealed class LiveSnapshot
{
    public bool HasExplorationData { get; set; }
    public string[] VisitedRooms { get; set; } = Array.Empty<string>();
    public int CommandVersion { get; set; }
    public string GameplayId { get; set; } = "";
    public bool IsOnline { get; set; }
    public bool IsHost { get; set; }
    public bool SupportsTeleportAll { get; set; }
    public string TeleportAllUnavailableReason { get; set; } = "";
    public string HostActionText { get; set; } = "";
    public bool AllowHostControl { get; set; }
    public LiveCommandResult? CommandResult { get; set; }
    public string Token { get; set; } = "";
    public string SessionId { get; set; } = "";
    public long Sequence { get; set; }
    public int ProtocolVersion { get; set; } = ProtocolInfo.Version;
    public string ModVersion { get; set; } = ProtocolInfo.ModVersion;
    public string GameVersion { get; set; } = "";
    public string GameInstallPath { get; set; } = "";
    public string Campaign { get; set; } = "";
    public string Timeline { get; set; } = "";
    public string State { get; set; } = "menu";
    public string[] EnabledExpansions { get; set; } = Array.Empty<string>();
    public LivePlayer[] Players { get; set; } = Array.Empty<LivePlayer>();
}

public sealed class LiveCommand
{
    public bool TeleportAll { get; set; }
    public bool? AllowHostControl { get; set; }
    public string Id { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string GameplayId { get; set; } = "";
    public string PlayerId { get; set; } = "";
    public string RoomId { get; set; } = "";
    public string Region { get; set; } = "";
    public long ExpiresUtcTicks { get; set; }
}

public sealed class LiveCommandReply
{
    public string Token { get; set; } = "";
    public LiveCommand? Command { get; set; }
}

public sealed class LiveCommandResult
{
    public string Id { get; set; } = "";
    public bool Success { get; set; }
    public string Message { get; set; } = "";
}

public sealed class LivePlayer
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? RoomId { get; set; }
    public string? Region { get; set; }
    public bool? Dead { get; set; }
    public bool IsLocal { get; set; }
    public bool AllowsHostControl { get; set; }
    public string? CompanionVersion { get; set; }
    public bool IsHost { get; set; }
}

public static class LiveJson
{
    public static string Serialize<T>(T value)
    {
        using var stream = new MemoryStream();
        new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static T Deserialize<T>(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return (T)(new DataContractJsonSerializer(typeof(T)).ReadObject(stream) ?? throw new System.Runtime.Serialization.SerializationException("Live message is empty."));
    }
}
