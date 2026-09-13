using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using RainWorldCompanion.LiveProtocol;
using RainWorldCompanion.Core.Updates;

namespace RainWorldCompanion.Core.Live;

public enum LiveConnectionStatus { Waiting, Connected, Disconnected, Incompatible }

public sealed class LiveConnectionServer : IDisposable
{
    private readonly string _directory;
    private readonly Func<string?>? _gameInstallPath;
    private readonly CancellationTokenSource _stop = new();
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private bool _started;
    private readonly object _stateLock = new();
    private int? _port;
    private long _connections, _messages, _accepted, _rejected, _ignored, _bytes;
    private DateTimeOffset? _lastReceived, _lastAccepted;
    private string _lastEvent = "Listener has not started.";
    private string? _observedModVersion;
    private int? _observedProtocol;
    private LiveCommand? _queuedCommand;
    private TaskCompletionSource<LiveCommandResult>? _commandCompletion;
    private string? _commandId;

    public Task<LiveCommandResult> TeleportAsync(string gameplayId, string playerId, string roomId, string region)
        => SendCommandAsync(gameplayId, playerId, roomId, region, null);

    public Task<LiveCommandResult> TeleportAllAsync(string gameplayId, string roomId, string region)
        => SendCommandAsync(gameplayId, "", roomId, region, null, true);

    public Task<LiveCommandResult> SetHostControlAsync(bool enabled) => SendCommandAsync("", "", "", "", enabled);

    public Task<LiveCommandResult> RecoverAsync(string gameplayId, string playerId)
        => SendCommandAsync(gameplayId, playerId, "", "", null, recover: true);

