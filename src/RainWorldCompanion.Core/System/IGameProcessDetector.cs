namespace RainWorldCompanion.Core.System;

/// <summary>
/// Reports whether Rain World is currently running, so backup and restore can refuse to
/// touch files the game may have open.
/// </summary>
public interface IGameProcessDetector
{
    /// <summary>
    /// Returns true when a Rain World process is running. <paramref name="processName"/>
    /// carries the name that matched, and is null when nothing matched.
    /// </summary>
    bool IsGameRunning(out string? processName);
}
