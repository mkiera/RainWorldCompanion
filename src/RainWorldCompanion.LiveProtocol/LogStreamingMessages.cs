namespace RainWorldCompanion.LiveProtocol;

public static class LogStreamKinds
{
    public const string Open = "open";
    public const string OpenAccepted = "open-accepted";
    public const string Chunk = "chunk";
    public const string Acknowledgement = "ack";
    public const string Close = "close";
    public const string Error = "error";
}

public sealed class LogBridgeUpstream
{
    public string Token { get; set; } = "";
    public int ProtocolVersion { get; set; } = ProtocolInfo.LogStreamingVersion;
    public string GameInstallPath { get; set; } = "";
    public string GameSessionId { get; set; } = "";
    public long Sequence { get; set; }
    public LogLobbySnapshot Lobby { get; set; } = new();
    public LogRelayPacket[] ReceivedPackets { get; set; } = Array.Empty<LogRelayPacket>();
}

public sealed class LogBridgeDownstream
{
    public string Token { get; set; } = "";
    public int ProtocolVersion { get; set; } = ProtocolInfo.LogStreamingVersion;
    public LogReceiverAdvertisement Advertisement { get; set; } = new();
    public LogRelayPacket[] OutgoingPackets { get; set; } = Array.Empty<LogRelayPacket>();
}

public sealed class LogLobbySnapshot
{
    public bool IsConnected { get; set; }
    public bool IsSteam { get; set; }
    public string LobbyId { get; set; } = "";
    public string LocalSteamId { get; set; } = "";
    public string LocalDisplayName { get; set; } = "";
    public bool LocalIsHost { get; set; }
    public LogLobbyPeer[] Peers { get; set; } = Array.Empty<LogLobbyPeer>();
}

public sealed class LogLobbyPeer
{
    public string SteamId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool IsHost { get; set; }
    public bool SupportsLogStreaming { get; set; }
    public int ProtocolVersion { get; set; }
    public bool ReceiverAvailable { get; set; }
    public bool CaptureActive { get; set; }
    public bool CapturePaused { get; set; }
    public bool DeepTraceEnabled { get; set; }
    public string CaptureId { get; set; } = "";
    public string CaptureToken { get; set; } = "";
    public long LastSeenUtcTicks { get; set; }
}

public sealed class LogReceiverAdvertisement
{
    public bool Available { get; set; }
    public bool CaptureActive { get; set; }
    public bool CapturePaused { get; set; }
    public bool DeepTraceEnabled { get; set; }
    public string[] DeepTracePeerIds { get; set; } = Array.Empty<string>();
    public string CaptureId { get; set; } = "";
    public string CaptureToken { get; set; } = "";
}

public sealed class LogRelayPacket
{
    public string PeerSteamId { get; set; } = "";
    public byte[] Payload { get; set; } = Array.Empty<byte>();
}

public sealed class LogStreamNetworkMessage
{
    public int Version { get; set; } = ProtocolInfo.LogStreamingVersion;
    public string Kind { get; set; } = "";
    public string LobbyId { get; set; } = "";
    public string SenderSteamId { get; set; } = "";
    public string ReceiverSteamId { get; set; } = "";
    public string CaptureId { get; set; } = "";
    public string CaptureToken { get; set; } = "";
    public string TransferId { get; set; } = "";
    public string ConsentToken { get; set; } = "";
    public string LogSessionId { get; set; } = "";
    public string FileId { get; set; } = "";
    public int Generation { get; set; }
    public long Offset { get; set; }
    public long Sequence { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public bool AcceptsCompressedChunks { get; set; }
    public string DataEncoding { get; set; } = "";
    public string Hash { get; set; } = "";
    public string Message { get; set; } = "";
}