    private async Task<LiveCommandResult> SendCommandAsync(string gameplayId, string playerId, string roomId, string region, bool? allowHostControl, bool teleportAll = false, bool recover = false)
    {
        string action = allowHostControl.HasValue ? "Host control update" : "Teleport";
        TaskCompletionSource<LiveCommandResult> completion;
        lock (_stateLock)
        {
            if (Status != LiveConnectionStatus.Connected || Snapshot is not { CommandVersion: 1 } snapshot)
                return new() { Message = "Connect an updated Companion Game Hook first." };
            if (allowHostControl == null && (snapshot.State is not ("gameplay" or "paused")
                || string.IsNullOrEmpty(gameplayId) || snapshot.GameplayId != gameplayId))
                return new() { Message = "Connect an updated Companion Game Hook during gameplay first." };
            if (recover && (!snapshot.SupportsRecovery || !snapshot.Players.Any(p => p.Id == playerId && p.IsLocal)))
                return new() { Message = "Recovery requires an updated Game Hook and a local player." };
            if (teleportAll && (!snapshot.IsHost || !snapshot.SupportsTeleportAll || !string.IsNullOrEmpty(snapshot.TeleportAllUnavailableReason)))
                return new() { Message = string.IsNullOrEmpty(snapshot.TeleportAllUnavailableReason) ? "Teleport all requires an updated host mod and everyone's permission." : snapshot.TeleportAllUnavailableReason };
            if (allowHostControl == null && !teleportAll && !recover && !snapshot.Players.Any(p => p.Id == playerId && p.Dead == false
                && (p.IsLocal || snapshot.IsHost && p.AllowsHostControl && !string.IsNullOrEmpty(p.CompanionVersion))))
                return new() { Message = "Choose a living local player or an online player who allows host control." };
            if (_commandCompletion is not null) return new() { Message = "A live command is already in progress." };
            if (allowHostControl == null && !recover && (string.IsNullOrWhiteSpace(roomId) || roomId.Length > 100 || string.IsNullOrWhiteSpace(region) || region.Length > 20))
                return new() { Message = "Invalid destination." };
            _queuedCommand = new()
            {
                Id = Guid.NewGuid().ToString("N"), SessionId = snapshot.SessionId, GameplayId = gameplayId,
                PlayerId = playerId, RoomId = roomId, Region = region, ExpiresUtcTicks = DateTime.UtcNow.AddSeconds(5).Ticks,
                AllowHostControl = allowHostControl, TeleportAll = teleportAll, Recover = recover
            };
            _commandId = _queuedCommand.Id;
            _commandCompletion = completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try { return await completion.Task.WaitAsync(TimeSpan.FromSeconds(60), _stop.Token); }
        catch (TimeoutException) { return new() { Message = action + " reply timed out. Check the game before trying again." }; }
        catch (OperationCanceledException) { return new() { Message = "The live connection closed before the command reply." }; }
        finally
        {
            lock (_stateLock)
                if (_commandCompletion == completion) { _queuedCommand = null; _commandCompletion = null; _commandId = null; }
        }
    }

    public LiveDiagnostics CaptureDiagnostics()
    {
        lock (_stateLock)
            return new(Status, Snapshot, _port, _directory, _connections, _messages, _accepted,
                _rejected, _ignored, _bytes, _lastReceived, _lastAccepted, _lastEvent,
                _observedModVersion, _observedProtocol);
    }

    private void RecordEvent(string reason, bool rejected = false)
    {
        lock (_stateLock)
        {
            _lastEvent = reason;
            if (rejected) _rejected++;
        }
    }
    public LiveSnapshot? Snapshot { get; private set; }
    public LiveConnectionStatus Status { get; private set; } = LiveConnectionStatus.Waiting;
    public event Action? Changed;

    public LiveConnectionServer(string? discoveryDirectory = null, Func<string?>? gameInstallPath = null)
    {
        _directory = discoveryDirectory ?? ProtocolInfo.DiscoveryDirectory;
        _gameInstallPath = gameInstallPath;
    }

    public void Start(LogBridgeEndpoint? logBridge = null)
    {
        if (_started) return;
        Directory.CreateDirectory(_directory);
        _listener.Start();
        try
        {
            var discovery = new LiveDiscovery
            {
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port,
                Token = _token,
                LogPort = logBridge?.Port ?? 0,
                LogToken = logBridge?.Token ?? ""
            };
            string temporary = Path.Combine(_directory, "endpoint." + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(temporary, LiveJson.Serialize(discovery));
            File.Move(temporary, Path.Combine(_directory, "endpoint.json"), true);
            _started = true;
            lock (_stateLock) { _port = discovery.Port; _lastEvent = "Listening on loopback."; }
            _ = AcceptAsync();
        }
        catch { _listener.Stop(); throw; }
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                lock (_stateLock) _connections++;
                await ReceiveAsync(client);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    private async Task ReceiveAsync(TcpClient client)
    {
        bool authenticated = false;
        string? session = null;
        long sequence = -1;
        var lastAccepted = DateTime.UtcNow;
        using var reader = new StreamReader(client.GetStream(), Encoding.UTF8, false, 4096, true);
        using var writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                var json = await ReadBoundedLineAsync(reader, timeout.Token);
                if (json is null) { RecordEvent("Client closed the connection."); break; }
                lock (_stateLock)
                {
                    _messages++;
                    _bytes += Encoding.UTF8.GetByteCount(json) + 1;
                    _lastReceived = DateTimeOffset.UtcNow;
                }
                var incoming = LiveJson.Deserialize<LiveSnapshot>(json);
                if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(incoming.Token ?? ""), Encoding.UTF8.GetBytes(_token))) { RecordEvent("Authentication rejected.", true); break; }
                string? expected = _gameInstallPath?.Invoke();
                if (!string.IsNullOrWhiteSpace(expected) && !string.Equals(Path.GetFullPath(expected).TrimEnd('\\', '/'), Path.GetFullPath(incoming.GameInstallPath).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) { RecordEvent("Game installation path rejected.", true); break; }
                authenticated = true;
                lock (_stateLock) { _observedModVersion = incoming.ModVersion; _observedProtocol = incoming.ProtocolVersion; }
                if (incoming.ProtocolVersion != ProtocolInfo.Version || !SemVer.TryParse(incoming.ModVersion, out var modVersion) || modVersion.Major != Version.Parse(ProtocolInfo.ModVersion.Split('-')[0]).Major)
                {
                    RecordEvent("Protocol or mod version is incompatible.", true);
                    SetState(LiveConnectionStatus.Incompatible, null);
                    return;
                }
                if (string.IsNullOrWhiteSpace(incoming.SessionId) || incoming.Players is null || incoming.Players.Length > 256
                    || incoming.Players.Any(player => player is null || string.IsNullOrWhiteSpace(player.Id))
                    || incoming.EnabledExpansions is null || !ValidActiveMods(incoming.ActiveMods))
                {
                    RecordEvent("Snapshot fields rejected.", true);
                    break;
                }
                if (session is not null && session != incoming.SessionId) { RecordEvent("Session changed within a connection.", true); break; }
                session = incoming.SessionId;
                if (incoming.Sequence <= sequence)
                {
                    lock (_stateLock) _ignored++;
                    RecordEvent("Ignored an old or duplicate sequence.");
                    if (DateTime.UtcNow - lastAccepted > TimeSpan.FromSeconds(3)) { RecordEvent("No fresh snapshot within three seconds."); break; }
                    continue;
                }
                sequence = incoming.Sequence;
                lastAccepted = DateTime.UtcNow;
                lock (_stateLock) { _accepted++; _lastAccepted = DateTimeOffset.UtcNow; _lastEvent = "Authenticated snapshot accepted."; }
                incoming.Token = "";
                SetState(LiveConnectionStatus.Connected, incoming);
                if (incoming.CommandVersion == 1)
                {
                    LiveCommand? command;
                    lock (_stateLock)
                    {
                        if (incoming.CommandResult is { } result && result.Id == _commandId)
                            _commandCompletion?.TrySetResult(result);
                        command = _queuedCommand;
                        _queuedCommand = null;
                    }
                    await writer.WriteLineAsync(LiveJson.Serialize(new LiveCommandReply { Token = _token, Command = command }).AsMemory(), timeout.Token);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or global::System.Runtime.Serialization.SerializationException or ArgumentException or InvalidOperationException or global::System.Xml.XmlException)
        {
            RecordEvent(ex is OperationCanceledException ? "Connection stopped or heartbeat timed out." : "Message read failed: " + ex.GetType().Name,
                ex is not OperationCanceledException);
        }
        finally
        {
            lock (_stateLock)
            {
                _queuedCommand = null;
                _commandCompletion?.TrySetResult(new() { Message = "Disconnected before the command reply. Check the game after reconnecting." });
            }
            if (authenticated && Status != LiveConnectionStatus.Incompatible) SetState(LiveConnectionStatus.Disconnected, null);
        }
    }

    private static async Task<string?> ReadBoundedLineAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var result = new StringBuilder();
        var character = new char[1];
        while (await reader.ReadAsync(character.AsMemory(), cancellationToken) != 0)
        {
            if (character[0] == '\n') return result.ToString();
            if (result.Length >= ProtocolInfo.MaximumMessageLength) throw new IOException("Live message exceeds the size limit.");
            result.Append(character[0]);
        }
        return null;
    }

    private static bool ValidActiveMods(LiveModInfo[]? mods)
    {
        if (mods is null || mods.Length > ProtocolInfo.MaximumActiveMods) return false;
        foreach (var mod in mods)
        {
            if (mod is null || string.IsNullOrWhiteSpace(mod.Id) || mod.Id.Length > ProtocolInfo.MaximumModIdLength
                || string.IsNullOrWhiteSpace(mod.DisplayName) || mod.DisplayName.Length > ProtocolInfo.MaximumModDisplayNameLength
                || mod.Version is null || mod.Version.Length > ProtocolInfo.MaximumModVersionLength
                || mod.CodeFingerprint is null || mod.FingerprintStatus is null)
                return false;
            bool hasFingerprint = mod.CodeFingerprint.Length == 64 && mod.CodeFingerprint.All(Uri.IsHexDigit);
            if (mod.FingerprintStatus is "complete" or "partial")
            {
                if (!hasFingerprint) return false;
            }
            else if (mod.FingerprintStatus is "pending" or "unavailable" or "no-code")
            {
                if (mod.CodeFingerprint.Length != 0) return false;
            }
            else return false;
        }
        return true;
    }

    private void SetState(LiveConnectionStatus status, LiveSnapshot? snapshot)
    {
        lock (_stateLock) { Snapshot = snapshot; Status = status; }
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            string endpoint = Path.Combine(_directory, "endpoint.json");
            if (File.Exists(endpoint) && LiveJson.Deserialize<LiveDiscovery>(File.ReadAllText(endpoint)).Token == _token) File.Delete(endpoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or global::System.Runtime.Serialization.SerializationException) { }
    }
}
