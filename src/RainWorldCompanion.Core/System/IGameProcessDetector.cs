namespace RainWorldCompanion.Core.System;

public interface IGameProcessDetector
{
    bool IsGameRunning(out string? processName);
}
