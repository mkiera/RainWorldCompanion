using System.Diagnostics;
using System.IO;
using System.Text;
using RainWorldCompanion.Core.Updates;

namespace RainWorldCompanion.Services;

/// <param name="ShouldExit">False means the installer will replace nothing until somebody deals with it.</param>
/// <param name="Message">A whole sentence, written to be shown as it is.</param>
public sealed record LaunchOutcome(bool ShouldExit, string Message);

public interface IInstallerLauncher
{
    LaunchOutcome Start(string installerPath);
}

/// <summary>
/// The switches are the whole contract with installer.iss. /DIR and /TASKS are deliberately absent
/// because UsePreviousAppDir and UsePreviousTasks keep a silent update from relocating the install,
/// and /SUPPRESSMSGBOXES because once this app exits a message box is Setup's only way to speak.
/// </summary>
public sealed class InstallerLauncher : IInstallerLauncher
{
    private static readonly string[] Switches =
    [
        "/SILENT",
        "/CLOSEAPPLICATIONS",
        "/NORESTARTAPPLICATIONS",
    ];

    /// <summary>
    /// Lengthening this is self-defeating: /CLOSEAPPLICATIONS shuts this app down during Setup's
    /// "Preparing to Install" stage.
    /// </summary>
    private static readonly TimeSpan StartupWatch = TimeSpan.FromMilliseconds(1500);

    public LaunchOutcome Start(string installerPath)
    {
        if (!UpdatesFolder.Contains(installerPath))
        {
            return new LaunchOutcome(false, "That installer is not in the updates folder, so it was not run.");
        }

        if (!File.Exists(installerPath))
        {
            return new LaunchOutcome(false, "The downloaded installer is no longer there.");
        }

        if (!UpdateUrls.IsInstallerAsset(Path.GetFileName(installerPath)))
        {
            return new LaunchOutcome(
                false,
                $"\"{Path.GetFileName(installerPath)}\" is not the RainWorld Companion installer, so it was not run.");
        }

        var logPath = Path.Combine(UpdatesFolder.Location, "update.log");
        Delete(logPath);

        Process process;
        try
        {
            process = Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                // The default is this process's own directory, which is the install folder, and
                // holding that open is holding open the files Setup has to replace.
                WorkingDirectory = UpdatesFolder.Ensure(),
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { },
            }.With(Switches, $"/LOG={logPath}"))
                ?? throw new InvalidOperationException("no process");
        }
        catch (Exception e)
        {
            return new LaunchOutcome(false, "Could not start the installer. " + e.Message);
        }

        return Watch(process, logPath);
    }

    /// <summary>
    /// Every refusal keeps the app running: somebody left with neither the old version nor the new
    /// one has no way back.
    /// </summary>
    private static LaunchOutcome Watch(Process process, string logPath)
    {
        using (process)
        {
            var exited = process.WaitForExit((int)StartupWatch.TotalMilliseconds);
            var messages = ReadLog(logPath);
            var asking = SetupLog.PendingDialog(messages);

            if (exited)
            {
                var code = process.ExitCode;
                if (InstallerExitCodes.IsSuccess(code))
                {
                    // Setup's [Run] entry has already started the new version.
                    return new LaunchOutcome(
                        true, "The update is installed and the new version has already started.");
                }

                var detail = asking is null ? "" : $" It said: \"{asking}\"";
                return new LaunchOutcome(
                    false,
                    $"The installer stopped straight away. {InstallerExitCodes.Describe(code)}{detail} "
                    + $"Its log is at {logPath}.");
            }

            if (asking is not null)
            {
                return new LaunchOutcome(
                    false,
                    "The installer has stopped to ask something and is waiting for an answer: "
                    + $"\"{asking}\" RainWorld Companion has stayed open rather than leave you with "
                    + "a dialog and nothing else. Deal with the installer window first.");
            }

            var note = messages.Count == 0 ? " but has not written to its log yet" : "";
            return new LaunchOutcome(
                true,
                $"The installer is running{note}, and RainWorld Companion has to close so it can "
                + "replace these files. It will start the new version when it is done.");
        }
    }

    private static IReadOnlyList<string> ReadLog(string logPath)
    {
        try
        {
            if (!File.Exists(logPath))
            {
                return [];
            }

            // Shared read: Setup still has the file open and writing to it.
            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            var lines = new List<string>();
            while (reader.ReadLine() is { } line)
            {
                lines.Add(line);
            }

            return SetupLog.ReadMessages(lines);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void Delete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

file static class ProcessStartInfoExtensions
{
    /// <summary>
    /// ArgumentList rather than a single Arguments string, so the log path is passed as one
    /// argument whatever is in it.
    /// </summary>
    public static ProcessStartInfo With(this ProcessStartInfo info, string[] switches, params string[] extra)
    {
        foreach (var argument in switches)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var argument in extra)
        {
            info.ArgumentList.Add(argument);
        }

        return info;
    }
}
