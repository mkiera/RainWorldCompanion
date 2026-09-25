using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using RainWorldCompanion.LiveProtocol;

namespace RWCompanion.Mod;

internal sealed class MeadowLogPeer
{
    internal string SteamId { get; set; } = "";
    internal ushort LobbyPeerId { get; set; }
    internal string DisplayName { get; set; } = "";
    internal bool IsLocal { get; set; }
    internal bool IsHost { get; set; }
    internal bool SupportsLogStreaming { get; set; }
    internal int ProtocolVersion { get; set; }
    internal bool Available { get; set; }
    internal bool CaptureActive { get; set; }
    internal bool CapturePaused { get; set; }
    internal bool DeepTraceEnabled { get; set; }
    internal string CaptureId { get; set; } = "";
    internal string CaptureToken { get; set; } = "";
    internal DateTime LastSeenUtc { get; set; }
}

internal sealed class MeadowLogPacket
{
    internal string SenderSteamId { get; set; } = "";
    internal byte[] Payload { get; set; } = Array.Empty<byte>();
}

internal sealed class MeadowLogTransport : IDisposable
{
    internal const int MaximumPacketBytes = ProtocolInfo.MaximumLogPacketLength;
    private const string SubscriptionKey = "rwc-log-v1";
    private const int MaximumInboxPackets = 128;
    private const int MaximumInboxBytes = 2 * 1024 * 1024;
    private const int MaximumCallbacksPerUpdate = 32;
    private const int MaximumLobbyIdBytes = 64;
    private const int MaximumCaptureIdBytes = 96;
    private const int MaximumCaptureTokenBytes = 192;
    private const int AdvertisementIntervalMilliseconds = 2000;
    private const int AdvertisementFreshMilliseconds = 7000;
    private const int ConnectionSampleIntervalMilliseconds = 1000;
    private const int Magic = 0x314C4352;
    private const byte WireVersion = 1;
    private const byte AdvertisementKind = 1;
    private const byte PayloadKind = 2;

    private readonly ConcurrentQueue<MeadowLogPacket> _inbox = new();
    private readonly ConcurrentQueue<CallbackPacket> _callbacks = new();
    private readonly Dictionary<string, RemoteAdvertisement> _advertisements = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object> _participants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LiveMeadowConnection?> _connectionSamples = new(StringComparer.Ordinal);
    private readonly object _inboxSync = new();
    private readonly object _callbackSync = new();
    private object? _subscriber;
    private object? _lobby;
    private MeadowLogPeer[] _peers = Array.Empty<MeadowLogPeer>();
    private int _queuedBytes;
    private int _queuedPackets;
    private int _queuedCallbackBytes;
    private int _queuedCallbacks;
    private int _droppedInboundPackets;
    private long _nextAdvertisementTicks;
    private long _nextConnectionSampleTicks;
    private bool _registered;
    private bool _available;
    private bool _captureActive;
    private bool _capturePaused;
    private bool _deepTraceEnabled;
    private string[] _deepTracePeerIds = Array.Empty<string>();
    private int _protocolVersion = ProtocolInfo.LogStreamingVersion;
    private string _captureId = "";
    private string _captureToken = "";

    internal bool IsSteamLobby { get; private set; }
    internal string CurrentLobbyId { get; private set; } = "";
    internal int MaximumPayloadBytes => Math.Max(0, MaximumPacketBytes - 8 - Encoding.UTF8.GetByteCount(CurrentLobbyId));
    internal IReadOnlyList<MeadowLogPeer> Peers => _peers;
    internal int DroppedInboundPackets => System.Threading.Volatile.Read(ref _droppedInboundPackets);
    internal string AvailabilityError { get; private set; } = "";

