using RainWorldCompanion.LiveProtocol;
using UnityEngine;

namespace RWCompanion.Mod;

internal sealed partial class MeadowHostControl
{
    private sealed class GroupRequest
    {
        internal string Id = "";
        internal string Gameplay = "";
        internal string Grant = "";
        internal object Lobby = null!;
        internal object Host = null!;
        internal GroupTeleportOperation Operation = null!;
        internal readonly Dictionary<object, string> Peers = new();
        internal readonly HashSet<object> Ready = new();
        internal readonly Dictionary<object, bool> Results = new();
        internal bool ReadySent;
        internal bool LocalFinished;
        internal bool Committed;
        internal bool Coordinator;
        internal float Started = Time.unscaledTime;
    }

    private GroupRequest? _group;

    internal string AllUnavailableReason()
    {
        if (!MeadowPlayers.IsHost) return "Only the host can teleport everyone.";
        var blockers = new List<string>();
        if (!_allowed) blockers.Add("You: Allow host control is off");
        if (_game == null || GameAccess.Get(_game, "IsStorySession") is not true) blockers.Add("You: enter campaign gameplay");
        foreach (var peer in GameAccess.Items(GameAccess.Get(_lobby, "participants")))
        {
            if (GameAccess.Get(peer, "isMe") is true || !IsParticipant(peer)) continue;
            string name = GameAccess.Text(GameAccess.Get(peer, "id"), "DisplayName");
            if (string.IsNullOrEmpty(name)) name = "Player " + GameAccess.Text(peer, "inLobbyId");
            if (!_peers.TryGetValue(peer, out var state) || Time.unscaledTime - state.Seen >= 7)
                blockers.Add(name + ": no compatible rwcompanion mod detected");
            else if (state.Hello.AllTeleportVersion != 1) blockers.Add(name + ": update rwcompanion to 1.0.5 or newer");
            else if (!state.Hello.AllowsHostControl) blockers.Add(name + ": Allow host control is off");
            else if (state.Hello.Grant.Length == 0) blockers.Add(name + ": not in gameplay");
        }
        var players = MeadowPlayers.Read() ?? Array.Empty<LivePlayer>();
        if (players.Length == 0) blockers.Add("No players are in gameplay");
        foreach (var player in players)
            if (player.Dead != false || string.IsNullOrEmpty(player.RoomId)) blockers.Add(player.Name + ": must be alive and in a room");
        return string.Join(". ", blockers);
    }

    internal void RequestAll(LiveCommand command)
    {
        string reason = AllUnavailableReason();
        if (reason.Length > 0) throw new InvalidOperationException(reason);
        if (Busy) throw new InvalidOperationException("A teleport is already in progress.");
        var group = new GroupRequest { Id = command.Id, Lobby = _lobby!, Host = GameAccess.Get(_lobby, "owner")!,
            Gameplay = _gameplay, Grant = _permission.Grant, Coordinator = true,
            Operation = new GroupTeleportOperation(command.Id, command.RoomId, command.Region, _game!) };
        foreach (var peer in GameAccess.Items(GameAccess.Get(_lobby, "participants")))
            if (GameAccess.Get(peer, "isMe") is false && IsParticipant(peer)) group.Peers.Add(peer, _peers[peer].Hello.Grant);
        _group = group;
        try
        {
            foreach (var peer in group.Peers)
                Send(peer.Key, new() { Kind = "prepare-all", Id = group.Id, Grant = peer.Value,
                    RoomId = command.RoomId, Region = command.Region, ExpiresUtcTicks = DateTime.UtcNow.AddSeconds(10).Ticks });
        }
        catch { EndGroup("Could not prepare every player. No group move was committed.", false); throw; }
        LastAction = "Preparing Teleport all to " + command.RoomId + ".";
    }

