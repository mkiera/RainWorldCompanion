using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RainWorldCompanion.Core.LogStreaming;
using RainWorldCompanion.LiveProtocol;

namespace RainWorldCompanion.Core.Live;

public enum LogStreamingCaptureMode { Stopped, Capturing, Paused }
public enum LogStreamingPeerMode { Unsupported, NotSharing, Ready, Streaming, Reconnecting, ReceiverPaused, StorageLimited, Disconnected }

public sealed record LogStreamingDirectionSnapshot(
    LogStreamingPeerMode State, string Detail, double BytesPerSecond, long AcknowledgedBytes,
    long BacklogBytes, TimeSpan? AcknowledgementAge, int ReconnectCount, string LogSession);

public sealed record LogStreamingPeerSnapshot(
    string SteamId, string DisplayName, bool IsHost, bool CanReceive,
    bool IsReceiving, bool ReceiverDeepTraceEnabled,
    LogStreamingDirectionSnapshot Incoming, LogStreamingDirectionSnapshot Outgoing);

public sealed record LogStreamingViewerLine(
    long Sequence, DateTimeOffset Timestamp, string SenderId, string SenderName, string FileId, string Text);

public sealed record LogStreamingChartSample(
    DateTimeOffset Time, double BytesPerSecond, long BacklogBytes, TimeSpan? AcknowledgementAge);

public sealed record LogStreamingCoordinatorSnapshot(
    DateTimeOffset ObservedAt, bool IsSteamLobby, bool ReceiverAdvertised, bool DeepTraceEnabled,
    LogStreamingCaptureMode CaptureMode, string DestinationRoot, string CaptureFolder, string Message,
    IReadOnlyList<LogStreamingPeerSnapshot> Peers, IReadOnlyList<LogStreamingViewerLine> Lines,
    IReadOnlyList<LogStreamingChartSample> Samples);

public sealed record LogStreamingCoordinatorOptions
{
    public required Func<string?> GameInstallPath { get; init; }
    public required string DestinationRoot { get; init; }
    public string AppVersion { get; init; } = "";
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    public LogStreamSenderOptions? SenderOptions { get; init; }
    public LogStreamCaptureOptions? CaptureOptions { get; init; }
    public long MaximumBytesPerSecond { get; init; } = 64 * 1024;
    public long MaximumIncomingBytesPerSecondPerPeer { get; init; } = 32 * 1024;
    public TimeSpan ReconnectGrace { get; init; } = TimeSpan.FromMinutes(2);
}

public sealed class LogStreamingCoordinator
{
    private const string LocalCaptureReceiverId = "local-capture";
    private const int MaximumViewerLines = 10_000;
    private const int MaximumViewerCharacters = 256 * 1024;
    private const int MaximumViewerLineCharacters = 4 * 1024;
    private const int MaximumViewerStreamsPerTransfer = 16;
    private const int MaximumPendingTelemetryBytes = 8 * 1024 * 1024;
    private const int MaximumDeepSessionsPerTransfer = 63;
    private const int MaximumConcurrentDeepTraceReceivers = 8;
    private const long MaximumDeepTraceSpoolBytes = 8L * 1024 * 1024;
    private static readonly TimeSpan DeepTraceInterval = TimeSpan.FromMilliseconds(500);
    private const int MaximumControlPackets = 256;
    private const int MaximumRevokedIncomingAuthorizations = 1024;
    private const int MaximumIncomingOpensPerMinute = 8;
    private const int MinimumIncomingPacketCost = 512;
    private const double MaximumIncomingPacketsPerSecond = 32;
    private const double MaximumIncomingPacketsPerSecondPerPeer = 12;
    private const int MaximumPacketsPerExchange = ProtocolInfo.MaximumLogPacketsPerBridgeExchange;
    private const long MaximumUnacknowledgedBytes = 64 * 1024;
    private static readonly TimeSpan IncomingOpenWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan IncomingOpenNoticeInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TransferHeartbeatInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TransferHeartbeatStale = TimeSpan.FromSeconds(5);
    private readonly object _sync = new();
    private readonly LogStreamingCoordinatorOptions _options;
    private string _destinationRoot;
    private readonly Dictionary<string, OutgoingTransfer> _outgoing = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IncomingTransfer> _incoming = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RememberedPeer> _remembered = new(StringComparer.Ordinal);
    private readonly Queue<LogRelayPacket> _controlPackets = new();
    private readonly Queue<LogStreamingViewerLine> _viewerLines = new();
    private readonly Queue<LogStreamingChartSample> _samples = new();
    private readonly Queue<LogStreamTelemetryRecord> _pendingTelemetry = new();
    private readonly Dictionary<string, ViewerStream> _localViewers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IncomingBudget> _incomingBudgets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IncomingOpenRate> _incomingOpenRates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LogStreamSenderSession> _deepSenders = new(StringComparer.Ordinal);
    private readonly HashSet<IncomingAuthorization> _revokedIncoming = [];
    private readonly Queue<IncomingAuthorization> _revokedIncomingOrder = new();
    private LogLobbySnapshot _lobby = new();
    private string _gameSessionId = "";
    private LogStreamSenderSession? _sender;
    private LogStreamSenderSession? _localDeepSender;
    private LogStreamCaptureWriter? _capture;
    private readonly LogStreamTelemetryRecorder _telemetry;
    private bool _receiverAvailable;
    private bool _deepTraceEnabled;
    private LogStreamingCaptureMode _captureMode;
    private string _captureId = "";
    private string _captureToken = "";
    private string _lastCaptureFolder = "";
    private string _message = "Join a Steam Rain Meadow lobby to stream logs.";
    private DateTimeOffset _lastSample;
    private long _sampleBytes;
    private double _sendBudget;
    private double _incomingBudget;
    private double _incomingPacketBudget;
    private DateTimeOffset _lastBudget;
    private long _viewerSequence;
    private long _viewerStreamSequence;
    private int _viewerCharacters;
    private int _roundRobin;
    private int _pendingTelemetryBytes;
    private DateTimeOffset _lastDeepTraceSample;

