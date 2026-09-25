using System.Runtime.Serialization.Json;
using System.Text;

namespace RainWorldCompanion.LiveProtocol;

public static partial class ProtocolInfo
{
    public const int Version = 1;
    public const int MaximumMessageLength = 262144;
    public const int LogStreamingVersion = 3;
    public const int MaximumLogBridgeMessageLength = 524288;
    public const int MaximumLogPacketLength = 32768;
    public const int MaximumLogPacketsPerBridgeExchange = 6;
    public const int MaximumActiveMods = 256;
    public const int MaximumModIdLength = 64;
    public const int MaximumModDisplayNameLength = 96;
    public const int MaximumModVersionLength = 32;
    public const int MaximumMeadowPeers = 32;
    public const int MaximumMeadowAvatarsPerPeer = 4;
    public const int MaximumMeadowModIds = 64;
    public const int MaximumMeadowModIdLength = 64;
    public const int MaximumMeadowLobbyOptions = 32;
    public const int MaximumMeadowLobbyOptionNameLength = 64;
    public const int MaximumMeadowLobbyOptionValueLength = 32;
    public const int MaximumMeadowDisplayNameLength = 128;
    public const int MaximumMeadowAvatarIdLength = 64;
    public const int MaximumMeadowLabelLength = 64;
    public const int MaximumMeadowConnectionStateLength = 32;
    public static string DiscoveryDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RainWorldCompanion", "live");
}

public sealed class LiveDiscovery
{
    public int Port { get; set; }
    public string Token { get; set; } = "";
    public int ProtocolVersion { get; set; } = ProtocolInfo.Version;
    public int LogPort { get; set; }
    public string LogToken { get; set; } = "";
    public int LogProtocolVersion { get; set; } = ProtocolInfo.LogStreamingVersion;
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
    public bool SupportsRecovery { get; set; }
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
    public LiveModInfo[] ActiveMods { get; set; } = Array.Empty<LiveModInfo>();
    public bool ActiveModsTruncated { get; set; }
    public LivePlayer[] Players { get; set; } = Array.Empty<LivePlayer>();
    public LiveMeadowSnapshot? Meadow { get; set; }
    public LiveTrace? Trace { get; set; }
}

public sealed class LiveModInfo
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Version { get; set; } = "";
    public string CodeFingerprint { get; set; } = "";
    public string FingerprintStatus { get; set; } = "unavailable";
}

public sealed class LiveCommand
{
    public bool Recover { get; set; }
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
    public string MeadowSteamId { get; set; } = "";
    public ushort? MeadowPeerId { get; set; }
    public string? MeadowAvatarId { get; set; }
    public bool? NativeEntityAvailable { get; set; }
    public string NativeLocationAvailability { get; set; } = "";
    public bool? InDen { get; set; }
    public LivePlayerTrace? Trace { get; set; }
}

public sealed class LiveMeadowSnapshot
{
    public int SchemaVersion { get; set; } = 1;
    public string LobbyId { get; set; } = "";
    public string ObserverSteamId { get; set; } = "";
    public string GameMode { get; set; } = "";
    public string Timeline { get; set; } = "";
    public string[] RequiredMods { get; set; } = Array.Empty<string>();
    public string[] BannedMods { get; set; } = Array.Empty<string>();
    public bool? WhitelistMode { get; set; }
    public bool? CheatsEnabled { get; set; }
    public bool ConfigurationTruncated { get; set; }
    public bool RosterTruncated { get; set; }
    public LiveMeadowLobbyOption[] LobbyOptions { get; set; } = Array.Empty<LiveMeadowLobbyOption>();
    public LiveMeadowPeer[] Peers { get; set; } = Array.Empty<LiveMeadowPeer>();
}

public sealed class LiveMeadowLobbyOption
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}

public sealed class LiveMeadowPeer
{
    public string SteamId { get; set; } = "";
    public ushort LobbyPeerId { get; set; }
    public string DisplayName { get; set; } = "";
    public bool IsLocal { get; set; }
    public bool IsHost { get; set; }
    public bool SupportsGameHookPackets { get; set; }
    public bool? InGame { get; set; }
    public bool? EnteringChat { get; set; }
    public int? AvatarCount { get; set; }
    public string[] AvatarIds { get; set; } = Array.Empty<string>();
    public bool? StoryReadyForWin { get; set; }
    public bool? StoryReadyForTransition { get; set; }
    public bool? StoryDead { get; set; }
    public bool? IsSpectating { get; set; }
    public bool? NeedsAcknowledgement { get; set; }
    public int? PingMilliseconds { get; set; }
    public int? IncomingBytesPerSecond { get; set; }
    public int? OutgoingBytesPerSecond { get; set; }
    public uint? RemoteTick { get; set; }
    public uint? LatestAcknowledgedTick { get; set; }
    public int? OutgoingEventCount { get; set; }
    public int? OutgoingStateCount { get; set; }
    public bool? EventsRead { get; set; }
    public bool? StatesRead { get; set; }
    public bool? EventsWritten { get; set; }
    public bool? StatesWritten { get; set; }
    public LiveMeadowConnection? Connection { get; set; }
}