    internal LiveMeadowSnapshot? CaptureTelemetry(IReadOnlyList<LivePlayer> players)
    {
        if (!IsSteamLobby || _lobby == null || CurrentLobbyId.Length == 0) return null;
        RefreshConnectionSamples();
        var settings = SafeGet(_lobby, "clientSettings") as IDictionary;
        int framesPerSecond = PositiveInteger(SafeGet(SafeGet(
            GameAccess.FindType("RainMeadow.OnlineManager"), "instance"), "framesPerSecond")) ?? 20;
        bool rosterTruncated = _peers.Length > ProtocolInfo.MaximumMeadowPeers;
        var peers = new List<LiveMeadowPeer>(Math.Min(_peers.Length, ProtocolInfo.MaximumMeadowPeers));
        foreach (var peer in _peers.OrderBy(peer => peer.LobbyPeerId).Take(ProtocolInfo.MaximumMeadowPeers))
        {
            string[] avatarIds = TextArray(players
                .Where(player => string.Equals(player.MeadowSteamId, peer.SteamId, StringComparison.Ordinal))
                .Select(player => player.MeadowAvatarId), ProtocolInfo.MaximumMeadowAvatarsPerPeer,
                ProtocolInfo.MaximumMeadowAvatarIdLength, out bool avatarsTruncated);
            rosterTruncated |= avatarsTruncated;
            var sample = new LiveMeadowPeer
            {
                SteamId = peer.SteamId,
                LobbyPeerId = peer.LobbyPeerId,
                DisplayName = peer.DisplayName,
                IsLocal = peer.IsLocal,
                IsHost = peer.IsHost,
                SupportsGameHookPackets = peer.SupportsLogStreaming,
                AvatarIds = avatarIds,
                Connection = _connectionSamples.TryGetValue(peer.SteamId, out var connection) ? connection : null,
            };
            if (_participants.TryGetValue(peer.SteamId, out var participant))
            {
                try
                {
                    object? client = null;
                    try { client = settings?.Contains(participant) == true ? settings[participant] : null; }
                    catch (Exception) { }
                    sample.InGame = Boolean(client, "inGame");
                    sample.EnteringChat = Boolean(client, "isInteracting");
                    int? avatarCount = client == null ? null : Count(SafeGet(client, "avatars"));
                    if (avatarCount > ProtocolInfo.MaximumMeadowAvatarsPerPeer)
                    {
                        avatarCount = ProtocolInfo.MaximumMeadowAvatarsPerPeer;
                        rosterTruncated = true;
                    }
                    sample.AvatarCount = avatarCount;
                    object? story = sample.InGame == true && sample.AvatarCount is > 0
                        ? ReadStoryClientData(client)
                        : null;
                    sample.StoryReadyForWin = Boolean(story, "readyForWin");
                    sample.StoryReadyForTransition = Boolean(story, "readyForTransition");
                    sample.StoryDead = Boolean(story, "isDead");
                    sample.IsSpectating = Boolean(participant, "isActuallySpectating");
                    sample.NeedsAcknowledgement = Boolean(participant, "needsAck");
                    sample.PingMilliseconds = peer.IsLocal ? null : NonNegativeInteger(SafeGet(participant, "ping"));
                    sample.IncomingBytesPerSecond = BytesPerSecond(SafeGet(participant, "bytesIn"), framesPerSecond);
                    sample.OutgoingBytesPerSecond = BytesPerSecond(SafeGet(participant, "bytesOut"), framesPerSecond);
                    sample.RemoteTick = UnsignedInteger(SafeGet(participant, "tick"));
                    sample.LatestAcknowledgedTick = UnsignedInteger(SafeGet(participant, "latestTickAck"));
                    sample.OutgoingEventCount = Count(SafeGet(participant, "OutgoingEvents"));
                    sample.OutgoingStateCount = Count(SafeGet(participant, "OutgoingStates"));
                    sample.EventsRead = Boolean(participant, "eventsRead");
                    sample.StatesRead = Boolean(participant, "statesRead");
                    sample.EventsWritten = Boolean(participant, "eventsWritten");
                    sample.StatesWritten = Boolean(participant, "statesWritten");
                }
                catch (Exception) { }
            }
            peers.Add(sample);
        }

        var local = peers.FirstOrDefault(peer => peer.IsLocal);
        string[] requiredMods = TextArray(SafeGet(_lobby, "requiredmods"), ProtocolInfo.MaximumMeadowModIds,
            ProtocolInfo.MaximumMeadowModIdLength, out bool requiredModsTruncated);
        string[] bannedMods = TextArray(SafeGet(_lobby, "bannedmods"), ProtocolInfo.MaximumMeadowModIds,
            ProtocolInfo.MaximumMeadowModIdLength, out bool bannedModsTruncated);
        LiveMeadowLobbyOption[] lobbyOptions = ReadLobbyOptions(_lobby, out bool optionsTruncated);
        return new()
        {
            LobbyId = CurrentLobbyId,
            ObserverSteamId = local?.SteamId ?? "",
            GameMode = EnumText(SafeGet(_lobby, "gameModeType")),
            Timeline = CleanText(Convert.ToString(SafeGet(_lobby, "ActiveTimeline")), ProtocolInfo.MaximumMeadowLabelLength),
            RequiredMods = requiredMods,
            BannedMods = bannedMods,
            WhitelistMode = Boolean(_lobby, "whitelistmode"),
            CheatsEnabled = Boolean(_lobby, "cheats"),
            ConfigurationTruncated = requiredModsTruncated || bannedModsTruncated || optionsTruncated,
            RosterTruncated = rosterTruncated,
            LobbyOptions = lobbyOptions,
            Peers = peers.ToArray()
        };
    }