    private void ReceiveGroup(object sender, MeadowControlMessage message, bool localBusy)
    {
        if (message.Id is not { Length: > 0 and <= 64 }) return;
        if (message.Kind == "prepare-all")
        {
            string? rejection = !_permission.Accepts(sender, message.Grant) ? "Allow host control is off or the host/gameplay changed."
                : Busy || localBusy ? "A teleport is already in progress."
                : _game == null ? "Not in gameplay."
                : message.ExpiresUtcTicks < DateTime.UtcNow.Ticks || message.ExpiresUtcTicks > DateTime.UtcNow.AddSeconds(30).Ticks ? "Group request expired."
                : !ValidId(message.RoomId, 100) || !ValidId(message.Region, 20) ? "Invalid destination." : null;
            if (rejection != null) { Send(sender, new() { Kind = "result-all", Id = message.Id, Message = rejection }); return; }
            if (!_seenRequests.Add(message.Id)) return;
            if (_seenRequests.Count > 256) { _seenRequests.Clear(); _seenRequests.Add(message.Id); }
            try
            {
                _group = new() { Id = message.Id, Host = sender, Lobby = _lobby!, Grant = message.Grant, Gameplay = _gameplay,
                    Operation = new GroupTeleportOperation(message.Id, message.RoomId, message.Region, _game!) };
                LastAction = "Host is preparing Teleport all to " + message.RoomId + ".";
            }
            catch (Exception error) { Send(sender, new() { Kind = "result-all", Id = message.Id, Message = error.GetBaseException().Message }); }
            return;
        }
        var group = _group;
        if (group == null || group.Id != message.Id) return;
        if (group.Coordinator)
        {
            if (!group.Peers.ContainsKey(sender)) return;
            if (message.Kind == "ready-all") group.Ready.Add(sender);
            if (message.Kind == "result-all")
            {
                if (!group.Committed && !message.Success) { EndGroup("A player rejected Teleport all: " + message.Message, false); return; }
                if (group.Committed) group.Results[sender] = message.Success;
            }
        }
        else if (ReferenceEquals(sender, group.Host))
        {
            if (message.Kind == "cancel-all") EndGroup("Host cancelled Teleport all.", false);
            else if (message.Kind == "commit-all" && _permission.Accepts(sender, group.Grant)
                && message.Grant == group.Grant && group.ReadySent && group.Operation.Ready)
                group.Committed = true;
        }
    }

    private void UpdateGroup()
    {
        var group = _group;
        if (group == null) return;
        if (!ReferenceEquals(group.Lobby, _lobby) || group.Gameplay != _gameplay
            || !_permission.Accepts(group.Host, group.Grant) || !IsParticipant(group.Host))
        { EndGroup("Teleport all stopped because permission, host, or gameplay changed.", false); return; }
        if (Time.unscaledTime - group.Started > 50) { EndGroup("Teleport all timed out. Check everyone's location before retrying.", false); return; }
        if (group.Coordinator && !group.Committed)
        {
            var participants = GameAccess.Items(GameAccess.Get(_lobby, "participants")).Where(p => GameAccess.Get(p, "isMe") is false && IsParticipant(p)).ToArray();
            if (participants.Length != group.Peers.Count || participants.Any(p => !group.Peers.ContainsKey(p))
                || group.Peers.Any(p => !_peers.TryGetValue(p.Key, out var state) || state.Hello.Grant != p.Value || Time.unscaledTime - state.Seen >= 7))
            { EndGroup("Teleport all cancelled because lobby membership or a player's permission changed.", false); return; }
        }
        if (!group.LocalFinished)
        {
            group.Operation.Committed = group.Committed;
            if (group.Operation.Update(_game) is { } result)
            {
                if (!result.Success) { EndGroup("Teleport all failed: " + result.Message, false); return; }
                group.LocalFinished = true;
            }
        }
        if (!group.Coordinator)
        {
            if (group.LocalFinished) { EndGroup("All local players arrived.", true); return; }
            if (group.Operation.Ready && !group.ReadySent)
            {
                group.ReadySent = true;
                Send(group.Host, new() { Kind = "ready-all", Id = group.Id });
            }
        }
        else
        {
            if (!group.Committed && group.Operation.Ready && group.Ready.Count == group.Peers.Count)
            {
                group.Committed = true;
                try
                {
                    foreach (var peer in group.Peers) Send(peer.Key, new() { Kind = "commit-all", Id = group.Id, Grant = peer.Value });
                }
                catch { EndGroup("Group delivery failed. Some players may have moved. Check everyone's location before retrying.", false); return; }
                LastAction = "Teleporting everyone...";
            }
            if (group.LocalFinished && group.Results.Count == group.Peers.Count)
                EndGroup(group.Results.Values.All(success => success) ? "Everyone arrived." : "Some players rejected the move. Check their locations before retrying.", group.Results.Values.All(success => success));
        }
    }

    private void EndGroup(string message, bool success)
    {
        var group = _group;
        _group = null;
        if (group == null) return;
        LastAction = message;
        if (group.Coordinator)
        {
            _result = new() { Id = group.Id, Success = success, Message = message };
            foreach (var peer in group.Peers.Keys)
                if (IsParticipant(peer) && !success) Send(peer, new() { Kind = "cancel-all", Id = group.Id });
        }
        else if (IsParticipant(group.Host)) Send(group.Host, new() { Kind = "result-all", Id = group.Id, Success = success, Message = message });
    }
}
