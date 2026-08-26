namespace RainWorldCompanion.Core.System;

public sealed class NullGameProcessDetector : IGameProcessDetector
{
    public bool IsGameRunning(out string? processName)
    {
        processName = null;
        return false;
    }
}
