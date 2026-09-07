using System.Collections.Concurrent;
using RainWorldCompanion.LiveProtocol;
using UnityEngine;

namespace RWCompanion.Mod;

public sealed class MeadowControlMessage
{
    public int AllTeleportVersion { get; set; }
    public bool AllowsHostControl { get; set; }
    public int Version { get; set; } = 1;
    public string Kind { get; set; } = "";
    public string Id { get; set; } = "";
    public string Grant { get; set; } = "";
    public string ModVersion { get; set; } = "";
    public string PlayerId { get; set; } = "";
    public string RoomId { get; set; } = "";
    public string Region { get; set; } = "";
    public long ExpiresUtcTicks { get; set; }
    public bool Success { get; set; }
    public string Message { get; set; } = "";
}

internal sealed partial class MeadowHostControl : IDisposable
{
    private readonly ConcurrentQueue<(object Sender, object? Lobby, string Json)> _inbox = new();
    private readonly Dictionary<object, (MeadowControlMessage Hello, float Seen)> _peers = new();
    private readonly HostControlPermission _permission = new();
    private readonly HashSet<string> _seenRequests = new();
    private object? _lobby;
    private object? _game;
    private string _gameplay = "";
    private float _nextHello;
    private TeleportOperation? _incoming;
    private object? _requester;
    private MeadowControlMessage? _request;
    private LiveCommand? _outgoing;
    private object? _recipient;
    private float _sentAt;
    private object? _outgoingLobby;
    private string _outgoingGameplay = "";
    private LiveCommandResult? _result;
    private bool _allowed;
    internal string LastAction { get; private set; } = "";

    internal MeadowHostControl() => MeadowRpc.Received = (sender, json) =>
    {
        if (_inbox.Count < 32) _inbox.Enqueue((sender, MeadowPlayers.Lobby, json));
    };

    internal LiveCommandResult? Update(object? game, string gameplay, bool allowed, bool localBusy)
    {
        _game = game;
        _allowed = allowed;
        _gameplay = gameplay;
        object? lobby = MeadowPlayers.Lobby;
        if (!ReferenceEquals(_lobby, lobby)) { _peers.Clear(); _lobby = lobby; }
        if (_permission.Update(lobby, GameAccess.Get(lobby, "owner"), gameplay, allowed))
        {
            _nextHello = 0;
            _seenRequests.Clear();
            LastAction = "";
        }
        if (_incoming != null)
        {
            if (!_permission.Accepts(_requester!, _request!.Grant) || !IsParticipant(_requester!))
                FinishIncoming(new() { Id = _request.Id, Message = "Host control was revoked or the host changed." });
            else if (_incoming.Update(game) is { } completed) FinishIncoming(completed);
        }
        if (_outgoing != null && (!ReferenceEquals(_outgoingLobby, lobby) || _outgoingGameplay != gameplay
            || !MeadowPlayers.IsHost || !IsParticipant(_recipient!) || Time.unscaledTime - _sentAt > 50))
        {
            _result = new() { Id = _outgoing.Id, Message = "Host teleport lost its peer or timed out. Check the player's location before retrying." };
            _outgoing = null;
        }
        if (lobby != null && MeadowRpc.Register())
        {
            if (Time.unscaledTime >= _nextHello)
            {
                _nextHello = Time.unscaledTime + 2;
                foreach (var peer in GameAccess.Items(GameAccess.Get(lobby, "participants")))
                    if (GameAccess.Get(peer, "isMe") is false && IsParticipant(peer))
                        Send(peer, new() { Kind = "hello", Grant = _permission.Grant, ModVersion = ProtocolInfo.ModVersion,
                            AllTeleportVersion = 1, AllowsHostControl = allowed });
            }
            while (_inbox.TryDequeue(out var item))
            {
                if (!ReferenceEquals(item.Lobby, lobby) || !IsParticipant(item.Sender)) continue;
                try { Receive(item.Sender, LiveJson.Deserialize<MeadowControlMessage>(item.Json), localBusy); }
                catch (Exception error) when (error is System.Runtime.Serialization.SerializationException or ArgumentException or System.Xml.XmlException) { }
            }
        }
        else while (_inbox.TryDequeue(out _)) { }
        UpdateGroup();
        var result = _result;
        _result = null;
        return result;
    }

    internal bool Busy => _incoming != null || _outgoing != null || _group != null;

    internal void Describe(LivePlayer[] players)
    {
        foreach (var player in players)
        {
            if (player.IsLocal)
            {
                player.CompanionVersion = ProtocolInfo.ModVersion;
                player.AllowsHostControl = _allowed;
                continue;
            }
            var owner = MeadowPlayers.Owner(player.Id);
            if (owner != null && _peers.TryGetValue(owner, out var peer) && Time.unscaledTime - peer.Seen < 7)
            {
                player.CompanionVersion = peer.Hello.ModVersion;
                player.AllowsHostControl = peer.Hello.AllTeleportVersion > 0 ? peer.Hello.AllowsHostControl : peer.Hello.Grant.Length > 0;
            }
        }
    }

