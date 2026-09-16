using RainWorldCompanion.LiveProtocol;

namespace RainWorldCompanion.Core.LogStreaming;

internal sealed class AutomaticTraceDetector
{
    private string _context = "";
    private string _room = "";
    private DateTimeOffset _warmUntil;
    private DateTimeOffset _lastSample;
    private DateTimeOffset _cooldownUntil;
    private long _sequence;
    private int _slowWindows;
    private readonly Queue<(DateTimeOffset Time, long Bytes)> _memory = new();

    public string? Observe(LiveSnapshot snapshot, DateTimeOffset time, DateTimeOffset? receivedAt = null)
    {
        var performance = snapshot.Trace?.Performance;
        string context = snapshot.SessionId + ":" + snapshot.GameplayId;
        string room = snapshot.Players.FirstOrDefault(player => player.IsLocal)?.RoomId ?? "";
        bool valid = snapshot.State == "gameplay" && snapshot.Trace?.IsFocused != false && snapshot.GameplayId.Length > 0 && room.Length > 0
            && performance is { Ready: true, FrameCount: > 0 }
            && performance.GameplayId == snapshot.GameplayId
            && double.IsFinite(performance.DurationSeconds) && performance.DurationSeconds is >= 1 and <= 30
            && Math.Abs(((receivedAt ?? time) - time).TotalSeconds) <= 15;
        if (!valid || context != _context || time < _lastSample || time - _lastSample > TimeSpan.FromSeconds(35))
        {
            _context = context;
            _warmUntil = time.AddSeconds(15);
            _slowWindows = 0;
            _memory.Clear();
            _sequence = 0;
        }
        if (room != _room)
        {
            _room = room;
            if (_warmUntil < time.AddSeconds(5)) _warmUntil = time.AddSeconds(5);
            _slowWindows = 0;
            _memory.Clear();
        }
        _lastSample = time;
        if (!valid || performance!.Sequence <= _sequence) return null;
        _sequence = performance.Sequence;
        long bytes = snapshot.Trace!.ManagedMemoryBytes;
        while (_memory.Count > 0 && time - _memory.Peek().Time > TimeSpan.FromSeconds(12)) _memory.Dequeue();
        bool growth = _memory.Count > 0 && time - _memory.Peek().Time >= TimeSpan.FromSeconds(8)
            && bytes - _memory.Peek().Bytes >= 512L * 1024 * 1024
            && bytes >= _memory.Peek().Bytes * 1.25;
        if (_memory.Count < 16) _memory.Enqueue((time, bytes));
        bool slow = performance.DurationSeconds / performance.FrameCount >= 0.1
            && performance.FramesOver250Milliseconds >= 1;
        _slowWindows = slow && time >= _warmUntil ? _slowWindows + 1 : 0;
        if (time < _cooldownUntil || (_slowWindows < 3 && !(growth && _slowWindows >= 2))) return null;
        _cooldownUntil = time.AddMinutes(5);
        return growth ? "Rapid managed-memory growth with sustained slow frames"
            : "Sustained frame stalls (10 FPS or less across three performance windows)";
    }
}
