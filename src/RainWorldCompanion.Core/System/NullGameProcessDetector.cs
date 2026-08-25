namespace RainWorldCompanion.Core.System;

/// <summary>
/// Detector that always reports the game as not running. Used by tests and by any code path
/// that must run without consulting the real process list.
/// </summary>
public sealed class NullGameProcessDetector : IGameProcessDetector
{
    public bool IsGameRunning(out string? processName)
    {
        processName = null;
        return false;
    }
}
