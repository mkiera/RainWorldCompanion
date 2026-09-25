using RainWorldCompanion.LiveProtocol;

namespace RWCompanion.Mod;

internal sealed class FramePerformanceSampler
{
    private string _gameplayId = "";
    private long _sequence;
    private double _duration;
    private double _maximum;
    private int _frames;
    private int _over250;
    private int _over500;
    private bool _active;
    private bool _canReadCollections = true;

    internal LiveFramePerformance? Latest { get; private set; }

    internal void Observe(string gameplayId, bool active, double deltaSeconds)
    {
        if (!active || string.IsNullOrEmpty(gameplayId))
        {
            Reset();
            return;
        }
        if (!_active || !string.Equals(_gameplayId, gameplayId, StringComparison.Ordinal))
        {
            Reset();
            _active = true;
            _gameplayId = gameplayId;
            // The first delta includes time spent in the previous gameplay or pause state.
            return;
        }
        if (double.IsNaN(deltaSeconds) || double.IsInfinity(deltaSeconds) || deltaSeconds <= 0) return;
        _duration += deltaSeconds;
        _maximum = Math.Max(_maximum, deltaSeconds * 1000);
        _frames++;
        if (deltaSeconds >= 0.25) _over250++;
        if (deltaSeconds >= 0.5) _over500++;
        if (_duration < 1) return;
        Latest = new()
        {
            Sequence = ++_sequence,
            GameplayId = _gameplayId,
            Ready = true,
            DurationSeconds = _duration,
            FrameCount = _frames,
            MaximumFrameMilliseconds = _maximum,
            FramesOver250Milliseconds = _over250,
            FramesOver500Milliseconds = _over500,
            GarbageCollections = ReadCollections()
        };
        ClearWindow();
    }

    internal void Reset()
    {
        _active = false;
        _gameplayId = "";
        Latest = null;
        ClearWindow();
    }

    private void ClearWindow()
    {
        _duration = 0;
        _maximum = 0;
        _frames = 0;
        _over250 = 0;
        _over500 = 0;
    }

    private int? ReadCollections()
    {
        if (!_canReadCollections) return null;
        try { return GC.CollectionCount(0); }
        catch (NotImplementedException) { _canReadCollections = false; }
        catch (NotSupportedException) { _canReadCollections = false; }
        return null;
    }
}