public sealed class LiveMeadowConnection
{
    public string State { get; set; } = "";
    public int? PingMilliseconds { get; set; }
    public float? LocalDeliveryQuality { get; set; }
    public float? RemoteDeliveryQuality { get; set; }
    public float? IncomingPacketsPerSecond { get; set; }
    public float? OutgoingPacketsPerSecond { get; set; }
    public float? IncomingBytesPerSecond { get; set; }
    public float? OutgoingBytesPerSecond { get; set; }
    public int? EstimatedSendRateBytesPerSecond { get; set; }
    public int? PendingUnreliableBytes { get; set; }
    public int? PendingReliableBytes { get; set; }
    public int? UnacknowledgedReliableBytes { get; set; }
    public int? QueueTimeMicroseconds { get; set; }
}

public sealed class LiveTrace
{
    public string Process { get; set; } = "";
    public int Frame { get; set; }
    public float UnscaledDeltaSeconds { get; set; }
    public float TimeScale { get; set; }
    public bool? IsFocused { get; set; }
    public long ManagedMemoryBytes { get; set; }
    public LiveFramePerformance? Performance { get; set; }
    public int? Cycle { get; set; }
    public int? Karma { get; set; }
    public int? KarmaCap { get; set; }
    public int? RainTimer { get; set; }
    public int? RainCycleLength { get; set; }
}

public sealed class LiveFramePerformance
{
    public long Sequence { get; set; }
    public string GameplayId { get; set; } = "";
    public bool Ready { get; set; }
    public double DurationSeconds { get; set; }
    public int FrameCount { get; set; }
    public double MaximumFrameMilliseconds { get; set; }
    public int FramesOver250Milliseconds { get; set; }
    public int FramesOver500Milliseconds { get; set; }
    public int? GarbageCollections { get; set; }
}

public sealed class LivePlayerTrace
{
    public bool Realized { get; set; }
    public bool SlatedForDeletion { get; set; }
    public bool InShortcut { get; set; }
    public float? PositionX { get; set; }
    public float? PositionY { get; set; }
    public float? VelocityX { get; set; }
    public float? VelocityY { get; set; }
    public int? AbstractX { get; set; }
    public int? AbstractY { get; set; }
    public int? AbstractNode { get; set; }
    public int? Stun { get; set; }
    public float? AirInLungs { get; set; }
    public int? FoodInStomach { get; set; }
    public LiveInputTrace? Input { get; set; }
}

public sealed class LiveInputTrace
{
    public int X { get; set; }
    public int Y { get; set; }
    public bool Jump { get; set; }
    public bool Throw { get; set; }
    public bool Pickup { get; set; }
    public bool Map { get; set; }
}

public static class LiveJson
{
    public static string Serialize<T>(T value)
    {
        using var stream = new MemoryStream();
        new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string SerializeLiveSnapshot(LiveSnapshot snapshot)
    {
        if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
        string json = Serialize(snapshot);
        if (Encoding.UTF8.GetByteCount(json) <= ProtocolInfo.MaximumMessageLength) return json;

        LiveModInfo[] activeMods = snapshot.ActiveMods ?? Array.Empty<LiveModInfo>();
        bool wasTruncated = snapshot.ActiveModsTruncated;
        int minimum = 0;
        int maximum = activeMods.Length;
        int accepted = -1;
        string? acceptedJson = null;
        while (minimum <= maximum)
        {
            int count = minimum + (maximum - minimum) / 2;
            snapshot.ActiveMods = count == activeMods.Length ? activeMods : activeMods.Take(count).ToArray();
            snapshot.ActiveModsTruncated = wasTruncated || count < activeMods.Length;
            string candidate = Serialize(snapshot);
            if (Encoding.UTF8.GetByteCount(candidate) <= ProtocolInfo.MaximumMessageLength)
            {
                accepted = count;
                acceptedJson = candidate;
                minimum = count + 1;
            }
            else maximum = count - 1;
        }

        if (acceptedJson == null)
            throw new System.Runtime.Serialization.SerializationException("Live snapshot cannot fit within the message limit.");
        snapshot.ActiveMods = accepted == activeMods.Length ? activeMods : activeMods.Take(accepted).ToArray();
        snapshot.ActiveModsTruncated = wasTruncated || accepted < activeMods.Length;
        return acceptedJson;
    }

    public static T Deserialize<T>(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return (T)(new DataContractJsonSerializer(typeof(T)).ReadObject(stream) ?? throw new System.Runtime.Serialization.SerializationException("Live message is empty."));
    }
}
