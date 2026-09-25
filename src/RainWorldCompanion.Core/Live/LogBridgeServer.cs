using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using RainWorldCompanion.LiveProtocol;

namespace RainWorldCompanion.Core.Live;

public enum LogBridgeStatus { Waiting, Connected, Disconnected, Incompatible }

public sealed record LogBridgeEndpoint(int Port, string Token);

public sealed record LogBridgeDiagnostics(
    LogBridgeStatus Status, LogLobbySnapshot? Lobby, long Connections, long Exchanges,
    long ReceivedPackets, long SentPackets, long ReceivedBytes, long SentBytes,
    DateTimeOffset? LastExchange, string LastEvent);

public sealed class LogBridgeServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<string?>? _gameInstallPath;
    private readonly Func<LogBridgeUpstream, LogBridgeDownstream> _exchange;
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly object _sync = new();
    private bool _started;
    private long _connections;
    private long _exchanges;
    private long _receivedPackets;
    private long _sentPackets;
    private long _receivedBytes;
    private long _sentBytes;
    private DateTimeOffset? _lastExchange;
    private string _lastEvent = "Listener has not started.";
    private LogBridgeStatus _status = LogBridgeStatus.Waiting;
    private LogLobbySnapshot? _lobby;

    public LogBridgeServer(Func<LogBridgeUpstream, LogBridgeDownstream> exchange,
        Func<string?>? gameInstallPath = null)
    {
        _exchange = exchange ?? throw new ArgumentNullException(nameof(exchange));
        _gameInstallPath = gameInstallPath;
    }

    public LogBridgeEndpoint Start()
    {
        if (_started) return new(((IPEndPoint)_listener.LocalEndpoint).Port, _token);
        _listener.Start();
        _started = true;
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        lock (_sync) _lastEvent = "Listening on loopback.";
        _ = AcceptAsync();
        return new(port, _token);
    }

    public LogBridgeDiagnostics CaptureDiagnostics()
    {
        lock (_sync)
            return new(_status, _lobby, _connections, _exchanges, _receivedPackets, _sentPackets,
                _receivedBytes, _sentBytes, _lastExchange, _lastEvent);
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                lock (_sync) _connections++;
                await ExchangeAsync(client);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or SocketException
                or OperationCanceledException or ObjectDisposedException)
            {
            }
        }
    }

    private async Task ExchangeAsync(TcpClient client)
    {
        bool authenticated = false;
        long sequence = -1;
        using var reader = new StreamReader(client.GetStream(), Encoding.UTF8, false, 8192, true);
        using var writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false), 8192, true) { AutoFlush = true };
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                string? json = await ReadBoundedLineAsync(reader, timeout.Token);
                if (json is null) break;
                var upstream = LiveJson.Deserialize<LogBridgeUpstream>(json)
                    ?? throw new InvalidDataException("Log bridge message was empty.");
                Validate(upstream);
                if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(upstream.Token ?? ""), Encoding.UTF8.GetBytes(_token)))
                    throw new UnauthorizedAccessException("Log bridge authentication failed.");
                ValidateInstallPath(upstream.GameInstallPath);
                if (upstream.ProtocolVersion != ProtocolInfo.LogStreamingVersion)
                {
                    lock (_sync) { _status = LogBridgeStatus.Incompatible; _lastEvent = "Log bridge protocol is incompatible."; }
                    return;
                }
                authenticated = true;
                if (upstream.Sequence <= sequence) throw new InvalidDataException("Log bridge sequence did not advance.");
                sequence = upstream.Sequence;
                upstream.Token = "";
                var downstream = _exchange(upstream) ?? new LogBridgeDownstream();
                downstream.Token = _token;
                downstream.ProtocolVersion = ProtocolInfo.LogStreamingVersion;
                ValidateOutgoing(downstream);
                string reply = LiveJson.Serialize(downstream);
                if (Encoding.UTF8.GetByteCount(reply) > ProtocolInfo.MaximumLogBridgeMessageLength)
                    throw new InvalidDataException("Log bridge reply exceeds the size limit.");
                await writer.WriteLineAsync(reply.AsMemory(), timeout.Token);
                lock (_sync)
                {
                    _status = LogBridgeStatus.Connected;
                    _lobby = upstream.Lobby;
                    _exchanges++;
                    _receivedPackets += upstream.ReceivedPackets.Length;
                    _sentPackets += downstream.OutgoingPackets.Length;
                    _receivedBytes += upstream.ReceivedPackets.Sum(packet => packet.Payload.Length);
                    _sentBytes += downstream.OutgoingPackets.Sum(packet => packet.Payload.Length);
                    _lastExchange = DateTimeOffset.UtcNow;
                    _lastEvent = "Authenticated log bridge exchange accepted.";
                }
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or SocketException or OperationCanceledException
            or global::System.Runtime.Serialization.SerializationException or ArgumentException
            or InvalidOperationException or NotSupportedException or UnauthorizedAccessException
            or global::System.Xml.XmlException)
        {
            lock (_sync) _lastEvent = error is OperationCanceledException
                ? "Log bridge stopped or timed out."
                : "Log bridge rejected a message: " + error.GetType().Name;
        }
        finally
        {
            if (authenticated)
            {
                lock (_sync)
                {
                    if (_status != LogBridgeStatus.Incompatible) _status = LogBridgeStatus.Disconnected;
                    _lobby = null;
                }
            }
        }
    }

    private void ValidateInstallPath(string reported)
    {
        string? expected = _gameInstallPath?.Invoke();
        if (string.IsNullOrWhiteSpace(expected)) return;
        if (string.IsNullOrWhiteSpace(reported)
            || !string.Equals(Path.GetFullPath(expected).TrimEnd('\\', '/'), Path.GetFullPath(reported).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Log bridge game installation path did not match.");
    }

    private static void Validate(LogBridgeUpstream message)
    {
        if (message.Token is null or { Length: > 128 }
            || message.GameInstallPath is null or { Length: > 4096 }
            || message.Sequence < 1
            || message.Lobby is null || message.ReceivedPackets is null
            || message.ReceivedPackets.Length > ProtocolInfo.MaximumLogPacketsPerBridgeExchange
            || message.Lobby.Peers is null || message.Lobby.Peers.Length > 64)
            throw new InvalidDataException("Log bridge fields exceed their limits.");
        if (message.ReceivedPackets.Any(packet => packet is null || packet.Payload is null
            || packet.Payload.Length > ProtocolInfo.MaximumLogPacketLength || !ValidId(packet.PeerSteamId, 32)))
            throw new InvalidDataException("A relayed log packet is invalid.");
        if (!ValidId(message.GameSessionId, 64) || !ValidOptionalId(message.Lobby.LobbyId, 64)
            || !ValidOptionalId(message.Lobby.LocalSteamId, 32)
            || message.Lobby.LocalDisplayName is null or { Length: > 128 }
            || message.Lobby.Peers.Any(peer => peer is null
                || !ValidId(peer.SteamId, 32) || peer.DisplayName is null or { Length: > 128 }
                || peer.CaptureId is null or { Length: > 64 }
                || peer.CaptureToken is null or { Length: > 128 }
                || peer.DeepTraceEnabled && !peer.ReceiverAvailable))
            throw new InvalidDataException("Lobby identity fields are invalid.");
        if (message.Lobby.Peers.Select(peer => peer.SteamId).Distinct(StringComparer.Ordinal).Count()
            != message.Lobby.Peers.Length)
            throw new InvalidDataException("Lobby peer identities must be unique.");
    }

    private static void ValidateOutgoing(LogBridgeDownstream message)
    {
        if (message.Advertisement is null || message.OutgoingPackets is null
            || message.OutgoingPackets.Length > ProtocolInfo.MaximumLogPacketsPerBridgeExchange
            || message.Advertisement.CaptureId is null or { Length: > 64 }
            || message.Advertisement.CaptureToken is null or { Length: > 128 }
            || message.Advertisement.CaptureActive && message.Advertisement.CapturePaused
            || message.Advertisement.DeepTraceEnabled && !message.Advertisement.Available
            || (message.Advertisement.Available
                && (message.Advertisement.CaptureId.Length == 0 || message.Advertisement.CaptureToken.Length == 0))
            || (!message.Advertisement.Available
                && (message.Advertisement.CaptureActive || message.Advertisement.CapturePaused
                    || message.Advertisement.CaptureId.Length > 0 || message.Advertisement.CaptureToken.Length > 0))
            || message.OutgoingPackets.Any(packet => packet is null || packet.Payload is null
                || packet.Payload.Length > ProtocolInfo.MaximumLogPacketLength || !ValidId(packet.PeerSteamId, 32)))
            throw new InvalidDataException("Outgoing log bridge fields are invalid.");
    }

    private static bool ValidId(string? value, int maximum) => value is { Length: > 0 } && value.Length <= maximum
        && value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or ':');
    private static bool ValidOptionalId(string? value, int maximum) => string.IsNullOrEmpty(value) || ValidId(value, maximum);

    private static async Task<string?> ReadBoundedLineAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var result = new StringBuilder();
        var character = new char[1];
        while (await reader.ReadAsync(character.AsMemory(), cancellationToken) != 0)
        {
            if (character[0] == '\n') return result.ToString();
            if (result.Length >= ProtocolInfo.MaximumLogBridgeMessageLength)
                throw new IOException("Log bridge message exceeds the size limit.");
            result.Append(character[0]);
        }
        return null;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}
