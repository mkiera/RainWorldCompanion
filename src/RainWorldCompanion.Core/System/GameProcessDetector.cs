using System.ComponentModel;
using System.Diagnostics;

namespace RainWorldCompanion.Core.System;

/// <summary>
/// Detects Rain World by process name.
/// </summary>
public sealed class GameProcessDetector : IGameProcessDetector
{
    // Steam ships the executable as RainWorld.exe, but other distributions have used a space.
    private static readonly string[] CandidateProcessNames = { "RainWorld", "Rain World" };

    public bool IsGameRunning(out string? processName)
    {
        foreach (var candidate in CandidateProcessNames)
        {
            if (TryFindRunning(candidate, out var matched))
            {
                processName = matched;
                return true;
            }
        }

        processName = null;
        return false;
    }

    private static bool TryFindRunning(string candidate, out string? processName)
    {
        processName = null;

        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(candidate);
        }
        catch (Win32Exception)
        {
            // The process table can be unreadable under a restricted token. Treat that as
            // "cannot tell", which for this caller means not running.
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }

        try
        {
            if (processes.Length == 0)
            {
                return false;
            }

            try
            {
                processName = processes[0].ProcessName;
            }
            catch (InvalidOperationException)
            {
                // The process exited between the enumeration and this read. Report the name we
                // searched for rather than losing the hit.
                processName = candidate;
            }

            return true;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }
}
