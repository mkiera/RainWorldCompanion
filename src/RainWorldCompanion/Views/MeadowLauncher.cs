using System.Diagnostics;
using System.IO;

using RainWorldCompanion.Core.System;

namespace RainWorldCompanion.Views;

internal static class MeadowLauncher
{
    public static bool Start(MeadowStart start, Action<string>? report = null)
    {
        ArgumentNullException.ThrowIfNull(start);

        if (!start.CanRun)
        {
            report?.Invoke(start.Problem ?? "The lobby could not be joined.");
            return false;
        }

        if (start.ThroughSteam)
        {
            bool failed = false;
            WorkshopLink.Open(start.SteamUrl, problem =>
            {
                failed = true;
                report?.Invoke("Steam could not be asked to join the lobby: " + problem);
            });
            return !failed;
        }

        try
        {
            // Started from the game folder, because Rain World reads its own files by relative path.
            var info = new ProcessStartInfo(start.Executable)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(start.Executable) ?? "",
            };

            foreach (string argument in start.Arguments)
            {
                info.ArgumentList.Add(argument);
            }

            Process.Start(info);
            return true;
        }
        catch (Exception ex)
        {
            report?.Invoke("Rain World could not be started: " + ex.Message);
            return false;
        }
    }
}
