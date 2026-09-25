using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using RainWorldCompanion.LiveProtocol;

namespace RWCompanion.Mod;

internal sealed class LogBridgeTransport
{
    private const int MaximumReceivedPackets = 128;
    private const int MaximumOutgoingPackets = 128;
    private const int MaximumOutgoingPacketsPerPeer = 8;
    private const int MaximumPacketsPerExchange = ProtocolInfo.MaximumLogPacketsPerBridgeExchange;
    private const int BridgeGraceMilliseconds = 5000;
    private readonly object _sync = new();
    private readonly Queue<LogRelayPacket> _received = new();
    private readonly Dictionary<string, Queue<QueuedRelayPacket>> _outgoing = new(StringComparer.Ordinal);
    private readonly HashSet<string> _outgoingKeys = new(StringComparer.Ordinal);
    private readonly string _gameSessionId = Guid.NewGuid().ToString("N");
    private LogLobbySnapshot _lobby = new();
    private LogReceiverAdvertisement _advertisement = new();
    private CancellationTokenSource? _stop;
    private TcpClient? _client;
    private long _sequence;
    private long _lobbyEpoch;
    private long _lastSuccessfulExchangeTicks;
    private int _outgoingPacketCount;
    private int _outgoingPeerIndex;

    internal LogReceiverAdvertisement Advertisement
    {
        get
        {
            lock (_sync)
            {
                ExpireBridgeState();
                return LogAdvertisement.Clone(_advertisement);
            }
        }
    }

    internal void PublishLobby(LogLobbySnapshot lobby)
    {
        if (lobby is null) throw new ArgumentNullException(nameof(lobby));
        lock (_sync)
        {
            bool changed = !string.Equals(_lobby.LobbyId, lobby.LobbyId, StringComparison.Ordinal)
                || !string.Equals(_lobby.LocalSteamId, lobby.LocalSteamId, StringComparison.Ordinal)
                || _lobby.IsConnected != lobby.IsConnected
                || _lobby.IsSteam != lobby.IsSteam;
            if (!lobby.IsConnected || changed)
            {
                _lobbyEpoch++;
                _advertisement = new();
                _received.Clear();
                ClearOutgoing();
                _lastSuccessfulExchangeTicks = 0;
            }
            _lobby = lobby;
            if (lobby.IsConnected) PruneOutgoing((lobby.Peers ?? Array.Empty<LogLobbyPeer>())
                .Where(peer => peer is not null).Select(peer => peer.SteamId).ToHashSet(StringComparer.Ordinal));
        }
    }

    internal void PublishReceived(LogRelayPacket packet)
    {
        if (packet is null || !ValidSteamId(packet.PeerSteamId) || packet.Payload is null
            || packet.Payload.Length > ProtocolInfo.MaximumLogPacketLength)
            return;
        lock (_sync)
        {
            if (_received.Count < MaximumReceivedPackets) _received.Enqueue(packet);
        }
    }

    internal LogRelayPacket[] TakeOutgoing()
    {
        lock (_sync)
        {
            ExpireBridgeState();
            if (_outgoingPacketCount == 0) return Array.Empty<LogRelayPacket>();
            var result = new List<LogRelayPacket>(MaximumPacketsPerExchange);
            while (result.Count < MaximumPacketsPerExchange && _outgoingPacketCount > 0)
            {
                string[] peers = _outgoing.Keys.OrderBy(peer => peer, StringComparer.Ordinal).ToArray();
                if (peers.Length == 0) break;
                _outgoingPeerIndex %= peers.Length;
                bool tookPacket = false;
                for (int checkedPeers = 0; checkedPeers < peers.Length; checkedPeers++)
                {
                    int index = (_outgoingPeerIndex + checkedPeers) % peers.Length;
                    string peer = peers[index];
                    if (!_outgoing.TryGetValue(peer, out var queue) || queue.Count == 0) continue;
                    var queued = queue.Dequeue();
                    _outgoingKeys.Remove(queued.Key);
                    _outgoingPacketCount--;
                    result.Add(queued.Packet);
                    if (queue.Count == 0) _outgoing.Remove(peer);
                    _outgoingPeerIndex = index + 1;
                    tookPacket = true;
                    break;
                }
                if (!tookPacket) break;
            }
            return result.ToArray();
        }
    }

    internal void RetryOutgoing(IEnumerable<LogRelayPacket> packets)
    {
        lock (_sync)
        {
            foreach (var packet in packets) EnqueueOutgoing(packet);
        }
    }

    internal void Start()
    {
        if (_stop != null) return;
        _stop = new CancellationTokenSource();
        _ = Task.Run(() => RunAsync(_stop.Token));
    }

    internal void Stop()
    {
        _stop?.Cancel();
        _client?.Close();
        _stop = null;
        lock (_sync)
        {
            _lobbyEpoch++;
            _received.Clear();
            ClearOutgoing();
            _advertisement = new();
            _lobby = new();
            _lastSuccessfulExchangeTicks = 0;
        }
    }