    public LogStreamingCoordinator(LogStreamingCoordinatorOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentNullException.ThrowIfNull(options.GameInstallPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DestinationRoot);
        if (options.MaximumBytesPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(options.MaximumBytesPerSecond));
        if (options.MaximumIncomingBytesPerSecondPerPeer <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.MaximumIncomingBytesPerSecondPerPeer));
        if (options.ReconnectGrace <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options.ReconnectGrace));
        _destinationRoot = Path.GetFullPath(options.DestinationRoot);
        _telemetry = new(options.AppVersion);
        _lastSample = _lastBudget = Now;
        _lastDeepTraceSample = DateTimeOffset.MinValue;
        _sendBudget = options.MaximumBytesPerSecond;
        _incomingBudget = options.MaximumBytesPerSecond;
        _incomingPacketBudget = MaximumIncomingPacketsPerSecond;
    }

    public LogBridgeDownstream Exchange(LogBridgeUpstream upstream)
    {
        ArgumentNullException.ThrowIfNull(upstream);
        lock (_sync)
        {
            DateTimeOffset now = Now;
            AdoptContext(upstream, now);
            if (_lobby.IsConnected && _lobby.IsSteam)
            {
                RefillBudgets(now);
                ExpireAuthorizations(now);
                RememberPeers(now);
                SyncDeepTraceSenders();
                ProcessIncoming(upstream.ReceivedPackets, now);
                PollSender();
                DrainLocalCapture(now);
            }
            else
            {
                while (_controlPackets.Count > 0) _controlPackets.Dequeue();
            }
            Sample(now);
            return new()
            {
                Advertisement = Advertisement(),
                OutgoingPackets = BuildOutgoing(now)
            };
        }
    }

    public void SetReceiverAvailability(bool available)
    {
        lock (_sync)
        {
            RequireSteamLobby();
            if (available == _receiverAvailable) return;
            if (available)
            {
                _receiverAvailable = true;
                _captureId = Token("capture");
                _captureToken = Secret();
                _captureMode = LogStreamingCaptureMode.Stopped;
                _message = "You are available to receive logs from people in this lobby.";
                return;
            }
            StopReceiving("Log receiving was stopped.");
        }
    }

    public void SetDeepTraceEnabled(bool enabled)
    {
        lock (_sync)
        {
            RequireSteamLobby();
            if (!_receiverAvailable)
                throw new InvalidOperationException("Make yourself available to receive logs first.");
            if (_deepTraceEnabled == enabled) return;
            if (enabled)
            {
                _deepTraceEnabled = true;
                EnsureLocalDeepSender();
                _message = "Deep trace is on. Approved senders will stream new position, input, and performance samples.";
                AppendTelemetry(_telemetry.CompanionAction(Now, "setting-changed", "enable-deep-trace",
                    null, null, null, true, _message));
            }
            else
            {
                _deepTraceEnabled = false;
                _message = "Deep trace is off. Raw logs and diagnostic events continue streaming.";
                AppendTelemetry(_telemetry.CompanionAction(Now, "setting-changed", "disable-deep-trace",
                    null, null, null, true, _message));
            }
        }
    }

    public void ObserveLiveSnapshot(LiveSnapshot? snapshot)
    {
        lock (_sync)
        {
            DateTimeOffset now = Now;
            foreach (LogStreamTelemetryRecord record in _telemetry.Observe(snapshot, now)) AppendTelemetry(record);
            if (snapshot is null || (!_deepTraceEnabled && _deepSenders.Count == 0)
                || now - _lastDeepTraceSample < DeepTraceInterval) return;

            bool hasRemoteTrace = _deepSenders.Keys.Any(IsDeepTraceActive);
            bool hasLocalTrace = _deepTraceEnabled && _captureMode == LogStreamingCaptureMode.Capturing
                && _localDeepSender is not null;
            if (!hasRemoteTrace && !hasLocalTrace) return;
            IReadOnlyList<LogStreamTelemetryRecord> traces = _telemetry.DeepTrace(snapshot, now);
            foreach (var item in _deepSenders.ToArray())
            {
                if (!IsDeepTraceActive(item.Key)) continue;
                if (traces.All(trace => item.Value.TryAppendGenerated(trace.FileId, trace.Data))) continue;
                if (_outgoing.TryGetValue(item.Key, out var transfer))
                {
                    transfer.DeepTraceRejected = true;
                    transfer.Error = "Deep trace reached its bounded buffer. Normal logs continue.";
                }
                RemoveDeepSender(item.Key);
            }
            LogStreamSenderSession? localDeepSender = _localDeepSender;
            if (hasLocalTrace && localDeepSender is not null
                && traces.Any(trace => !localDeepSender.TryAppendGenerated(trace.FileId, trace.Data)))
            {
                _capture?.TryMarkSessionInterrupted(LocalIdentity(), localDeepSender.SourceSessionId,
                    "Deep trace reached its bounded buffer.");
                _localDeepSender = null;
                _message = "Local Deep trace reached its bounded buffer. Raw logs and diagnostic events continue.";
            }
            _lastDeepTraceSample = now;
        }
    }

    public void RecordCompanionAction(
        string phase,
        string action,
        string? playerId,
        string? roomId,
        string? region,
        bool? success,
        string? message)
    {
        lock (_sync)
        {
            AppendTelemetry(_telemetry.CompanionAction(Now, phase, action, playerId, roomId, region, success, message));
        }
    }

    public void SetCaptureMode(LogStreamingCaptureMode mode)
    {
        lock (_sync)
        {
            RequireSteamLobby();
            if (mode == LogStreamingCaptureMode.Stopped)
            {
                StopReceiving("Capture stopped. Existing approvals were cleared.");
                return;
            }
            if (!_receiverAvailable) throw new InvalidOperationException("Make yourself available to receive logs first.");
            EnsureSender();
            if (_capture is null)
            {
                var captureOptions = (_options.CaptureOptions ?? new LogStreamCaptureOptions()) with { LobbyId = _lobby.LobbyId };
                _capture = new LogStreamCaptureWriter(_destinationRoot, _captureId, captureOptions);
                _lastCaptureFolder = _capture.CaptureDirectory;
            }
            _sender!.AddReceiver(LocalCaptureReceiverId);
            _captureMode = mode;
            if (mode == LogStreamingCaptureMode.Capturing)
            {
                if (_deepTraceEnabled) EnsureLocalDeepSender();
            }
            _message = mode == LogStreamingCaptureMode.Capturing
                ? "Capture is running. Approved logs will be written and shown live."
                : "Capture is paused. Approvals are preserved and senders will wait.";
        }
    }

    public void PrepareSharing(IReadOnlyList<string> receiverSteamIds)
    {
        ArgumentNullException.ThrowIfNull(receiverSteamIds);
        lock (_sync)
        {
            RequireSteamLobby();
            EnsureSender();
            var peers = _lobby.Peers.ToDictionary(peer => peer.SteamId, StringComparer.Ordinal);
            int prepared = 0;
            foreach (string id in receiverSteamIds.Distinct(StringComparer.Ordinal))
            {
                if (!peers.TryGetValue(id, out var peer) || !Compatible(peer) || !peer.ReceiverAvailable
                    || peer.CaptureId.Length == 0 || peer.CaptureToken.Length == 0)
                    continue;
                if (_outgoing.TryGetValue(id, out var existing)
                    && existing.CaptureId == peer.CaptureId && existing.CaptureToken == peer.CaptureToken)
                {
                    RemoveQueuedPackets(existing);
                    if (existing.StorageLimited || existing.DeepTraceRejected)
                    {
                        var replacement = new OutgoingTransfer(peer, Token("transfer"), Secret(), Now);
                        _outgoing[id] = replacement;
                        QueueOpen(replacement, Now);
                        prepared++;
                        continue;
                    }
                    existing.Invalidated = false;
                    existing.Error = "";
                    QueueOpen(existing, now: Now);
                    prepared++;
                    continue;
                }
                if (existing is not null)
                {
                    RemoveQueuedPackets(existing);
                    _sender!.RemoveReceiver(id);
                    RemoveDeepSender(id);
                }
                _sender!.AddReceiver(id);
                var transfer = new OutgoingTransfer(peer, Token("transfer"), Secret(), Now);
                _outgoing[id] = transfer;
                QueueOpen(transfer, Now);
                prepared++;
            }
            _message = prepared == 0
                ? "No selected player is currently available with a compatible Game Hook."
                : prepared == 1 ? "Prepared one player to receive your complete current logs."
                : $"Prepared {prepared} players to receive your complete current logs.";
        }
    }

    public void SetDestinationRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        lock (_sync)
        {
            if (_captureMode != LogStreamingCaptureMode.Stopped || _capture is not null)
                throw new InvalidOperationException("Stop the current capture before changing its folder.");
            string fullPath = Path.GetFullPath(path.Trim());
            Directory.CreateDirectory(fullPath);
            _destinationRoot = fullPath;
            _message = "New captures will be saved in " + fullPath + ".";
        }
    }

    public void RevokeSharing(IReadOnlyList<string> receiverSteamIds)
    {
        ArgumentNullException.ThrowIfNull(receiverSteamIds);
        lock (_sync)
        {
            int revoked = 0;
            foreach (string id in receiverSteamIds.Distinct(StringComparer.Ordinal))
            {
                if (!_outgoing.Remove(id, out var transfer)) continue;
                RemoveQueuedPackets(transfer);
                QueueNetwork(id, Message(LogStreamKinds.Close, transfer));
                _sender?.RemoveReceiver(id);
                RemoveDeepSender(id);
                revoked++;
            }
            _message = revoked == 0 ? "No active sharing approval was selected." : "Selected log sharing was revoked.";
        }
    }

    public void RevokeAllSharing()
    {
        lock (_sync)
        {
            RevokeSharing(_outgoing.Keys.ToArray());
            _message = "All outgoing log sharing was revoked.";
        }
    }

    public void StopAll()
    {
        lock (_sync)
        {
            if (_receiverAvailable) StopReceiving("Log receiving was stopped.");
            RevokeSharing(_outgoing.Keys.ToArray());
            _message = "All log streaming was stopped and approvals were cleared.";
        }
    }

    public void MarkEvent(string note)
    {
        lock (_sync)
        {
            if (_capture is null || _captureMode == LogStreamingCaptureMode.Stopped)
                throw new InvalidOperationException("Start a capture before marking an event.");
            _capture.MarkEvent(note);
            _message = "Event marker saved to this capture.";
        }
    }

    public LogStreamingCoordinatorSnapshot Snapshot()
    {
        lock (_sync)
        {
            DateTimeOffset now = Now;
            var ids = _remembered.Keys.Concat(_lobby.Peers.Select(peer => peer.SteamId))
                .Concat(_incoming.Keys).Concat(_outgoing.Keys).Distinct(StringComparer.Ordinal);
            var peers = ids.Select(id => BuildPeer(id, now))
                .OrderBy(peer => peer.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(peer => peer.SteamId, StringComparer.Ordinal)
                .ToArray();
            return new(now, _lobby.IsConnected && _lobby.IsSteam, _receiverAvailable, _deepTraceEnabled,
                _captureMode, _destinationRoot, _lastCaptureFolder, _message, peers,
                _viewerLines.ToArray(), _samples.ToArray());
        }
    }

    public void Shutdown()
    {
        lock (_sync)
        {
            ResetContext("Companion closed while this capture was active.");
            _lobby = new();
            _gameSessionId = "";
        }
    }

    internal (int Characters, int Streams) ViewerResourceUsage()
    {
        lock (_sync)
        {
            return (_viewerCharacters,
                _localViewers.Count + _incoming.Values.Sum(transfer => transfer.Viewers.Count));
        }
    }

    internal (int Sessions, long BufferedBytes) DeepTraceResourceUsage()
    {
        lock (_sync)
        {
            var sessions = _deepSenders.Values.Append(_localDeepSender)
                .Where(sender => sender is not null).Cast<LogStreamSenderSession>().ToArray();
            return (sessions.Length, sessions.Sum(sender => sender.GetSnapshot().SpoolBytes));
        }
    }

    private void AdoptContext(LogBridgeUpstream upstream, DateTimeOffset now)
    {
        bool valid = upstream.Lobby is { IsConnected: true, IsSteam: true }
            && ValidNumericId(upstream.Lobby.LocalSteamId)
            && upstream.Lobby.LobbyId.Length > 0;
        if (!valid)
        {
            if (_lobby.IsConnected || _gameSessionId.Length > 0) ResetContext("Join a Steam Rain Meadow lobby to stream logs.");
            _lobby = upstream.Lobby ?? new();
            _gameSessionId = "";
            return;
        }
        bool changed = _gameSessionId.Length > 0 && (!string.Equals(_gameSessionId, upstream.GameSessionId, StringComparison.Ordinal)
            || !string.Equals(_lobby.LobbyId, upstream.Lobby.LobbyId, StringComparison.Ordinal)
            || !string.Equals(_lobby.LocalSteamId, upstream.Lobby.LocalSteamId, StringComparison.Ordinal));
        if (changed) ResetContext("The game or Meadow lobby changed. Log approvals were cleared.");
        _lobby = upstream.Lobby;
        _gameSessionId = upstream.GameSessionId;
        if (_message.StartsWith("Join a Steam", StringComparison.Ordinal)) _message = "Log streaming is idle.";
    }

    private void ResetContext(string message)
    {
        foreach (var transfer in _incoming.Values) InterruptIncoming(transfer, message);
        _capture?.MarkInterrupted(message);
        _outgoing.Clear();
        _incoming.Clear();
        _remembered.Clear();
        _sender = null;
        _deepSenders.Clear();
        _localDeepSender = null;
        _capture = null;
        _receiverAvailable = false;
        _deepTraceEnabled = false;
        _captureMode = LogStreamingCaptureMode.Stopped;
        _captureId = "";
        _captureToken = "";
        _incomingBudgets.Clear();
        _incomingOpenRates.Clear();
        _revokedIncoming.Clear();
        _revokedIncomingOrder.Clear();
        _pendingTelemetry.Clear();
        _pendingTelemetryBytes = 0;
        _localViewers.Clear();
        _lastDeepTraceSample = DateTimeOffset.MinValue;
        _telemetry.Reset();
        _sendBudget = _incomingBudget = _options.MaximumBytesPerSecond;
        _incomingPacketBudget = MaximumIncomingPacketsPerSecond;
        _lastBudget = Now;
        while (_controlPackets.Count > 0) _controlPackets.Dequeue();
        _message = message;
    }

    private void StopReceiving(string message)
    {
        foreach (var transfer in _incoming.Values)
        {
            InterruptIncoming(transfer, message);
            QueueNetwork(transfer.SteamId, Message(LogStreamKinds.Close, transfer));
        }
        _incoming.Clear();
        _capture?.MarkInterrupted(message);
        _capture = null;
        _sender?.RemoveReceiver(LocalCaptureReceiverId);
        _localViewers.Clear();
        InterruptDeepTraceSessions(message);
        _localDeepSender = null;
        _receiverAvailable = false;
        _deepTraceEnabled = false;
        _captureMode = LogStreamingCaptureMode.Stopped;
        _captureId = "";
        _captureToken = "";
        _revokedIncoming.Clear();
        _revokedIncomingOrder.Clear();
        _incomingOpenRates.Clear();
        _message = message;
    }

    private void EnsureSender()
    {
        string? install = _options.GameInstallPath();
        if (string.IsNullOrWhiteSpace(install) || !Directory.Exists(install))
            throw new InvalidOperationException("The configured Rain World installation is unavailable.");
        if (!ulong.TryParse(_lobby.LocalSteamId, NumberStyles.None, CultureInfo.InvariantCulture, out ulong steamId))
            throw new InvalidOperationException("The local Steam identity is unavailable.");
        if (!TokenValid(_gameSessionId, 128))
            throw new InvalidOperationException("The authenticated game session is unavailable.");
        if (_sender is not null) return;
        _sender = new LogStreamSenderSession(install, Token("source"), _gameSessionId, steamId, _options.SenderOptions);
        while (_pendingTelemetry.Count > 0)
        {
            LogStreamTelemetryRecord record = _pendingTelemetry.Peek();
            if (!_sender.TryAppendGenerated(record.FileId, record.Data)) break;
            _pendingTelemetry.Dequeue();
            _pendingTelemetryBytes -= record.Data.Length;
        }
        _sender.Poll();
    }

    private LogStreamSenderSession CreateDeepSender()
    {
        EnsureSender();
        string install = _options.GameInstallPath()!;
        ulong.TryParse(_lobby.LocalSteamId, NumberStyles.None, CultureInfo.InvariantCulture, out ulong steamId);
        var sourceOptions = _options.SenderOptions ?? new LogStreamSenderOptions();
        var options = sourceOptions with
        {
            PollSourceLogs = false,
            MaxSpoolBytes = Math.Min(sourceOptions.MaxSpoolBytes, MaximumDeepTraceSpoolBytes),
            CompactAcknowledgedChunks = true
        };
        return new LogStreamSenderSession(install, Token("deep-source"), Token("deep"), steamId, options);
    }

    private void EnsureLocalDeepSender()
    {
        if (!_deepTraceEnabled || !_receiverAvailable || _captureMode != LogStreamingCaptureMode.Capturing
            || _localDeepSender is not null) return;
        _localDeepSender = CreateDeepSender();
        _localDeepSender.AddReceiver(LocalCaptureReceiverId);
    }

    private void SyncDeepTraceSenders()
    {
        foreach (var transfer in _outgoing.Values)
        {
            var peer = _lobby.Peers.FirstOrDefault(item => item.SteamId == transfer.SteamId);
            if (peer?.DeepTraceEnabled != true) transfer.DeepTraceRejected = false;
            bool validApproval = peer is not null && Compatible(peer) && HasFreshAdvertisement(peer)
                && !transfer.Invalidated && peer.ReceiverAvailable
                && peer.CaptureId == transfer.CaptureId && peer.CaptureToken == transfer.CaptureToken;
            if (!validApproval)
            {
                RemoveDeepSender(transfer.SteamId);
                continue;
            }
            if (!IsDeepTraceActive(transfer.SteamId))
            {
                if (_deepSenders.TryGetValue(transfer.SteamId, out var inactive))
                {
                    foreach (var key in transfer.InFlight.Keys
                                 .Where(key => key.SessionId == inactive.SourceSessionId).ToArray())
                        transfer.InFlight.Remove(key);
                }
                continue;
            }
            if (_deepSenders.ContainsKey(transfer.SteamId)) continue;
            if (_deepSenders.Count >= MaximumConcurrentDeepTraceReceivers)
            {
                transfer.DeepTraceRejected = true;
                transfer.Error = "Deep trace reached its concurrent receiver limit. Normal logs continue.";
                continue;
            }
            var sender = CreateDeepSender();
            sender.AddReceiver(transfer.SteamId);
            _deepSenders.Add(transfer.SteamId, sender);
        }

        foreach (string peerId in _deepSenders.Keys.Where(id => !_outgoing.ContainsKey(id)).ToArray())
            RemoveDeepSender(peerId);
    }

    private bool IsDeepTraceActive(string peerId)
    {
        if (!_outgoing.TryGetValue(peerId, out var transfer) || transfer.DeepTraceRejected
            || !transfer.Accepted || transfer.Invalidated) return false;
        var peer = _lobby.Peers.FirstOrDefault(item => item.SteamId == peerId);
        return peer is not null && Compatible(peer) && HasFreshAdvertisement(peer)
            && peer.ReceiverAvailable && peer.CaptureActive && !peer.CapturePaused && peer.DeepTraceEnabled
            && peer.CaptureId == transfer.CaptureId && peer.CaptureToken == transfer.CaptureToken;
    }

    private void RemoveDeepSender(string peerId)
    {
        if (!_deepSenders.Remove(peerId, out var sender)) return;
        if (_outgoing.TryGetValue(peerId, out var transfer))
        {
            foreach (var key in transfer.InFlight.Keys.Where(key => key.SessionId == sender.SourceSessionId).ToArray())
                transfer.InFlight.Remove(key);
        }
    }

    private void InterruptDeepTraceSessions(string reason)
    {
        if (_capture is null) return;
        foreach (var transfer in _incoming.Values)
        {
            foreach (string sessionId in transfer.OpenDeepSessions)
                _capture.TryMarkSessionInterrupted(transfer.Identity, sessionId, reason);
            transfer.OpenDeepSessions.Clear();
        }
        if (_localDeepSender is not null)
            _capture.MarkSessionInterrupted(LocalIdentity(), _localDeepSender.SourceSessionId, reason);
    }

    private void AppendTelemetry(LogStreamTelemetryRecord record)
    {
        if (_sender is not null)
        {
            _sender.TryAppendGenerated(record.FileId, record.Data);
            return;
        }

        if (record.Data.Length > MaximumPendingTelemetryBytes) return;
        while (_pendingTelemetryBytes + record.Data.Length > MaximumPendingTelemetryBytes
               && _pendingTelemetry.TryDequeue(out var dropped))
            _pendingTelemetryBytes -= dropped.Data.Length;
        _pendingTelemetry.Enqueue(record);
        _pendingTelemetryBytes += record.Data.Length;
    }

    private void DrainLocalCapture(DateTimeOffset now)
    {
        if (_captureMode != LogStreamingCaptureMode.Capturing || _capture is null || _sender is null) return;
        _capture.ObservePeer(LocalIdentity());
        DrainLocalSender(_sender, now, 5);
        if (_deepTraceEnabled && _localDeepSender is not null) DrainLocalSender(_localDeepSender, now, 1);
    }

    private void DrainLocalSender(LogStreamSenderSession sender, DateTimeOffset now, int maximumChunks)
    {
        foreach (var sourceChunk in sender.GetPendingChunks(LocalCaptureReceiverId, maximumChunks))
        {
            var chunk = new LogStreamChunk(_captureId, sourceChunk.SourceSessionId, sourceChunk.SenderSteamId,
                sourceChunk.Sequence, sourceChunk.FileId, sourceChunk.Generation, sourceChunk.Offset,
                sourceChunk.CopyData(), sourceChunk.Sha256);
            var result = _capture!.Write(LocalIdentity(), chunk);
            if (!result.ShouldAcknowledge) break;
            var acknowledgement = result.Acknowledgement!;
            var acknowledged = sender.Acknowledge(LocalCaptureReceiverId,
                new(sender.CaptureId, sender.SourceSessionId, acknowledgement.Sequence, acknowledgement.Sha256));
            if (acknowledged.Status == LogStreamAcknowledgeStatus.Accepted)
            {
                _sampleBytes += chunk.Length;
                AddViewer(LocalIdentity(), chunk, now, _localViewers);
            }
        }
    }

    private LogStreamPeerIdentity LocalIdentity()
    {
        ulong.TryParse(_lobby.LocalSteamId, NumberStyles.None, CultureInfo.InvariantCulture, out ulong steamId);
        string name = string.IsNullOrWhiteSpace(_lobby.LocalDisplayName) ? "Local receiver" : _lobby.LocalDisplayName;
        return new(steamId, name, _lobby.LocalIsHost);
    }

    private void ExpireAuthorizations(DateTimeOffset now)
    {
        var expiredIncoming = _incoming.Values
            .Where(transfer => now - transfer.LastSeen > _options.ReconnectGrace).ToArray();
        var expiredOutgoing = _outgoing.Values
            .Where(transfer => now - transfer.LastSeen > _options.ReconnectGrace).ToArray();
        foreach (var transfer in expiredIncoming)
        {
            RemoveQueuedPackets(transfer);
            InterruptIncoming(transfer, "The sender did not reconnect before the approval expired.");
            RevokeIncoming(transfer);
            QueueNetwork(transfer.SteamId, Message(LogStreamKinds.Close, transfer));
            _incoming.Remove(transfer.SteamId);
        }
        foreach (var transfer in expiredOutgoing)
        {
            RemoveQueuedPackets(transfer);
            QueueNetwork(transfer.SteamId, Message(LogStreamKinds.Close, transfer));
            _outgoing.Remove(transfer.SteamId);
            _sender?.RemoveReceiver(transfer.SteamId);
            RemoveDeepSender(transfer.SteamId);
        }
        if (expiredIncoming.Length > 0 || expiredOutgoing.Length > 0)
            _message = "A reconnect window expired. Select the player again to approve a new stream.";

        var currentPeers = _lobby.Peers.Select(peer => peer.SteamId).ToHashSet(StringComparer.Ordinal);
        foreach (string peerId in _remembered
                     .Where(item => !currentPeers.Contains(item.Key)
                         && !_incoming.ContainsKey(item.Key) && !_outgoing.ContainsKey(item.Key)
                         && now - item.Value.LastSeen > _options.ReconnectGrace)
                     .Select(item => item.Key).ToArray())
        {
            _remembered.Remove(peerId);
            _incomingBudgets.Remove(peerId);
            _incomingOpenRates.Remove(peerId);
        }
    }

    private void PollSender()
    {
        if (_sender is null || (_outgoing.Count == 0 && _capture is null)) return;
        _sender.Poll();
    }

    private void RememberPeers(DateTimeOffset now)
    {
        foreach (var peer in _lobby.Peers)
        {
            if (!ValidNumericId(peer.SteamId)) continue;
            _remembered[peer.SteamId] = new(peer.DisplayName, peer.IsHost, now);
            bool fresh = HasFreshAdvertisement(peer);
            if (fresh && _incoming.TryGetValue(peer.SteamId, out var presentIncoming))
            {
                presentIncoming.Identity = PeerIdentity(peer);
                if (ulong.TryParse(peer.SteamId, NumberStyles.None, CultureInfo.InvariantCulture, out ulong steamId))
                    _capture?.ObservePeer(new(steamId, peer.DisplayName, peer.IsHost));
            }
            if (_outgoing.TryGetValue(peer.SteamId, out var transfer)
                && fresh
                && (peer.CaptureId != transfer.CaptureId || peer.CaptureToken != transfer.CaptureToken || !peer.ReceiverAvailable))
            {
                transfer.Invalidated = true;
                transfer.Accepted = false;
                transfer.InFlight.Clear();
                RemoveDeepSender(peer.SteamId);
            }
        }
    }

    private void ProcessIncoming(IEnumerable<LogRelayPacket> packets, DateTimeOffset now)
    {
        var current = _lobby.Peers.ToDictionary(peer => peer.SteamId, StringComparer.Ordinal);
        foreach (var packet in packets)
        {
            if (!current.TryGetValue(packet.PeerSteamId, out var peer) || !Compatible(peer)) continue;
            if (packet.Payload is null || packet.Payload.Length > ProtocolInfo.MaximumLogPacketLength
                || !TryConsumeIncoming(peer.SteamId, Math.Max(MinimumIncomingPacketCost, PacketCost(packet))))
                continue;
            LogStreamNetworkMessage message;
            try { message = LiveJson.Deserialize<LogStreamNetworkMessage>(Encoding.UTF8.GetString(packet.Payload)); }
            catch (Exception error) when (error is global::System.Runtime.Serialization.SerializationException
                or ArgumentException or global::System.Xml.XmlException or DecoderFallbackException) { continue; }
            if (!ValidMessage(message, packet.PeerSteamId)) continue;
            switch (message.Kind)
            {
                case LogStreamKinds.Open: ReceiveOpen(peer, message, now); break;
                case LogStreamKinds.OpenAccepted: ReceiveOpenAccepted(message, now); break;
                case LogStreamKinds.Chunk: ReceiveChunk(peer, message, now); break;
                case LogStreamKinds.Acknowledgement: ReceiveAcknowledgement(message, now); break;
                case LogStreamKinds.Close: ReceiveClose(message); break;
                case LogStreamKinds.Error: ReceiveError(message); break;
            }
        }
    }

    private void ReceiveOpen(LogLobbyPeer peer, LogStreamNetworkMessage message, DateTimeOffset now)
    {
        if (IsDeepSession(message.LogSessionId))
        {
            QueueNetwork(peer.SteamId, Reply(LogStreamKinds.Error, message,
                "Deep trace uses the existing approved stream."));
            return;
        }
        if (!_receiverAvailable || message.CaptureId != _captureId || message.CaptureToken != _captureToken)
        {
            QueueNetwork(peer.SteamId, Reply(LogStreamKinds.Error, message, "This receiver is no longer available. Ask them to approve a new capture."));
            return;
        }
        if (_revokedIncoming.Contains(IncomingAuthorization.From(peer.SteamId, message)))
        {
            QueueNetwork(peer.SteamId, Reply(LogStreamKinds.Error, message,
                "This sharing approval expired. Select this receiver again to create a new approval."));
            return;
        }
        _incoming.TryGetValue(peer.SteamId, out var transfer);
        bool current = transfer is not null
            && transfer.TransferId == message.TransferId
            && transfer.ConsentToken == message.ConsentToken
            && transfer.LogSessionId == message.LogSessionId;
        if (!current)
        {
            if (!TryAcceptIncomingOpen(peer.SteamId, now, out bool notify))
            {
                if (notify)
                {
                    QueueNetwork(peer.SteamId, Reply(LogStreamKinds.Error, message,
                        "Too many new log sharing approvals were opened. Try again shortly."));
                }
                return;
            }
            if (transfer is not null)
            {
                InterruptIncoming(transfer, "The sender replaced this sharing approval.");
                RevokeIncoming(transfer);
            }
            transfer = new IncomingTransfer(peer, message.TransferId, message.ConsentToken, message.LogSessionId, now);
            _incoming[peer.SteamId] = transfer;
        }
        if (now - transfer!.LastSeen > TransferHeartbeatStale) transfer.ReconnectCount++;
        transfer.LastSeen = now;
        QueueNetwork(peer.SteamId, Reply(LogStreamKinds.OpenAccepted, message, "Ready."));
    }

    private void ReceiveOpenAccepted(LogStreamNetworkMessage message, DateTimeOffset now)
    {
        if (!TryMatchOutgoing(message, out var transfer)) return;
        transfer.Accepted = true;
        transfer.Invalidated = false;
        if (now - transfer.LastSeen > TransferHeartbeatStale) transfer.ReconnectCount++;
        transfer.LastSeen = now;
    }

    private void ReceiveChunk(LogLobbyPeer peer, LogStreamNetworkMessage message, DateTimeOffset now)
    {
        if (!_incoming.TryGetValue(peer.SteamId, out var transfer) || !Matches(message, transfer)
            || !_receiverAvailable || message.CaptureId != _captureId || message.CaptureToken != _captureToken)
            return;
        transfer.LastSeen = now;
        if (_captureMode != LogStreamingCaptureMode.Capturing || _capture is null) return;
        if (!ulong.TryParse(peer.SteamId, NumberStyles.None, CultureInfo.InvariantCulture, out ulong senderId)) return;
        LogStreamChunk chunk;
        try
        {
            chunk = new(_captureId, message.LogSessionId, senderId, message.Sequence,
                message.FileId, message.Generation, message.Offset, message.Data, message.Hash);
        }
        catch (Exception error) when (error is ArgumentException or OverflowException) { return; }
        bool deepSession = IsDeepSession(message.LogSessionId);
        if (deepSession)
        {
            if (!_deepTraceEnabled || !IsDeepTraceFile(message.FileId)) return;
            if (!transfer.DeepSessions.Contains(message.LogSessionId)
                && transfer.DeepSessions.Count >= MaximumDeepSessionsPerTransfer)
            {
                QueueNetwork(peer.SteamId, Reply(LogStreamKinds.Error, message,
                    "Too many Deep trace sessions were opened for this approval."));
                return;
            }
            transfer.DeepSessions.Add(message.LogSessionId);
            transfer.OpenDeepSessions.Add(message.LogSessionId);
        }
        else if (IsDeepTraceFile(message.FileId)) return;
        var result = _capture.Write(new(senderId, peer.DisplayName, peer.IsHost), chunk);
        if (result.ShouldAcknowledge)
        {
            var acknowledgement = result.Acknowledgement!;
            QueueNetwork(peer.SteamId, Reply(LogStreamKinds.Acknowledgement, message, "Flushed.", acknowledgement.Sequence, acknowledgement.Sha256));
            long lastAcknowledged = transfer.LastAcknowledgedSequences.GetValueOrDefault(message.LogSessionId);
            bool newlyAcknowledged = message.Sequence > lastAcknowledged;
            transfer.LastAcknowledgedSequences[message.LogSessionId] = Math.Max(lastAcknowledged, message.Sequence);
            transfer.AcknowledgedBytes += newlyAcknowledged ? chunk.Length : 0;
            transfer.BytesSinceSample += newlyAcknowledged ? chunk.Length : 0;
            transfer.LastFlush = now;
            transfer.StorageProblem = "";
            if (newlyAcknowledged)
            {
                _sampleBytes += chunk.Length;
                AddViewer(peer, chunk, now);
            }
        }
        else if (result.Status is LogStreamWriteStatus.CaptureLimitReached or LogStreamWriteStatus.InsufficientDiskSpace
                 or LogStreamWriteStatus.IoError)
        {
            transfer.StorageProblem = result.Message;
            QueueNetwork(peer.SteamId, Reply(LogStreamKinds.Error, message, result.Message));
        }
    }

    private void ReceiveAcknowledgement(LogStreamNetworkMessage message, DateTimeOffset now)
    {
        if (!TryMatchOutgoing(message, out var transfer) || SenderFor(transfer.SteamId, message.LogSessionId) is not { } sender)
            return;
        var result = sender.Acknowledge(transfer.SteamId,
            new(sender.CaptureId, sender.SourceSessionId, message.Sequence, message.Hash));
        if (result.Status is not (LogStreamAcknowledgeStatus.Accepted or LogStreamAcknowledgeStatus.Duplicate)) return;
        long before = transfer.AcknowledgedBySession.GetValueOrDefault(sender.SourceSessionId);
        transfer.AcknowledgedBySession[sender.SourceSessionId] = result.BytesAcknowledged;
        long added = Math.Max(0, result.BytesAcknowledged - before);
        transfer.AcknowledgedBytes += added;
        transfer.BytesSinceSample += added;
        _sampleBytes += added;
        transfer.LastSeen = now;
        foreach (var key in transfer.InFlight.Keys
                     .Where(key => key.SessionId == message.LogSessionId && key.Sequence <= message.Sequence).ToArray())
            transfer.InFlight.Remove(key);
    }

    private void ReceiveClose(LogStreamNetworkMessage message)
    {
        if (_incoming.TryGetValue(message.SenderSteamId, out var incoming) && Matches(message, incoming))
        {
            InterruptIncoming(incoming, "The sender closed without a confirmed final watermark.");
            RevokeIncoming(incoming);
            _incoming.Remove(message.SenderSteamId);
        }
        if (TryMatchOutgoing(message, out var outgoing))
        {
            outgoing.Invalidated = true;
            outgoing.Accepted = false;
        }
    }

    private void ReceiveError(LogStreamNetworkMessage message)
    {
        if (TryMatchOutgoing(message, out var outgoing))
        {
            outgoing.Error = SafeRemoteMessage(message.Message);
            if (IsDeepSession(message.LogSessionId))
            {
                outgoing.DeepTraceRejected = true;
                RemoveDeepSender(outgoing.SteamId);
            }
            else if (outgoing.Error.Contains("limit", StringComparison.OrdinalIgnoreCase)
                     || outgoing.Error.Contains("space", StringComparison.OrdinalIgnoreCase))
                outgoing.StorageLimited = true;
        }
    }

    private LogRelayPacket[] BuildOutgoing(DateTimeOffset now)
    {
        if (!_lobby.IsConnected || !_lobby.IsSteam) return [];
        var activePeers = _lobby.Peers.Select(peer => peer.SteamId).ToHashSet(StringComparer.Ordinal);
        var result = new List<LogRelayPacket>(MaximumPacketsPerExchange);
        int controls = _controlPackets.Count;
        while (controls-- > 0 && result.Count < MaximumPacketsPerExchange && _controlPackets.TryDequeue(out var packet))
        {
            if (activePeers.Contains(packet.PeerSteamId) && PacketCost(packet) <= _sendBudget)
            {
                result.Add(packet);
                _sendBudget -= PacketCost(packet);
            }
            else _controlPackets.Enqueue(packet);
        }
        if (_sender is null || result.Count >= MaximumPacketsPerExchange || _outgoing.Count == 0) return result.ToArray();
        var transfers = _outgoing.Values.OrderBy(transfer => transfer.SteamId, StringComparer.Ordinal).ToArray();
        for (int index = 0; index < transfers.Length && result.Count < MaximumPacketsPerExchange; index++)
        {
            var transfer = transfers[(_roundRobin + index) % transfers.Length];
            var peer = _lobby.Peers.FirstOrDefault(item => item.SteamId == transfer.SteamId);
            if (peer is null || !Compatible(peer) || !HasFreshAdvertisement(peer)
                || transfer.Invalidated || transfer.StorageLimited) continue;
            if (now - transfer.LastOpenSent >= TransferHeartbeatInterval) QueueOpen(transfer, now);
            if (!transfer.Accepted) continue;
            if (!peer.CaptureActive || peer.CapturePaused) continue;
            var senders = new List<LogStreamSenderSession>(2) { _sender };
            if (IsDeepTraceActive(transfer.SteamId)
                && _deepSenders.TryGetValue(transfer.SteamId, out var deepSender))
            {
                if (transfer.PreferDeepTrace) senders.Insert(0, deepSender);
                else senders.Add(deepSender);
            }
            LogStreamChunk? chunk = null;
            foreach (var candidate in senders)
            {
                var pending = candidate.GetPendingChunks(transfer.SteamId, 9);
                var resend = pending.FirstOrDefault(item => transfer.InFlight.TryGetValue(
                        new(item.SourceSessionId, item.Sequence), out var sent)
                    && now - sent.LastSentAt >= TimeSpan.FromSeconds(2));
                chunk = resend ?? pending.FirstOrDefault(item => !transfer.InFlight.ContainsKey(
                    new(item.SourceSessionId, item.Sequence)));
                if (chunk is not null) break;
            }
            transfer.PreferDeepTrace = !transfer.PreferDeepTrace;
            if (chunk is null) continue;
            var chunkKey = new ChunkKey(chunk.SourceSessionId, chunk.Sequence);
            long inFlight = transfer.InFlight.Values.Sum(item => (long)item.Length);
            if (!transfer.InFlight.ContainsKey(chunkKey) && inFlight + chunk.Length > MaximumUnacknowledgedBytes) continue;
            var network = Message(LogStreamKinds.Chunk, transfer);
            network.LogSessionId = chunk.SourceSessionId;
            network.FileId = chunk.FileId;
            network.Generation = chunk.Generation;
            network.Offset = chunk.Offset;
            network.Sequence = chunk.Sequence;
            network.Data = chunk.CopyData();
            network.Hash = chunk.Sha256;
            if (TryPacket(transfer.SteamId, network, out var relay) && PacketCost(relay) <= _sendBudget)
            {
                result.Add(relay);
                if (transfer.InFlight.TryGetValue(chunkKey, out var sent)) sent.LastSentAt = now;
                else transfer.InFlight.Add(chunkKey, new(chunk.Length, now));
                _sendBudget -= PacketCost(relay);
            }
        }
        _roundRobin = (_roundRobin + 1) % Math.Max(1, transfers.Length);
        return result.ToArray();
    }

    private void QueueOpen(OutgoingTransfer transfer, DateTimeOffset now)
    {
        QueueNetwork(transfer.SteamId, Message(LogStreamKinds.Open, transfer));
        transfer.LastOpenSent = now;
    }

    private void QueueNetwork(string peerId, LogStreamNetworkMessage message)
    {
        if (_controlPackets.Count >= MaximumControlPackets) return;
        if (TryPacket(peerId, message, out var packet)) _controlPackets.Enqueue(packet);
    }

    private void RemoveQueuedPackets(OutgoingTransfer transfer)
        => RemoveQueuedPackets(transfer.SteamId, transfer.TransferId, transfer.ConsentToken);

    private void RemoveQueuedPackets(IncomingTransfer transfer)
        => RemoveQueuedPackets(transfer.SteamId, transfer.TransferId, transfer.ConsentToken);

    private void RemoveQueuedPackets(string peerId, string transferId, string consentToken)
    {
        int count = _controlPackets.Count;
        while (count-- > 0 && _controlPackets.TryDequeue(out var packet))
        {
            bool matches = false;
            if (string.Equals(packet.PeerSteamId, peerId, StringComparison.Ordinal))
            {
                try
                {
                    var message = LiveJson.Deserialize<LogStreamNetworkMessage>(Encoding.UTF8.GetString(packet.Payload));
                    matches = message.TransferId == transferId && message.ConsentToken == consentToken;
                }
                catch (Exception error) when (error is global::System.Runtime.Serialization.SerializationException
                    or ArgumentException or global::System.Xml.XmlException or DecoderFallbackException)
                {
                }
            }
            if (!matches) _controlPackets.Enqueue(packet);
        }
    }

    private void InterruptIncoming(IncomingTransfer transfer, string reason)
    {
        _capture?.TryMarkSessionInterrupted(transfer.Identity, transfer.LogSessionId, reason);
        foreach (string sessionId in transfer.OpenDeepSessions)
            _capture?.TryMarkSessionInterrupted(transfer.Identity, sessionId, reason);
    }

    private bool TryAcceptIncomingOpen(string peerId, DateTimeOffset now, out bool notify)
    {
        if (!_incomingOpenRates.TryGetValue(peerId, out var rate))
        {
            rate = new IncomingOpenRate();
            _incomingOpenRates.Add(peerId, rate);
        }
        while (rate.Accepted.TryPeek(out var acceptedAt) && now - acceptedAt >= IncomingOpenWindow)
            rate.Accepted.Dequeue();
        if (rate.Accepted.Count < MaximumIncomingOpensPerMinute)
        {
            rate.Accepted.Enqueue(now);
            notify = false;
            return true;
        }

        notify = rate.LastNotice == DateTimeOffset.MinValue
            || now - rate.LastNotice >= IncomingOpenNoticeInterval;
        if (notify) rate.LastNotice = now;
        return false;
    }

    private void RevokeIncoming(IncomingTransfer transfer)
    {
        var authorization = IncomingAuthorization.From(transfer);
        if (!_revokedIncoming.Add(authorization)) return;
        _revokedIncomingOrder.Enqueue(authorization);
        while (_revokedIncomingOrder.Count > MaximumRevokedIncomingAuthorizations)
            _revokedIncoming.Remove(_revokedIncomingOrder.Dequeue());
    }

    private static bool TryPacket(string peerId, LogStreamNetworkMessage message, out LogRelayPacket packet)
    {
        byte[] payload = Encoding.UTF8.GetBytes(LiveJson.Serialize(message));
        packet = new() { PeerSteamId = peerId, Payload = payload };
        return payload.Length <= ProtocolInfo.MaximumLogPacketLength;
    }

    private static int PacketCost(LogRelayPacket packet) => checked(packet.Payload.Length + 72);

    private LogStreamNetworkMessage Message(string kind, OutgoingTransfer transfer) => new()
    {
        Kind = kind,
        LobbyId = _lobby.LobbyId,
        SenderSteamId = _lobby.LocalSteamId,
        ReceiverSteamId = transfer.SteamId,
        CaptureId = transfer.CaptureId,
        CaptureToken = transfer.CaptureToken,
        TransferId = transfer.TransferId,
        ConsentToken = transfer.ConsentToken,
        LogSessionId = _sender?.SourceSessionId ?? _gameSessionId
    };

    private LogStreamNetworkMessage Message(string kind, IncomingTransfer transfer) => new()
    {
        Kind = kind,
        LobbyId = _lobby.LobbyId,
        SenderSteamId = _lobby.LocalSteamId,
        ReceiverSteamId = transfer.SteamId,
        CaptureId = _captureId,
        CaptureToken = _captureToken,
        TransferId = transfer.TransferId,
        ConsentToken = transfer.ConsentToken,
        LogSessionId = transfer.LogSessionId
    };

    private LogStreamNetworkMessage Reply(string kind, LogStreamNetworkMessage request, string text,
        long sequence = 0, string hash = "") => new()
    {
        Kind = kind,
        LobbyId = _lobby.LobbyId,
        SenderSteamId = _lobby.LocalSteamId,
        ReceiverSteamId = request.SenderSteamId,
        CaptureId = request.CaptureId,
        CaptureToken = request.CaptureToken,
        TransferId = request.TransferId,
        ConsentToken = request.ConsentToken,
        LogSessionId = request.LogSessionId,
        Sequence = sequence,
        Hash = hash,
        Message = text
    };

    private bool ValidMessage(LogStreamNetworkMessage message, string authenticatedSender)
    {
        if (message is null || message.Kind is null || message.LobbyId is null
            || message.SenderSteamId is null || message.ReceiverSteamId is null
            || message.CaptureId is null || message.CaptureToken is null
            || message.TransferId is null || message.ConsentToken is null
            || message.LogSessionId is null || message.FileId is null
            || message.Hash is null || message.Message is null
            || message.Version != ProtocolInfo.LogStreamingVersion
            || message.LobbyId != _lobby.LobbyId || message.SenderSteamId != authenticatedSender
            || message.ReceiverSteamId != _lobby.LocalSteamId || !ValidNumericId(message.SenderSteamId)
            || message.Kind.Length is < 1 or > 24 || message.CaptureId.Length > 96
            || message.CaptureToken.Length > 192 || message.TransferId.Length > 96
            || message.ConsentToken.Length > 192 || message.LogSessionId.Length > 128
            || message.FileId.Length > 64 || message.Hash.Length > 128 || message.Message.Length > 512
            || message.Data is null || message.Data.Length > LogStreamSenderOptions.DefaultChunkSize
            || message.Sequence < 0 || message.Generation < 0 || message.Offset < 0)
            return false;
        return TokenValid(message.CaptureId, 96) && TokenValid(message.CaptureToken, 192)
            && TokenValid(message.TransferId, 96) && TokenValid(message.ConsentToken, 192)
            && TokenValid(message.LogSessionId, 128) && ValidMessageFields(message);
    }

    private static bool ValidMessageFields(LogStreamNetworkMessage message)
    {
        bool emptyData = message.Data.Length == 0;
        bool emptyChunkFields = message.FileId.Length == 0 && message.Generation == 0
            && message.Offset == 0 && emptyData;
        return message.Kind switch
        {
            LogStreamKinds.Open or LogStreamKinds.Close => emptyChunkFields && message.Sequence == 0
                && message.Hash.Length == 0 && message.Message.Length == 0,
            LogStreamKinds.OpenAccepted => emptyChunkFields && message.Sequence == 0
                && message.Hash.Length == 0,
            LogStreamKinds.Error => emptyChunkFields && message.Sequence == 0
                && message.Hash.Length == 0 && !string.IsNullOrWhiteSpace(message.Message),
            LogStreamKinds.Acknowledgement => emptyChunkFields && message.Sequence > 0
                && ValidSha256(message.Hash),
            LogStreamKinds.Chunk => message.Sequence > 0 && message.Generation > 0
                && message.Data.Length > 0 && message.Message.Length == 0
                && ValidSha256(message.Hash) && LogStreamFileCatalog.TryGet(message.FileId, out _),
            _ => false,
        };
    }

    private static bool ValidSha256(string value)
        => value.Length == 64 && value.All(char.IsAsciiHexDigit);

    private bool TryMatchOutgoing(LogStreamNetworkMessage message, out OutgoingTransfer transfer)
    {
        if (_outgoing.TryGetValue(message.SenderSteamId, out transfer!) && Matches(message, transfer)) return true;
        transfer = null!;
        return false;
    }

    private bool Matches(LogStreamNetworkMessage message, OutgoingTransfer transfer)
        => message.TransferId == transfer.TransferId && message.ConsentToken == transfer.ConsentToken
            && message.CaptureId == transfer.CaptureId && message.CaptureToken == transfer.CaptureToken
            && SenderFor(transfer.SteamId, message.LogSessionId) is not null;

    private bool Matches(LogStreamNetworkMessage message, IncomingTransfer transfer)
        => message.TransferId == transfer.TransferId && message.ConsentToken == transfer.ConsentToken
            && message.CaptureId == _captureId && message.CaptureToken == _captureToken
            && (message.LogSessionId == transfer.LogSessionId
                || (_deepTraceEnabled && IsDeepSession(message.LogSessionId)));

    private LogStreamSenderSession? SenderFor(string peerId, string sourceSessionId)
    {
        if (_sender?.SourceSessionId == sourceSessionId) return _sender;
        return _deepSenders.TryGetValue(peerId, out var deep) && deep.SourceSessionId == sourceSessionId
            ? deep
            : null;
    }

    private LogReceiverAdvertisement Advertisement() => new()
    {
        Available = _receiverAvailable,
        CaptureActive = _receiverAvailable && _captureMode == LogStreamingCaptureMode.Capturing,
        CapturePaused = _receiverAvailable && _captureMode == LogStreamingCaptureMode.Paused,
        DeepTraceEnabled = _receiverAvailable && _deepTraceEnabled,
        CaptureId = _receiverAvailable ? _captureId : "",
        CaptureToken = _receiverAvailable ? _captureToken : ""
    };

    private LogStreamingPeerSnapshot BuildPeer(string id, DateTimeOffset now)
    {
        var current = _lobby.Peers.FirstOrDefault(peer => peer.SteamId == id);
        _remembered.TryGetValue(id, out var remembered);
        string name = current?.DisplayName ?? remembered?.Name ?? "Steam user " + id;
        bool host = current?.IsHost ?? remembered?.IsHost == true;
        bool compatible = current is not null && Compatible(current);
        var incoming = BuildIncoming(id, current, compatible, now);
        var outgoing = BuildOutgoing(id, current, compatible, now);
        return new(id, name, host, compatible && current!.ReceiverAvailable,
            _outgoing.ContainsKey(id), current?.DeepTraceEnabled == true, incoming, outgoing);
    }

    private LogStreamingDirectionSnapshot BuildIncoming(string id, LogLobbyPeer? peer, bool compatible, DateTimeOffset now)
    {
        if (!_incoming.TryGetValue(id, out var transfer))
        {
            if (peer is not null && !compatible) return Direction(LogStreamingPeerMode.Unsupported,
                "This player needs a compatible Companion Game Hook.");
            return Direction(LogStreamingPeerMode.NotSharing,
            "This player has not approved sharing logs with you.");
        }
        if (peer is null || !HasFreshAdvertisement(peer) || now - transfer.LastSeen > TransferHeartbeatStale)
        {
            var state = now - transfer.LastSeen <= _options.ReconnectGrace ? LogStreamingPeerMode.Reconnecting : LogStreamingPeerMode.Disconnected;
            string heartbeatDetail = peer is not null && HasFreshAdvertisement(peer)
                ? state == LogStreamingPeerMode.Reconnecting
                    ? "Waiting for this player's Companion to confirm the sharing approval again."
                    : "This player's Companion stopped confirming the sharing approval."
                : state == LogStreamingPeerMode.Reconnecting
                    ? "The connection dropped. Companion will resume automatically in this lobby."
                    : "This player is no longer connected.";
            return Direction(state, heartbeatDetail, transfer);
        }
        if (!compatible) return Direction(LogStreamingPeerMode.Unsupported,
            "This player needs a compatible Companion Game Hook.", transfer);
        var capture = _capture?.GetSnapshot();
        if (transfer.StorageProblem.Length > 0 || capture?.IsBlocked == true || capture?.MetadataHealthy == false)
            return Direction(LogStreamingPeerMode.StorageLimited, transfer.StorageProblem.Length > 0
                ? transfer.StorageProblem : capture?.BlockedReason ?? capture?.MetadataError ?? "Capture storage is unavailable.", transfer);
        if (_captureMode == LogStreamingCaptureMode.Paused)
            return Direction(LogStreamingPeerMode.ReceiverPaused, "You paused this capture. Approval is preserved.", transfer);
        if (_captureMode != LogStreamingCaptureMode.Capturing)
            return Direction(LogStreamingPeerMode.Ready, "Approved and waiting for you to start capture.", transfer);
        return Direction(now - transfer.LastFlush.GetValueOrDefault(transfer.Created) < TimeSpan.FromSeconds(3)
            && transfer.AcknowledgedBytes > 0 ? LogStreamingPeerMode.Streaming : LogStreamingPeerMode.Ready,
            transfer.AcknowledgedBytes > 0 ? "Validated log data is being flushed to disk." : "Approved and waiting for log data.", transfer);
    }

    private LogStreamingDirectionSnapshot BuildOutgoing(string id, LogLobbyPeer? peer, bool compatible, DateTimeOffset now)
    {
        if (!_outgoing.TryGetValue(id, out var transfer))
        {
            if (peer is not null && !compatible) return Direction(LogStreamingPeerMode.Unsupported,
                "This player needs a compatible Companion Game Hook.");
            return Direction(LogStreamingPeerMode.NotSharing,
            peer?.ReceiverAvailable == true ? "Available to receive your logs." : "This player is not accepting logs.");
        }
        if (peer is null || !HasFreshAdvertisement(peer) || now - transfer.LastSeen > TransferHeartbeatStale)
        {
            var state = now - transfer.LastSeen <= _options.ReconnectGrace ? LogStreamingPeerMode.Reconnecting : LogStreamingPeerMode.Disconnected;
            string heartbeatDetail = peer is not null && HasFreshAdvertisement(peer)
                ? state == LogStreamingPeerMode.Reconnecting
                    ? "Waiting for this player's Companion to confirm the sharing approval again."
                    : "This player's Companion stopped confirming the sharing approval."
                : state == LogStreamingPeerMode.Reconnecting
                    ? "The connection dropped. Companion will resume automatically in this lobby."
                    : "This player is no longer connected.";
            return Direction(state, heartbeatDetail, transfer);
        }
        if (!compatible) return Direction(LogStreamingPeerMode.Unsupported,
            "This player needs a compatible Companion Game Hook.", transfer);
        if (transfer.Invalidated) return Direction(LogStreamingPeerMode.NotSharing,
            "The receiver started a different capture. Select them again to approve it.", transfer);
        if (_sender?.GetSnapshot().IsSpoolFull == true)
            return Direction(LogStreamingPeerMode.StorageLimited,
            "Your local log buffer reached its safety limit. Restart Rain World or join a different Meadow lobby to create a new stream.", transfer);
        if (transfer.StorageLimited) return Direction(LogStreamingPeerMode.StorageLimited,
            transfer.Error.Length > 0 ? transfer.Error : "The receiver reached a storage limit.", transfer);
        if (peer.CapturePaused) return Direction(LogStreamingPeerMode.ReceiverPaused,
            "The receiver paused capture. Approval is preserved.", transfer);
        if (!transfer.Accepted || !peer.CaptureActive) return Direction(LogStreamingPeerMode.Ready,
            transfer.Accepted ? "Approved and waiting for the receiver to start capture." : "Waiting for the receiver to accept this stream.", transfer);
        long backlog = SenderBacklog(id);
        string detail = transfer.DeepTraceRejected
            ? "Normal logs continue. Select this receiver again to restart Deep trace."
            : backlog > 0 ? "Sending the complete current logs and new entries." : "Caught up. New log entries will stream live.";
        return Direction(transfer.InFlight.Count > 0 || backlog > 0 ? LogStreamingPeerMode.Streaming : LogStreamingPeerMode.Ready,
            detail, transfer);
    }

    private static LogStreamingDirectionSnapshot Direction(LogStreamingPeerMode state, string detail)
        => new(state, detail, 0, 0, 0, null, 0, "");

    private LogStreamingDirectionSnapshot Direction(LogStreamingPeerMode state, string detail, IncomingTransfer transfer)
        => new(state, detail, transfer.LastRate, transfer.AcknowledgedBytes, 0, null,
            transfer.ReconnectCount, transfer.LogSessionId);

    private LogStreamingDirectionSnapshot Direction(LogStreamingPeerMode state, string detail, OutgoingTransfer transfer)
        => new(state, detail, transfer.LastRate, transfer.AcknowledgedBytes, SenderBacklog(transfer.SteamId),
            transfer.InFlight.Count == 0 ? null : Now - transfer.InFlight.Values.Min(item => item.FirstSentAt),
            transfer.ReconnectCount, _sender?.SourceSessionId ?? "");

    private long SenderBacklog(string receiverId)
    {
        long backlog = _sender?.GetSnapshot().Receivers
            .FirstOrDefault(receiver => receiver.ReceiverId == receiverId)?.BacklogBytes ?? 0;
        if (IsDeepTraceActive(receiverId) && _deepSenders.TryGetValue(receiverId, out var deep))
            backlog += deep.GetSnapshot().Receivers
                .FirstOrDefault(receiver => receiver.ReceiverId == receiverId)?.BacklogBytes ?? 0;
        return backlog;
    }

    private void AddViewer(LogLobbyPeer peer, LogStreamChunk chunk, DateTimeOffset now)
    {
        if (!_incoming.TryGetValue(peer.SteamId, out var transfer)) return;
        AddViewer(transfer.Identity, chunk, now, transfer.Viewers);
    }

    private void AddViewer(
        LogStreamPeerIdentity peer,
        LogStreamChunk chunk,
        DateTimeOffset now,
        IDictionary<string, ViewerStream> viewers)
    {
        var key = chunk.SourceSessionId + ":" + chunk.FileId + ":" + chunk.Generation;
        if (!viewers.TryGetValue(key, out var viewer))
        {
            if (viewers.Count >= MaximumViewerStreamsPerTransfer)
            {
                string oldest = viewers.MinBy(item => item.Value.LastUsed).Key;
                viewers.Remove(oldest);
            }
            viewer = new ViewerStream(++_viewerStreamSequence);
            viewers[key] = viewer;
        }
        else viewer.LastUsed = ++_viewerStreamSequence;
        foreach (string line in viewer.Append(chunk.CopyData()))
        {
            while (_viewerLines.Count > 0 && (_viewerLines.Count >= MaximumViewerLines
                   || _viewerCharacters + line.Length > MaximumViewerCharacters))
                _viewerCharacters -= _viewerLines.Dequeue().Text.Length;
            _viewerLines.Enqueue(new(++_viewerSequence, now,
                peer.SteamId.ToString(CultureInfo.InvariantCulture), peer.SteamName, chunk.FileId, line));
            _viewerCharacters += line.Length;
        }
    }

    private void RefillBudgets(DateTimeOffset now)
    {
        double seconds = Math.Max(0, (now - _lastBudget).TotalSeconds);
        _lastBudget = now;
        _sendBudget = Math.Min(_options.MaximumBytesPerSecond,
            _sendBudget + seconds * _options.MaximumBytesPerSecond);
        _incomingBudget = Math.Min(_options.MaximumBytesPerSecond,
            _incomingBudget + seconds * _options.MaximumBytesPerSecond);
        _incomingPacketBudget = Math.Min(MaximumIncomingPacketsPerSecond,
            _incomingPacketBudget + seconds * MaximumIncomingPacketsPerSecond);
        foreach (var budget in _incomingBudgets.Values)
        {
            budget.Available = Math.Min(_options.MaximumIncomingBytesPerSecondPerPeer,
                budget.Available + seconds * _options.MaximumIncomingBytesPerSecondPerPeer);
            budget.AvailablePackets = Math.Min(MaximumIncomingPacketsPerSecondPerPeer,
                budget.AvailablePackets + seconds * MaximumIncomingPacketsPerSecondPerPeer);
        }
    }

    private bool TryConsumeIncoming(string peerId, int byteCount)
    {
        if (!_incomingBudgets.TryGetValue(peerId, out var peerBudget))
        {
            peerBudget = new(_options.MaximumIncomingBytesPerSecondPerPeer, MaximumIncomingPacketsPerSecondPerPeer);
            _incomingBudgets.Add(peerId, peerBudget);
        }
        if (_incomingBudget < byteCount || peerBudget.Available < byteCount
            || _incomingPacketBudget < 1 || peerBudget.AvailablePackets < 1) return false;
        _incomingBudget -= byteCount;
        peerBudget.Available -= byteCount;
        _incomingPacketBudget--;
        peerBudget.AvailablePackets--;
        return true;
    }

    private void Sample(DateTimeOffset now)
    {
        double elapsed = (now - _lastSample).TotalSeconds;
        if (elapsed < 1) return;
        foreach (var transfer in _outgoing.Values)
        {
            transfer.LastRate = transfer.BytesSinceSample / elapsed;
            transfer.BytesSinceSample = 0;
        }
        foreach (var transfer in _incoming.Values)
        {
            transfer.LastRate = transfer.BytesSinceSample / elapsed;
            transfer.BytesSinceSample = 0;
        }
        long backlog = _outgoing.Keys.Sum(SenderBacklog);
        var ages = _outgoing.Values.Where(transfer => transfer.InFlight.Count > 0)
            .Select(transfer => now - transfer.InFlight.Values.Min(item => item.FirstSentAt)).ToArray();
        _samples.Enqueue(new(now, _sampleBytes / elapsed, backlog, ages.Length == 0 ? null : ages.Max()));
        while (_samples.Count > 1 && now - _samples.Peek().Time > TimeSpan.FromMinutes(1)) _samples.Dequeue();
        _sampleBytes = 0;
        _lastSample = now;
    }

    private void RequireSteamLobby()
    {
        if (!_lobby.IsConnected || !_lobby.IsSteam)
            throw new InvalidOperationException("Join a Steam Rain Meadow lobby first.");
    }

    private static bool Compatible(LogLobbyPeer peer) => peer.SupportsLogStreaming
        && peer.ProtocolVersion == ProtocolInfo.LogStreamingVersion;
    private static bool HasFreshAdvertisement(LogLobbyPeer peer) => peer.LastSeenUtcTicks > 0;
    private static bool IsDeepSession(string sourceSessionId)
        => sourceSessionId.StartsWith("deep-", StringComparison.Ordinal) && TokenValid(sourceSessionId, 128);
    private static bool IsDeepTraceFile(string fileId)
        => fileId is LogStreamTelemetryRecorder.DeepTraceFileId or LogStreamTelemetryRecorder.MeadowNativeDeepFileId;
    private static bool ValidNumericId(string? value) => value is { Length: > 0 and <= 32 } && value.All(char.IsDigit);
    private static bool TokenValid(string? value, int maximum) => value is { Length: > 0 } && value.Length <= maximum
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');
    private static string Token(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N");
    private static string Secret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static string SafeRemoteMessage(string? message) => string.IsNullOrWhiteSpace(message)
        ? "The remote capture rejected log data." : message.Trim()[..Math.Min(message.Trim().Length, 256)];
    private DateTimeOffset Now => _options.TimeProvider.GetUtcNow();

    private sealed record RememberedPeer(string Name, bool IsHost, DateTimeOffset LastSeen);
    private readonly record struct ChunkKey(string SessionId, long Sequence);
    private sealed class InFlight(int length, DateTimeOffset sentAt)
    {
        public int Length { get; } = length;
        public DateTimeOffset FirstSentAt { get; } = sentAt;
        public DateTimeOffset LastSentAt { get; set; } = sentAt;
    }
    private sealed record IncomingAuthorization(string SteamId, string TransferId, string ConsentToken)
    {
        public static IncomingAuthorization From(IncomingTransfer transfer)
            => new(transfer.SteamId, transfer.TransferId, transfer.ConsentToken);

        public static IncomingAuthorization From(string steamId, LogStreamNetworkMessage message)
            => new(steamId, message.TransferId, message.ConsentToken);
    }
    private sealed class IncomingBudget(double available, double availablePackets)
    {
        public double Available { get; set; } = available;
        public double AvailablePackets { get; set; } = availablePackets;
    }
    private sealed class IncomingOpenRate
    {
        public Queue<DateTimeOffset> Accepted { get; } = new();
        public DateTimeOffset LastNotice { get; set; } = DateTimeOffset.MinValue;
    }

    private static LogStreamPeerIdentity PeerIdentity(LogLobbyPeer peer)
    {
        ulong.TryParse(peer.SteamId, NumberStyles.None, CultureInfo.InvariantCulture, out ulong steamId);
        return new(steamId, peer.DisplayName, peer.IsHost);
    }

    private sealed class OutgoingTransfer
    {
        public OutgoingTransfer(LogLobbyPeer peer, string transferId, string consentToken, DateTimeOffset now)
        {
            SteamId = peer.SteamId;
            CaptureId = peer.CaptureId;
            CaptureToken = peer.CaptureToken;
            TransferId = transferId;
            ConsentToken = consentToken;
            LastSeen = now;
            LastOpenSent = DateTimeOffset.MinValue;
        }
        public string SteamId { get; }
        public string CaptureId { get; }
        public string CaptureToken { get; }
        public string TransferId { get; }
        public string ConsentToken { get; }
        public DateTimeOffset LastSeen { get; set; }
        public DateTimeOffset LastOpenSent { get; set; }
        public bool Accepted { get; set; }
        public bool Invalidated { get; set; }
        public bool StorageLimited { get; set; }
        public bool DeepTraceRejected { get; set; }
        public string Error { get; set; } = "";
        public int ReconnectCount { get; set; }
        public long AcknowledgedBytes { get; set; }
        public long BytesSinceSample { get; set; }
        public double LastRate { get; set; }
        public bool PreferDeepTrace { get; set; }
        public Dictionary<string, long> AcknowledgedBySession { get; } = new(StringComparer.Ordinal);
        public Dictionary<ChunkKey, InFlight> InFlight { get; } = [];
    }

    private sealed class IncomingTransfer
    {
        public IncomingTransfer(LogLobbyPeer peer, string transferId, string consentToken, string logSessionId, DateTimeOffset now)
        {
            SteamId = peer.SteamId;
            TransferId = transferId;
            ConsentToken = consentToken;
            LogSessionId = logSessionId;
            Identity = PeerIdentity(peer);
            Created = LastSeen = now;
        }
        public string SteamId { get; }
        public string TransferId { get; }
        public string ConsentToken { get; }
        public string LogSessionId { get; }
        public LogStreamPeerIdentity Identity { get; set; }
        public DateTimeOffset Created { get; }
        public DateTimeOffset LastSeen { get; set; }
        public DateTimeOffset? LastFlush { get; set; }
        public int ReconnectCount { get; set; }
        public long AcknowledgedBytes { get; set; }
        public long BytesSinceSample { get; set; }
        public double LastRate { get; set; }
        public string StorageProblem { get; set; } = "";
        public HashSet<string> DeepSessions { get; } = new(StringComparer.Ordinal);
        public HashSet<string> OpenDeepSessions { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, long> LastAcknowledgedSequences { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ViewerStream> Viewers { get; } = new(StringComparer.Ordinal);
    }

    private sealed class ViewerStream(long lastUsed)
    {
        private readonly Decoder _decoder = new UTF8Encoding(false, false).GetDecoder();
        private readonly StringBuilder _pending = new();
        private bool _discardUntilNewline;
        public long LastUsed { get; set; } = lastUsed;
        public IReadOnlyList<string> Append(byte[] bytes)
        {
            int count = _decoder.GetCharCount(bytes, 0, bytes.Length, false);
            var chars = new char[count];
            _decoder.GetChars(bytes, 0, bytes.Length, chars, 0, false);
            var lines = new List<string>();
            foreach (char character in chars)
            {
                if (character == '\n')
                {
                    if (!_discardUntilNewline) lines.Add(_pending.ToString().TrimEnd('\r'));
                    _pending.Clear();
                    _discardUntilNewline = false;
                    continue;
                }
                if (_discardUntilNewline) continue;
                if (_pending.Length < MaximumViewerLineCharacters - 1)
                {
                    _pending.Append(character);
                    continue;
                }
                _pending.Append('…');
                lines.Add(_pending.ToString().TrimEnd('\r'));
                _pending.Clear();
                _discardUntilNewline = true;
            }
            return lines;
        }
    }
}