    internal void Request(LiveCommand command)
    {
        if (!MeadowPlayers.IsHost) throw new InvalidOperationException("Only the current Rain Meadow host can request a teleport.");
        if (Busy) throw new InvalidOperationException("A host teleport is already in progress.");
        var owner = MeadowPlayers.Owner(command.PlayerId);
        if (owner == null || GameAccess.Get(owner, "isMe") is not false || !IsParticipant(owner)
            || !_peers.TryGetValue(owner, out var peer) || Time.unscaledTime - peer.Seen >= 7 || peer.Hello.Grant.Length == 0)
            throw new InvalidOperationException("This player needs an updated Companion Game Hook and Allow host control enabled.");
        Send(owner, new() { Kind = "teleport", Id = command.Id, Grant = peer.Hello.Grant,
            PlayerId = command.PlayerId, RoomId = command.RoomId, Region = command.Region,
            ExpiresUtcTicks = DateTime.UtcNow.AddSeconds(10).Ticks });
        _outgoing = command;
        _recipient = owner;
        _sentAt = Time.unscaledTime;
        _outgoingLobby = _lobby;
        _outgoingGameplay = _gameplay;
    }

    private bool IsParticipant(object peer) => _lobby != null && GameAccess.Get(peer, "hasLeft") is false
        && GameAccess.Items(GameAccess.Get(_lobby, "participants")).Any(p => ReferenceEquals(p, peer));

    private void Receive(object sender, MeadowControlMessage message, bool localBusy)
    {
        if (message.Version != 1) return;
        if (message.Kind == "hello")
        {
            if (message.Grant is { Length: <= 64 } && message.ModVersion is { Length: <= 40 }) _peers[sender] = (message, Time.unscaledTime);
            return;
        }
        if (message.Kind?.EndsWith("-all", StringComparison.Ordinal) == true) { ReceiveGroup(sender, message, localBusy); return; }
        if (message.Kind == "result")
        {
            if (_outgoing != null && ReferenceEquals(sender, _recipient) && message.Id == _outgoing.Id)
            {
                _result = new() { Id = message.Id, Success = message.Success, Message = message.Message };
                _outgoing = null;
            }
            return;
        }
        if (message.Kind != "teleport" || message.Id is not { Length: > 0 and <= 64 }) return;
        string? rejection = !_permission.Accepts(sender, message.Grant) ? "Host control is off or this request is from an outdated host or game session."
            : message.ExpiresUtcTicks < DateTime.UtcNow.Ticks || message.ExpiresUtcTicks > DateTime.UtcNow.AddSeconds(30).Ticks ? "The host request expired. Check both computers' clocks."
            : localBusy || Busy ? "A teleport is already in progress."
            : _game == null || MeadowPlayers.FindLocal(message.PlayerId) == null ? "The requested player is not owned by this client."
            : !ValidId(message.RoomId, 100) || !ValidId(message.Region, 20) ? "Invalid destination." : null;
        if (rejection != null) { Reply(sender, new() { Id = message.Id, Message = rejection }); return; }
        if (!_seenRequests.Add(message.Id)) return;
        if (_seenRequests.Count > 256) { _permission.Update(null, null, "", false); _nextHello = 0; Reply(sender, new() { Id = message.Id, Message = "Host permission is refreshing. Try again." }); return; }
        try
        {
            _incoming = new TeleportOperation(new() { Id = message.Id, PlayerId = message.PlayerId,
                RoomId = message.RoomId, Region = message.Region, GameplayId = _gameplay }, _game!);
            _request = message;
            _requester = sender;
            LastAction = "Host requested teleport to " + message.RoomId + ".";
        }
        catch (Exception error) { Reply(sender, new() { Id = message.Id, Message = error.GetBaseException().Message }); }
    }

    private void FinishIncoming(LiveCommandResult result)
    {
        LastAction = "Host teleport: " + result.Message;
        object? requester = _requester;
        _incoming = null;
        _request = null;
        _requester = null;
        if (requester != null && IsParticipant(requester)) Reply(requester, result);
    }

    private static bool ValidId(string? text, int maximum) => text is { Length: > 0 } && text.Length <= maximum && text.All(c => char.IsLetterOrDigit(c) || c == '_');
    private static void Send(object peer, MeadowControlMessage message) => MeadowRpc.Send(peer, LiveJson.Serialize(message));
    private static void Reply(object peer, LiveCommandResult result) => Send(peer, new() { Kind = "result", Id = result.Id, Success = result.Success, Message = result.Message });
    internal void Suspend()
    {
        _group = null;
        _permission.Update(null, null, "", false);
        _incoming = null;
        _outgoing = null;
        _request = null;
        _requester = null;
        _recipient = null;
        _lobby = null;
        _nextHello = 0;
        _peers.Clear();
        _seenRequests.Clear();
        while (_inbox.TryDequeue(out _)) { }
    }

    public void Dispose() { MeadowRpc.Received = null; Suspend(); }
}