    internal void Update()
    {
        object? lobby = MeadowPlayers.Lobby;
        bool steam = IsSteamDomain();
        if (!steam)
        {
            Unregister();
            ResetLobby();
            AvailabilityError = lobby == null ? "" : "Log streaming is available in Steam lobbies only.";
            return;
        }

        if (!TryRegister())
        {
            ResetLobby();
            return;
        }

        string lobbyId = lobby == null ? "" : ReadLobbyId();
        if (lobby == null || string.IsNullOrWhiteSpace(lobbyId))
        {
            ResetLobby();
            AvailabilityError = "";
            return;
        }

        if (!ReferenceEquals(_lobby, lobby) || !string.Equals(CurrentLobbyId, lobbyId, StringComparison.Ordinal))
        {
            ClearLocalAdvertisement();
            ClearInbox();
            _advertisements.Clear();
            _participants.Clear();
            _lobby = lobby;
            CurrentLobbyId = lobbyId;
            _nextAdvertisementTicks = 0;
        }

        IsSteamLobby = true;
        AvailabilityError = "";
        RefreshParticipants(lobby);
        DrainCallbacks();
        RemoveDepartedAdvertisements();
        RefreshPeerSnapshot(lobby);

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now < _nextAdvertisementTicks) return;
        _nextAdvertisementTicks = now + MillisecondsToTicks(AdvertisementIntervalMilliseconds);
        foreach (var peer in _peers)
        {
            if (peer.IsLocal || !peer.SupportsLogStreaming) continue;
            if (_participants.TryGetValue(peer.SteamId, out var participant)) SendPacket(participant, EncodeAdvertisement(peer.SteamId));
        }
    }

    internal void SetLocalAdvertisement(bool available, bool captureActive, bool capturePaused, bool deepTraceEnabled,
        string captureId, string captureToken, int protocolVersion, string[]? deepTracePeerIds = null)
    {
        deepTracePeerIds ??= Array.Empty<string>();
        if (!LogAdvertisement.ValidDeepTracePeerIds(deepTracePeerIds))
            throw new ArgumentException("Deep trace targets must be at most 32 numeric Steam IDs.", nameof(deepTracePeerIds));
        if ((captureActive || capturePaused) && !available)
            throw new ArgumentException("An active or paused capture must also be available.", nameof(available));
        if ((deepTraceEnabled || deepTracePeerIds.Length > 0) && !available)
            throw new ArgumentException("Deep trace requires an available receiver.", nameof(deepTraceEnabled));
        if (captureActive && capturePaused) throw new ArgumentException("A capture cannot be active and paused at the same time.");
        if (protocolVersion < 1 || protocolVersion > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(protocolVersion));
        ValidateText(captureId, MaximumCaptureIdBytes, nameof(captureId));
        ValidateText(captureToken, MaximumCaptureTokenBytes, nameof(captureToken));
        if ((captureActive || capturePaused) && (captureId.Length == 0 || captureToken.Length == 0))
            throw new ArgumentException("An active or paused capture needs an ID and token.");

        bool changed = _available != available || _captureActive != captureActive || _capturePaused != capturePaused
            || _deepTraceEnabled != deepTraceEnabled
            || !_deepTracePeerIds.SequenceEqual(deepTracePeerIds, StringComparer.Ordinal)
            || _protocolVersion != protocolVersion
            || !string.Equals(_captureId, captureId, StringComparison.Ordinal)
            || !string.Equals(_captureToken, captureToken, StringComparison.Ordinal);
        _available = available;
        _captureActive = captureActive;
        _capturePaused = capturePaused;
        _deepTraceEnabled = deepTraceEnabled;
        if (changed) _deepTracePeerIds = deepTracePeerIds.ToArray();
        _captureId = captureId;
        _captureToken = captureToken;
        _protocolVersion = protocolVersion;
        if (changed) _nextAdvertisementTicks = 0;
    }

    internal bool TrySend(string steamId, byte[] payload)
    {
        if (steamId == null) throw new ArgumentNullException(nameof(steamId));
        if (payload == null) throw new ArgumentNullException(nameof(payload));
        if (!IsSteamLobby || _lobby == null || !_participants.TryGetValue(steamId, out var participant)) return false;
        if (!IsParticipant(participant) || !PeerSupportsPackets(_lobby, participant)) return false;
        byte[] packet;
        try { packet = EncodePayload(payload); }
        catch (ArgumentException) { return false; }
        return SendPacket(participant, packet);
    }

    internal bool TryReceive(out MeadowLogPacket? packet)
    {
        lock (_inboxSync)
        {
            if (!_inbox.TryDequeue(out packet)) return false;
            _queuedPackets--;
            _queuedBytes -= packet.Payload.Length;
            return true;
        }
    }

    private bool TryRegister()
    {
        if (_registered) return true;
        try
        {
            Type? manager = GameAccess.FindType("RainMeadow.CustomManager");
            Type? subscriberType = GameAccess.FindType("RainMeadow.IUseCustomPackets");
            if (manager == null || subscriberType == null)
            {
                AvailabilityError = "Rain Meadow does not support custom packets.";
                return false;
            }

            MeadowCustomSubscriber.SetReceiver(ReceivePacket);
            var subscribed = GameAccess.Items(GameAccess.Call(manager, "GetSubscribed"))
                .Any(value => string.Equals(Convert.ToString(value), SubscriptionKey, StringComparison.Ordinal));
            if (subscribed)
            {
                _registered = true;
                AvailabilityError = "";
                return true;
            }
            _subscriber ??= MeadowCustomSubscriber.Create(subscriberType, ReceivePacket);
            GameAccess.Call(manager, "Subscribe", SubscriptionKey, _subscriber);
            _registered = true;
            AvailabilityError = "";
            return true;
        }
        catch (Exception error) when (error is MissingMemberException or MissingMethodException or TargetInvocationException
            or TypeLoadException or InvalidOperationException or ArgumentException)
        {
            AvailabilityError = "Rain Meadow log transport could not start: " + error.GetBaseException().Message;
            return false;
        }
    }

    private void Unregister()
    {
        if (!_registered)
        {
            MeadowCustomSubscriber.SetReceiver(null);
            return;
        }
        try
        {
            if (GameAccess.FindType("RainMeadow.CustomManager") is { } manager)
                GameAccess.Call(manager, "Unsubscribe", SubscriptionKey);
        }
        catch (Exception error) when (error is MissingMemberException or MissingMethodException or TargetInvocationException
            or InvalidOperationException or ArgumentException) { }
        _registered = false;
        MeadowCustomSubscriber.SetReceiver(null);
    }

    private void RefreshParticipants(object lobby)
    {
        var previous = new HashSet<string>(_participants.Keys, StringComparer.Ordinal);
        _participants.Clear();
        foreach (var participant in GameAccess.Items(GameAccess.Get(lobby, "participants")))
        {
            if (GameAccess.Get(participant, "hasLeft") is true || !TryReadSteamId(participant, out string steamId)) continue;
            _participants[steamId] = participant;
            if (!previous.Contains(steamId) && GameAccess.Get(participant, "isMe") is false) _nextAdvertisementTicks = 0;
        }
    }

    private void RefreshPeerSnapshot(object lobby)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        object? owner = GameAccess.Get(lobby, "owner");
        var peers = new List<MeadowLogPeer>(_participants.Count);
        foreach (var pair in _participants)
        {
            object participant = pair.Value;
            bool local = GameAccess.Get(participant, "isMe") is true;
            bool supports = local ? _registered : PeerSupportsPackets(lobby, participant);
            RemoteAdvertisement? remote = null;
            bool fresh = !local && _advertisements.TryGetValue(pair.Key, out remote)
                && now - remote.SeenAtTicks < MillisecondsToTicks(AdvertisementFreshMilliseconds);
            peers.Add(new MeadowLogPeer
            {
                SteamId = pair.Key,
                LobbyPeerId = GameAccess.Get(participant, "inLobbyId") is ushort peerId ? peerId : (ushort)0,
                DisplayName = ReadDisplayName(participant),
                IsLocal = local,
                IsHost = ReferenceEquals(participant, owner),
                SupportsLogStreaming = supports,
                ProtocolVersion = local ? _protocolVersion : fresh ? remote!.ProtocolVersion : 0,
                Available = local ? _available : fresh && remote!.Available,
                CaptureActive = local ? _captureActive : fresh && remote!.CaptureActive,
                CapturePaused = local ? _capturePaused : fresh && remote!.CapturePaused,
                DeepTraceEnabled = local ? _deepTraceEnabled : fresh && remote!.DeepTraceEnabled,
                CaptureId = local ? _captureId : fresh ? remote!.CaptureId : "",
                CaptureToken = local ? _captureToken : fresh ? remote!.CaptureToken : "",
                LastSeenUtc = local ? DateTime.UtcNow : fresh ? remote!.SeenUtc : DateTime.MinValue
            });
        }
        _peers = peers.OrderBy(peer => peer.LobbyPeerId).ToArray();
    }

    private void ReceivePacket(object sender, object packet)
    {
        try
        {
            if (GameAccess.Get(packet, "data") is not byte[] data || GameAccess.Get(packet, "dataSize") is not ushort size
                || size > MaximumPacketBytes || size > data.Length)
            {
                return;
            }
            var copy = new byte[size];
            Buffer.BlockCopy(data, 0, copy, 0, size);
            lock (_callbackSync)
            {
                if (_queuedCallbacks >= MaximumInboxPackets || _queuedCallbackBytes + size > MaximumInboxBytes)
                {
                    System.Threading.Interlocked.Increment(ref _droppedInboundPackets);
                    return;
                }
                _queuedCallbacks++;
                _queuedCallbackBytes += size;
                _callbacks.Enqueue(new(sender, copy));
            }
        }
        catch (Exception)
        {
            System.Threading.Interlocked.Increment(ref _droppedInboundPackets);
        }
    }

    private void DrainCallbacks()
    {
        for (int processed = 0; processed < MaximumCallbacksPerUpdate && TryTakeCallback(out var callback); processed++)
            ProcessPacket(callback.Sender, callback.Data);
    }

    private bool TryTakeCallback(out CallbackPacket callback)
    {
        lock (_callbackSync)
        {
            if (!_callbacks.TryDequeue(out callback!)) return false;
            _queuedCallbacks--;
            _queuedCallbackBytes -= callback.Data.Length;
            return true;
        }
    }

    private void ProcessPacket(object sender, byte[] data)
    {
        try
        {
            if (!IsSteamLobby || _lobby == null || !IsParticipant(sender) || !TryReadSteamId(sender, out string steamId)) return;
            if (!_participants.TryGetValue(steamId, out var current) || !ReferenceEquals(current, sender)) return;
            using var stream = new MemoryStream(data, false);
            using var reader = new BinaryReader(stream, Encoding.UTF8, true);
            if (reader.ReadInt32() != Magic || reader.ReadByte() != WireVersion) return;
            byte kind = reader.ReadByte();
            string lobbyId = ReadText(reader, MaximumLobbyIdBytes);
            if (!string.Equals(lobbyId, CurrentLobbyId, StringComparison.Ordinal)) return;
            if (kind == AdvertisementKind)
            {
                ReceiveAdvertisement(steamId, reader);
                return;
            }
            if (kind != PayloadKind) return;
            int remaining = checked((int)(stream.Length - stream.Position));
            byte[] payload = reader.ReadBytes(remaining);
            if (payload.Length != remaining) return;
            Enqueue(new MeadowLogPacket { SenderSteamId = steamId, Payload = payload });
        }
        catch (Exception)
        {
            System.Threading.Interlocked.Increment(ref _droppedInboundPackets);
        }
    }

    private void ReceiveAdvertisement(string steamId, BinaryReader reader)
    {
        int protocolVersion = reader.ReadUInt16();
        byte flags = reader.ReadByte();
        string captureId = ReadText(reader, MaximumCaptureIdBytes);
        string captureToken = ReadText(reader, MaximumCaptureTokenBytes);
        if (reader.BaseStream.Position != reader.BaseStream.Length || protocolVersion < 1 || (flags & ~15) != 0) return;
        bool available = (flags & 1) != 0;
        bool captureActive = (flags & 2) != 0;
        bool capturePaused = (flags & 4) != 0;
        bool deepTraceEnabled = (flags & 8) != 0;
        if (captureActive && capturePaused) return;
        if (deepTraceEnabled && !available) return;
        if ((captureActive || capturePaused) && (!available || captureId.Length == 0 || captureToken.Length == 0)) return;
        _advertisements[steamId] = new RemoteAdvertisement
        {
            ProtocolVersion = protocolVersion,
            Available = available,
            CaptureActive = captureActive,
            CapturePaused = capturePaused,
            DeepTraceEnabled = deepTraceEnabled,
            CaptureId = captureId,
            CaptureToken = captureToken,
            SeenAtTicks = System.Diagnostics.Stopwatch.GetTimestamp(),
            SeenUtc = DateTime.UtcNow
        };
    }

    private void Enqueue(MeadowLogPacket packet)
    {
        lock (_inboxSync)
        {
            if (_queuedPackets >= MaximumInboxPackets || _queuedBytes + packet.Payload.Length > MaximumInboxBytes)
            {
                System.Threading.Interlocked.Increment(ref _droppedInboundPackets);
                return;
            }
            _queuedPackets++;
            _queuedBytes += packet.Payload.Length;
            _inbox.Enqueue(packet);
        }
    }

    private byte[] EncodeAdvertisement(string destinationSteamId)
    {
        using var stream = CreateEnvelope(AdvertisementKind);
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write((ushort)_protocolVersion);
            writer.Write(LogAdvertisement.Flags(_available, _captureActive, _capturePaused, _deepTraceEnabled,
                _deepTracePeerIds, destinationSteamId));
            WriteText(writer, _captureId, MaximumCaptureIdBytes);
            WriteText(writer, _captureToken, MaximumCaptureTokenBytes);
        }
        return FinishPacket(stream);
    }

    private byte[] EncodePayload(byte[] payload)
    {
        using var stream = CreateEnvelope(PayloadKind);
        if (payload.Length > MaximumPacketBytes - stream.Length)
            throw new ArgumentException("Log transport payload exceeds the 32 KiB packet limit.", nameof(payload));
        stream.Write(payload, 0, payload.Length);
        return FinishPacket(stream);
    }

    private MemoryStream CreateEnvelope(byte kind)
    {
        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(Magic);
            writer.Write(WireVersion);
            writer.Write(kind);
            WriteText(writer, CurrentLobbyId, MaximumLobbyIdBytes);
        }
        return stream;
    }

    private static byte[] FinishPacket(MemoryStream stream)
    {
        if (stream.Length > MaximumPacketBytes) throw new IOException("Log transport packet exceeds the 32 KiB packet limit.");
        return stream.ToArray();
    }

    private static void WriteText(BinaryWriter writer, string value, int maximumBytes)
    {
        byte[] bytes = new UTF8Encoding(false, true).GetBytes(value);
        if (bytes.Length > maximumBytes) throw new ArgumentException("Log transport text field exceeds its size limit.");
        writer.Write((ushort)bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadText(BinaryReader reader, int maximumBytes)
    {
        int length = reader.ReadUInt16();
        if (length > maximumBytes || length > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new IOException("Log transport text field exceeds its size limit.");
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();
        return new UTF8Encoding(false, true).GetString(bytes);
    }

    private static void ValidateText(string value, int maximumBytes, string parameterName)
    {
        if (value == null) throw new ArgumentNullException(parameterName);
        if (new UTF8Encoding(false, true).GetByteCount(value) > maximumBytes)
            throw new ArgumentException("Value exceeds the log transport size limit.", parameterName);
    }

    private bool SendPacket(object participant, byte[] packet)
    {
        if (!IsSteamLobby || _lobby == null || packet.Length > MaximumPacketBytes || !IsParticipant(participant)) return false;
        try
        {
            Type? manager = GameAccess.FindType("RainMeadow.CustomManager");
            Type? sendType = GameAccess.FindType("RainMeadow.NetIO+SendType");
            if (manager == null || sendType == null) return false;
            GameAccess.Call(manager, "SendCustomData", participant, SubscriptionKey, packet, (ushort)packet.Length,
                Enum.Parse(sendType, "Reliable"));
            return true;
        }
        catch (Exception error) when (error is MissingMemberException or MissingMethodException or TargetInvocationException
            or ArgumentException or InvalidOperationException)
        {
            AvailabilityError = "Rain Meadow rejected a log packet: " + error.GetBaseException().Message;
            return false;
        }
    }

    private bool IsParticipant(object participant) => _lobby != null && GameAccess.Get(participant, "hasLeft") is false
        && GameAccess.Items(GameAccess.Get(_lobby, "participants")).Any(peer => ReferenceEquals(peer, participant));

    private static bool IsSteamDomain()
    {
        object? domain = GameAccess.Get(GameAccess.FindType("RainMeadow.MatchmakingManager"), "currentDomain");
        return string.Equals(Convert.ToString(domain), "Steam", StringComparison.OrdinalIgnoreCase)
            || string.Equals(GameAccess.Text(domain, "value"), "Steam", StringComparison.OrdinalIgnoreCase);
    }

    private static long MillisecondsToTicks(int milliseconds) => checked(System.Diagnostics.Stopwatch.Frequency * milliseconds / 1000);

    private static string ReadLobbyId()
    {
        object? manager = GameAccess.Get(GameAccess.FindType("RainMeadow.MatchmakingManager"), "currentInstance");
        return manager == null ? "" : Convert.ToString(GameAccess.Call(manager, "GetLobbyID")) ?? "";
    }

    private static bool TryReadSteamId(object participant, out string steamId)
    {
        steamId = "";
        object? id = GameAccess.Get(participant, "id");
        if (id?.GetType().FullName?.EndsWith("SteamMatchmakingManager+SteamPlayerId", StringComparison.Ordinal) != true) return false;
        steamId = Convert.ToString(GameAccess.Call(participant, "GetUniqueID")) ?? "";
        return steamId.Length is > 0 and <= 32 && steamId.All(char.IsDigit);
    }

    private static string ReadDisplayName(object participant)
    {
        object? id = GameAccess.Get(participant, "id");
        string name = Convert.ToString(GameAccess.Get(id, "DisplayName")) ?? "";
        if (string.IsNullOrWhiteSpace(name)) name = "Steam user " + GameAccess.Text(participant, "inLobbyId");
        return CleanText(name, ProtocolInfo.MaximumMeadowDisplayNameLength);
    }

    private static bool PeerSupportsPackets(object lobby, object participant)
    {
        if (GameAccess.Get(lobby, "clientSettings") is not IDictionary settings || !settings.Contains(participant)) return false;
        object? clientSettings = settings[participant];
        Type? customType = GameAccess.FindType("RainMeadow.CustomClientSettings");
        if (clientSettings == null || customType == null) return false;
        object?[] arguments = { customType, null };
        try
        {
            if (GameAccess.Call(clientSettings, "TryGetData", arguments) is not true || arguments[1] == null) return false;
            return GameAccess.Items(GameAccess.Get(arguments[1], "keys"))
                .Any(value => string.Equals(Convert.ToString(value), SubscriptionKey, StringComparison.Ordinal));
        }
        catch (Exception error) when (error is MissingMemberException or MissingMethodException or TargetInvocationException
            or ArgumentException or InvalidOperationException) { return false; }
    }

    private void RefreshConnectionSamples()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now < _nextConnectionSampleTicks) return;
        _nextConnectionSampleTicks = now + MillisecondsToTicks(ConnectionSampleIntervalMilliseconds);
        foreach (string removed in _connectionSamples.Keys.Where(id => !_participants.ContainsKey(id)).ToArray())
            _connectionSamples.Remove(removed);
        foreach (var pair in _participants)
            _connectionSamples[pair.Key] = SafeGet(pair.Value, "isMe") is true ? null : ReadSteamConnection(pair.Value);
    }

    private static LiveMeadowConnection? ReadSteamConnection(object participant)
    {
        try
        {
            Type? messages = GameAccess.FindType("Steamworks.SteamNetworkingMessages");
            object? identity = SafeGet(SafeGet(participant, "id"), "oid");
            MethodInfo? method = messages?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(candidate => candidate.Name == "GetSessionConnectionInfo"
                    && candidate.GetParameters().Length == 3);
            if (method == null || identity == null) return null;
            ParameterInfo[] parameters = method.GetParameters();
            Type? infoType = parameters[1].ParameterType.GetElementType();
            Type? statusType = parameters[2].ParameterType.GetElementType();
            if (infoType == null || statusType == null) return null;
            object?[] arguments = { identity, Activator.CreateInstance(infoType), Activator.CreateInstance(statusType) };
            object? state = method.Invoke(null, arguments);
            object? status = arguments[2];
            if (status == null) return null;
            return new()
            {
                State = EnumText(state, ProtocolInfo.MaximumMeadowConnectionStateLength),
                PingMilliseconds = NonNegativeInteger(SafeGet(status, "m_nPing")),
                LocalDeliveryQuality = Quality(SafeGet(status, "m_flConnectionQualityLocal")),
                RemoteDeliveryQuality = Quality(SafeGet(status, "m_flConnectionQualityRemote")),
                IncomingPacketsPerSecond = Rate(SafeGet(status, "m_flInPacketsPerSec")),
                OutgoingPacketsPerSecond = Rate(SafeGet(status, "m_flOutPacketsPerSec")),
                IncomingBytesPerSecond = Rate(SafeGet(status, "m_flInBytesPerSec")),
                OutgoingBytesPerSecond = Rate(SafeGet(status, "m_flOutBytesPerSec")),
                EstimatedSendRateBytesPerSecond = NonNegativeInteger(SafeGet(status, "m_nSendRateBytesPerSecond")),
                PendingUnreliableBytes = NonNegativeInteger(SafeGet(status, "m_cbPendingUnreliable")),
                PendingReliableBytes = NonNegativeInteger(SafeGet(status, "m_cbPendingReliable")),
                UnacknowledgedReliableBytes = NonNegativeInteger(SafeGet(status, "m_cbSentUnackedReliable")),
                QueueTimeMicroseconds = NonNegativeInteger(SafeGet(status, "m_usecQueueTime"))
            };
        }
        catch (Exception) { return null; }
    }

    private static object? ReadStoryClientData(object? client)
    {
        Type? type = GameAccess.FindType("RainMeadow.StoryClientSettingsData");
        if (client == null || type == null) return null;
        object?[] arguments = { type, null };
        try { return GameAccess.Call(client, "TryGetData", arguments) is true ? arguments[1] : null; }
        catch (Exception error) when (error is MissingMemberException or MissingMethodException or TargetInvocationException
            or ArgumentException or InvalidOperationException) { return null; }
    }

    private static LiveMeadowLobbyOption[] ReadLobbyOptions(object lobby, out bool truncated)
    {
        var options = new List<LiveMeadowLobbyOption>();
        truncated = false;
        AddOptions(options, SafeGet(lobby, "configurableBools") as IDictionary, "bool", ref truncated);
        AddOptions(options, SafeGet(lobby, "configurableFloats") as IDictionary, "float", ref truncated);
        AddOptions(options, SafeGet(lobby, "configurableInts") as IDictionary, "int", ref truncated);
        LiveMeadowLobbyOption[] sorted = options.OrderBy(option => option.Name, StringComparer.Ordinal).ToArray();
        if (sorted.Length > ProtocolInfo.MaximumMeadowLobbyOptions) truncated = true;
        return sorted.Take(ProtocolInfo.MaximumMeadowLobbyOptions).ToArray();
    }

    private static void AddOptions(ICollection<LiveMeadowLobbyOption> destination, IDictionary? values, string type,
        ref bool truncated)
    {
        if (values == null) return;
        try
        {
            foreach (DictionaryEntry item in values)
            {
                int keyLength = Math.Max(0, ProtocolInfo.MaximumMeadowLobbyOptionNameLength - type.Length - 1);
                string name = CleanText(Convert.ToString(item.Key), keyLength, out bool nameTruncated);
                string value = CleanText(Convert.ToString(item.Value, System.Globalization.CultureInfo.InvariantCulture),
                    ProtocolInfo.MaximumMeadowLobbyOptionValueLength, out bool valueTruncated);
                truncated |= nameTruncated || valueTruncated;
                if (name.Length > 0) destination.Add(new() { Name = type + ":" + name, Value = value });
            }
        }
        catch (Exception) { truncated = true; }
    }

    private static string[] TextArray(object? value, int maximumItems, int maximumLength, out bool truncated)
    {
        try { return TextArray(GameAccess.Items(value).Select(item => Convert.ToString(item)), maximumItems, maximumLength, out truncated); }
        catch (Exception)
        {
            truncated = true;
            return Array.Empty<string>();
        }
    }

    private static string[] TextArray(IEnumerable<string?> values, int maximumItems, int maximumLength, out bool truncated)
    {
        truncated = false;
        var cleaned = new List<string>();
        foreach (string? value in values)
        {
            string item = CleanText(value, maximumLength, out bool itemTruncated);
            truncated |= itemTruncated;
            if (item.Length > 0) cleaned.Add(item);
        }
        string[] distinct = cleaned.Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal).ToArray();
        if (distinct.Length != cleaned.Count || distinct.Length > maximumItems) truncated = true;
        return distinct.Take(maximumItems).ToArray();
    }

    private static string CleanText(string? value, int maximum) => CleanText(value, maximum, out _);

    private static string CleanText(string? value, int maximum, out bool truncated)
    {
        truncated = false;
        if (string.IsNullOrWhiteSpace(value)) return "";
        string source = value!.Trim();
        string clean = new(source.Where(character => !char.IsControl(character)).Take(maximum).ToArray());
        if (clean.Length > 0 && char.IsHighSurrogate(clean[clean.Length - 1])) clean = clean.Substring(0, clean.Length - 1);
        truncated = !string.Equals(source, clean, StringComparison.Ordinal);
        return clean;
    }

    private static int? Count(object? value)
        => value is ICollection collection ? Math.Max(0, collection.Count) : NonNegativeInteger(SafeGet(value, "Count"));

    private static int? BytesPerSecond(object? value, int framesPerSecond)
    {
        long total = 0;
        int count = 0;
        foreach (object item in GameAccess.Items(value))
        {
            total = Math.Min(int.MaxValue, total + (NonNegativeInteger(item) ?? 0));
            count++;
        }
        return count == 0 ? null : (int)Math.Min(int.MaxValue, total * Math.Max(1, framesPerSecond) / count);
    }

    private static int? PositiveInteger(object? value)
        => NonNegativeInteger(value) is > 0 and var number ? number : null;

    private static int? NonNegativeInteger(object? value)
    {
        try
        {
            long number = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
            return number is >= 0 and <= int.MaxValue ? (int)number : null;
        }
        catch (Exception error) when (error is FormatException or InvalidCastException or OverflowException) { return null; }
    }

    private static uint? UnsignedInteger(object? value)
    {
        try { return Convert.ToUInt32(value, System.Globalization.CultureInfo.InvariantCulture); }
        catch (Exception error) when (error is FormatException or InvalidCastException or OverflowException) { return null; }
    }

    private static bool? Boolean(object? target, string name)
        => target == null ? null : SafeGet(target, name) as bool?;

    private static object? SafeGet(object? target, string name)
    {
        if (target == null) return null;
        try { return GameAccess.Get(target, name); }
        catch (Exception) { return null; }
    }

    private static float? Quality(object? value)
        => Number(value) is >= 0 and <= 1 and var number ? number : null;

    private static float? Rate(object? value)
        => Number(value) is >= 0 and var number ? number : null;

    private static float? Number(object? value)
    {
        try
        {
            float number = Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture);
            return !float.IsNaN(number) && !float.IsInfinity(number) ? number : null;
        }
        catch (Exception error) when (error is FormatException or InvalidCastException or OverflowException) { return null; }
    }

    private static string EnumText(object? value, int maximumLength = ProtocolInfo.MaximumMeadowLabelLength)
    {
        string text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        return CleanText(text, maximumLength);
    }

    private void RemoveDepartedAdvertisements()
    {
        foreach (string steamId in _advertisements.Keys.Where(id => !_participants.ContainsKey(id)).ToArray())
            _advertisements.Remove(steamId);
    }

    private void ResetLobby()
    {
        _lobby = null;
        IsSteamLobby = false;
        CurrentLobbyId = "";
        _peers = Array.Empty<MeadowLogPeer>();
        _participants.Clear();
        _connectionSamples.Clear();
        _advertisements.Clear();
        _nextAdvertisementTicks = 0;
        _nextConnectionSampleTicks = 0;
        ClearLocalAdvertisement();
        ClearInbox();
    }

    private void ClearLocalAdvertisement()
    {
        _available = false;
        _captureActive = false;
        _capturePaused = false;
        _deepTraceEnabled = false;
        _deepTracePeerIds = Array.Empty<string>();
        _captureId = "";
        _captureToken = "";
    }

    private void ClearInbox()
    {
        lock (_inboxSync)
        {
            while (_inbox.TryDequeue(out _)) { }
            _queuedPackets = 0;
            _queuedBytes = 0;
        }
        lock (_callbackSync)
        {
            while (_callbacks.TryDequeue(out _)) { }
            _queuedCallbacks = 0;
            _queuedCallbackBytes = 0;
        }
    }

    public void Dispose()
    {
        Unregister();
        ResetLobby();
    }

    private sealed class RemoteAdvertisement
    {
        internal int ProtocolVersion { get; set; }
        internal bool Available { get; set; }
        internal bool CaptureActive { get; set; }
        internal bool CapturePaused { get; set; }
        internal bool DeepTraceEnabled { get; set; }
        internal string CaptureId { get; set; } = "";
        internal string CaptureToken { get; set; } = "";
        internal long SeenAtTicks { get; set; }
        internal DateTime SeenUtc { get; set; }
    }

    private sealed class CallbackPacket
    {
        internal CallbackPacket(object sender, byte[] data)
        {
            Sender = sender;
            Data = data;
        }

        internal object Sender { get; }
        internal byte[] Data { get; }
    }
}