    private async Task RunAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var discovery = LiveJson.Deserialize<LiveDiscovery>(File.ReadAllText(Path.Combine(ProtocolInfo.DiscoveryDirectory, "endpoint.json")));
                if (discovery == null) throw new IOException("Live discovery is empty.");
                if (discovery.LogPort <= 0 || discovery.LogProtocolVersion != ProtocolInfo.LogStreamingVersion
                    || string.IsNullOrWhiteSpace(discovery.LogToken))
                {
                    await Task.Delay(1000, stop);
                    continue;
                }
                using var client = new TcpClient();
                _client = client;
                var connect = client.ConnectAsync(IPAddress.Loopback, discovery.LogPort);
                if (await Task.WhenAny(connect, Task.Delay(2000, stop)) != connect) continue;
                await connect;
                using var registration = stop.Register(client.Close);
                using var writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
                using var reader = new StreamReader(client.GetStream(), Encoding.UTF8);
                while (!stop.IsCancellationRequested)
                {
                    LogBridgeUpstream message;
                    int queued;
                    long lobbyEpoch;
                    lock (_sync)
                    {
                        var packets = _received.Take(MaximumPacketsPerExchange).ToArray();
                        queued = packets.Length;
                        lobbyEpoch = _lobbyEpoch;
                        message = new()
                        {
                            Token = discovery.LogToken,
                            GameInstallPath = Path.GetDirectoryName(UnityEngine.Application.dataPath) ?? "",
                            GameSessionId = _gameSessionId,
                            Sequence = ++_sequence,
                            Lobby = CloneLobby(_lobby),
                            ReceivedPackets = packets
                        };
                    }
                    string json = LiveJson.Serialize(message);
                    if (Encoding.UTF8.GetByteCount(json) > ProtocolInfo.MaximumLogBridgeMessageLength)
                        throw new IOException("Log bridge message exceeds the size limit.");
                    var write = writer.WriteLineAsync(json);
                    if (await Task.WhenAny(write, Task.Delay(2000, stop)) != write) break;
                    await write;
                    var read = ReadReply(reader);
                    if (await Task.WhenAny(read, Task.Delay(3000, stop)) != read) break;
                    var reply = LiveJson.Deserialize<LogBridgeDownstream>(await read);
                    if (reply == null) throw new IOException("Log bridge reply is empty.");
                    if (reply.Token != discovery.LogToken || reply.ProtocolVersion != ProtocolInfo.LogStreamingVersion
                        || !LogAdvertisement.IsValid(reply.Advertisement) || reply.OutgoingPackets is null
                        || reply.OutgoingPackets.Length > ProtocolInfo.MaximumLogPacketsPerBridgeExchange
                        || reply.OutgoingPackets.Any(packet => packet is null || packet.Payload is null
                            || packet.Payload.Length > ProtocolInfo.MaximumLogPacketLength
                            || !ValidSteamId(packet.PeerSteamId)))
                        throw new IOException("Log bridge reply is invalid.");
                    int delayMilliseconds;
                    lock (_sync)
                    {
                        if (lobbyEpoch != _lobbyEpoch) continue;
                        for (int index = 0; index < queued && _received.Count > 0; index++) _received.Dequeue();
                        _advertisement = reply.Advertisement;
                        _lastSuccessfulExchangeTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                        foreach (var packet in reply.OutgoingPackets) EnqueueOutgoing(packet);
                        delayMilliseconds = LogBridgePacing.GetDelayMilliseconds(queued,
                            reply.OutgoingPackets.Length, _received.Count);
                    }
                    await Task.Delay(delayMilliseconds, stop);
                }
            }
            catch (Exception error) when (error is IOException or SocketException or UnauthorizedAccessException
                or OperationCanceledException or global::System.Runtime.Serialization.SerializationException
                or ArgumentException or InvalidOperationException or NotSupportedException
                or ObjectDisposedException or global::System.Xml.XmlException)
            {
            }
            try { await Task.Delay(1000, stop); }
            catch (OperationCanceledException) { }
        }
    }

    private static async Task<string> ReadReply(StreamReader reader)
    {
        var text = new StringBuilder();
        var character = new char[1];
        while (await reader.ReadAsync(character, 0, 1) != 0)
        {
            if (character[0] == '\n') return text.ToString();
            if (text.Length >= ProtocolInfo.MaximumLogBridgeMessageLength)
                throw new IOException("Log bridge reply exceeds the size limit.");
            text.Append(character[0]);
        }
        throw new IOException("Log bridge connection closed.");
    }

    private void EnqueueOutgoing(LogRelayPacket packet)
    {
        if (packet is null || !ValidSteamId(packet.PeerSteamId) || packet.Payload is null
            || packet.Payload.Length > ProtocolInfo.MaximumLogPacketLength)
            return;
        string key = PacketKey(packet);
        if (_outgoingKeys.Contains(key)) return;
        if (!_outgoing.TryGetValue(packet.PeerSteamId, out var queue))
        {
            queue = new Queue<QueuedRelayPacket>();
            _outgoing[packet.PeerSteamId] = queue;
        }
        while (queue.Count >= MaximumOutgoingPacketsPerPeer) RemoveOldest(packet.PeerSteamId, queue);
        if (_outgoingPacketCount >= MaximumOutgoingPackets) EvictForFairness(packet.PeerSteamId);
        if (_outgoingPacketCount >= MaximumOutgoingPackets) return;
        queue.Enqueue(new(packet, key));
        _outgoingKeys.Add(key);
        _outgoingPacketCount++;
    }

    private void EvictForFairness(string incomingPeer)
    {
        var candidate = _outgoing
            .Where(item => !string.Equals(item.Key, incomingPeer, StringComparison.Ordinal) && item.Value.Count > 1)
            .OrderByDescending(item => item.Value.Count)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .FirstOrDefault();
        if (candidate.Value is not null) RemoveOldest(candidate.Key, candidate.Value);
    }

    private void RemoveOldest(string peer, Queue<QueuedRelayPacket> queue)
    {
        if (queue.Count == 0) return;
        _outgoingKeys.Remove(queue.Dequeue().Key);
        _outgoingPacketCount--;
        if (queue.Count == 0) _outgoing.Remove(peer);
    }

    private void ClearOutgoing()
    {
        _outgoing.Clear();
        _outgoingKeys.Clear();
        _outgoingPacketCount = 0;
        _outgoingPeerIndex = 0;
    }

    private void PruneOutgoing(HashSet<string> activePeers)
    {
        foreach (string peer in _outgoing.Keys.Where(peer => !activePeers.Contains(peer)).ToArray())
        {
            foreach (var packet in _outgoing[peer]) _outgoingKeys.Remove(packet.Key);
            _outgoingPacketCount -= _outgoing[peer].Count;
            _outgoing.Remove(peer);
        }
        if (_outgoingPacketCount == 0) _outgoingPeerIndex = 0;
    }

    private void ExpireBridgeState()
    {
        if (_lastSuccessfulExchangeTicks == 0) return;
        long elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - _lastSuccessfulExchangeTicks;
        if (elapsed <= MillisecondsToTicks(BridgeGraceMilliseconds)) return;
        _lastSuccessfulExchangeTicks = 0;
        _advertisement = new();
        _received.Clear();
        ClearOutgoing();
    }

    private static string PacketKey(LogRelayPacket packet)
    {
        try
        {
            var message = LiveJson.Deserialize<LogStreamNetworkMessage>(Encoding.UTF8.GetString(packet.Payload));
            if (message is not null && message.Kind is not null && message.TransferId is not null)
                return packet.PeerSteamId + "|" + message.Kind + "|" + message.TransferId + "|"
                    + message.LogSessionId + "|" + message.FileId + "|" + message.Generation + "|"
                    + message.Sequence + "|" + message.Offset + "|" + message.Hash;
        }
        catch (Exception error) when (error is global::System.Runtime.Serialization.SerializationException
            or ArgumentException or global::System.Xml.XmlException)
        {
        }
        using var sha256 = SHA256.Create();
        return packet.PeerSteamId + "|" + Convert.ToBase64String(sha256.ComputeHash(packet.Payload));
    }

    private static LogLobbySnapshot CloneLobby(LogLobbySnapshot lobby) => new()
    {
        IsConnected = lobby.IsConnected,
        IsSteam = lobby.IsSteam,
        LobbyId = lobby.LobbyId,
        LocalSteamId = lobby.LocalSteamId,
        LocalDisplayName = lobby.LocalDisplayName,
        LocalIsHost = lobby.LocalIsHost,
        Peers = lobby.Peers?.Where(peer => peer is not null).Select(peer => new LogLobbyPeer
        {
            SteamId = peer.SteamId,
            DisplayName = peer.DisplayName,
            IsHost = peer.IsHost,
            SupportsLogStreaming = peer.SupportsLogStreaming,
            ProtocolVersion = peer.ProtocolVersion,
            ReceiverAvailable = peer.ReceiverAvailable,
            CaptureActive = peer.CaptureActive,
            CapturePaused = peer.CapturePaused,
            DeepTraceEnabled = peer.DeepTraceEnabled,
            CaptureId = peer.CaptureId,
            CaptureToken = peer.CaptureToken,
            LastSeenUtcTicks = peer.LastSeenUtcTicks
        }).ToArray() ?? Array.Empty<LogLobbyPeer>()
    };

    private static bool ValidSteamId(string? value) => value is { Length: > 0 and <= 32 } && value.All(char.IsDigit);
    private static long MillisecondsToTicks(int milliseconds)
        => checked(System.Diagnostics.Stopwatch.Frequency * milliseconds / 1000);

    private sealed class QueuedRelayPacket
    {
        internal QueuedRelayPacket(LogRelayPacket packet, string key)
        {
            Packet = packet;
            Key = key;
        }

        internal LogRelayPacket Packet { get; }
        internal string Key { get; }
    }
}