internal static class MeadowCustomSubscriber
{
    private static Type? _adapterType;
    private static Action<object, object>? _receive;

    internal static void SetReceiver(Action<object, object>? receive) => _receive = receive;
    private static void Dispatch(object sender, object packet) => _receive?.Invoke(sender, packet);

    internal static object Create(Type subscriberType, Action<object, object> receive)
    {
        SetReceiver(receive);
        if (_adapterType == null || !_adapterType.GetInterfaces().Contains(subscriberType))
            _adapterType = Build(subscriberType);
        return Activator.CreateInstance(_adapterType, new Action<object, object>(Dispatch))!;
    }

    private static Type Build(Type subscriberType)
    {
        var assemblyName = new AssemblyName("RWCompanion.MeadowLogPacketAdapter");
        AssemblyBuilder assembly = AppDomain.CurrentDomain.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
        ModuleBuilder module = assembly.DefineDynamicModule(assemblyName.Name!);
        TypeBuilder type = module.DefineType("RWCompanion.MeadowLogPacketSubscriber", TypeAttributes.Public | TypeAttributes.Sealed);
        type.AddInterfaceImplementation(subscriberType);

        FieldBuilder receive = type.DefineField("_receive", typeof(Action<object, object>), FieldAttributes.Private | FieldAttributes.InitOnly);
        ConstructorBuilder constructor = type.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard,
            new[] { typeof(Action<object, object>) });
        ILGenerator constructorIl = constructor.GetILGenerator();
        constructorIl.Emit(OpCodes.Ldarg_0);
        constructorIl.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
        constructorIl.Emit(OpCodes.Ldarg_0);
        constructorIl.Emit(OpCodes.Ldarg_1);
        constructorIl.Emit(OpCodes.Stfld, receive);
        constructorIl.Emit(OpCodes.Ret);

        MethodInfo activeInterface = subscriberType.GetProperty("Active")?.GetGetMethod()
            ?? throw new MissingMemberException(subscriberType.FullName, "Active");
        MethodBuilder active = type.DefineMethod(activeInterface.Name,
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
            typeof(bool), Type.EmptyTypes);
        ILGenerator activeIl = active.GetILGenerator();
        activeIl.Emit(OpCodes.Ldc_I4_1);
        activeIl.Emit(OpCodes.Ret);
        type.DefineMethodOverride(active, activeInterface);

        MethodInfo processInterface = subscriberType.GetMethod("ProcessPacket")
            ?? throw new MissingMemberException(subscriberType.FullName, "ProcessPacket");
        Type[] parameters = processInterface.GetParameters().Select(parameter => parameter.ParameterType).ToArray();
        if (parameters.Length != 2) throw new MissingMethodException(subscriberType.FullName, "ProcessPacket");
        MethodBuilder process = type.DefineMethod(processInterface.Name,
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
            typeof(void), parameters);
        ILGenerator processIl = process.GetILGenerator();
        processIl.Emit(OpCodes.Ldarg_0);
        processIl.Emit(OpCodes.Ldfld, receive);
        processIl.Emit(OpCodes.Ldarg_1);
        if (parameters[0].IsValueType) processIl.Emit(OpCodes.Box, parameters[0]);
        processIl.Emit(OpCodes.Ldarg_2);
        if (parameters[1].IsValueType) processIl.Emit(OpCodes.Box, parameters[1]);
        processIl.Emit(OpCodes.Callvirt, typeof(Action<object, object>).GetMethod("Invoke")!);
        processIl.Emit(OpCodes.Ret);
        type.DefineMethodOverride(process, processInterface);
        return type.CreateType()!;
    }
}
