using System.Diagnostics;
using System.IO;
using System.Text;
using RainWorldCompanion.Core.Updates;

namespace RainWorldCompanion.Services;

/// <summary>
/// What became of an attempt to start the installer.
/// </summary>
/// <param name="ShouldExit">
/// Whether the app should now close. False means the installer is not going to replace anything
/// until somebody deals with it, so closing would take away the only thing able to explain that.
/// </param>
/// <param name="Message">A whole sentence, written to be shown as it is.</param>
public sealed record LaunchOutcome(bool ShouldExit, string Message);

/// <summary>Hands a downloaded installer to Windows.</summary>
public interface IInstallerLauncher
{
    LaunchOutcome Start(string installerPath);
}

/// <summary>
/// Starts the installer and watches it just long enough to tell working from stuck.
///
/// The switches are the whole contract with installer.iss, and each one is here for a reason:
///
///   /SILENT hides the wizard but keeps Setup's progress window, so the user sees something after
///   this app's window disappears. /VERYSILENT would leave a blank screen that reads as a crash.
///
///   /CLOSEAPPLICATIONS covers losing the race with this app's own exit. Setup does this by default
///   and the script asks for it too, so passing it means the update still works if either ever
///   changes.
///
///   /NORESTARTAPPLICATIONS keeps the relaunch owned by exactly one thing, the [Run] entry in the
///   script. Setup's Restart Manager putting the app back as well would open a second copy, which
///   the single-instance check would then refuse with a message about it already running, in the
///   middle of an update.
///
/// Deliberately absent: /DIR and /TASKS, because UsePreviousAppDir and UsePreviousTasks are what
/// keep a silent update from relocating the install and clearing the desktop-shortcut choice, and
/// passing either would override them. Also absent is /SUPPRESSMSGBOXES: once this app has exited,
/// a message box is the only way Setup can tell the user anything.
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
    /// How long to watch before deciding. Lengthening this is self-defeating: /CLOSEAPPLICATIONS
    /// shuts this app down during Setup's "Preparing to Install" stage, so a longer watch spends
    /// its extra time waiting for an answer while being closed for the privilege.
    /// </summary>
    private static readonly TimeSpan StartupWatch = TimeSpan.FromMilliseconds(1500);

    public LaunchOutcome Start(string installerPath)
    {
        // Only ever a file this app downloaded, into its own folder. Resolved through the
        // filesystem, so a junction pointing out of the updates folder does not get past it.
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
                // The working directory stays in the updates folder. The default is this process's
                // own directory, which is the install folder, and holding that open is holding
                // open the very files Setup has to replace.
                WorkingDirectory = UpdatesFolder.Ensure(),
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { },
            }.With(Switches, $"/LOG={logPath}"))
                ?? throw new InvalidOperationException("no process");
        }
        catch (Exception e)
        {
            // Missing, blocked by antivirus or by policy, or not an executable at all. Whatever it
            // was, this app is still running and is the only thing able to say so.
            return new LaunchOutcome(false, "Could not start the installer. " + e.Message);
        }

        return Watch(process, logPath);
    }

    /// <summary>
    /// Reads what can be known in the first second and a half, and nothing beyond it.
    ///
    /// The rule underneath all four outcomes: every refusal keeps the app running. Somebody left
    /// with neither the old version nor the new one has no way back, so the only cases that close
    /// the app are the one where Setup has provably finished and the one where it is provably
    /// working.
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
                    // The one outcome that is not a guess. Setup ran to completion, and its [Run]
                    // entry has already started the new version, so this process is now simply the
                    // stale copy of an app that is running again elsewhere.
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
                // Setup is on screen waiting for an answer it will wait for forever. Exiting into
                // that would leave a dialog with no application behind it, which is the case that
                // looks identical to a crash.
                return new LaunchOutcome(
                    false,
                    "The installer has stopped to ask something and is waiting for an answer: "
                    + $"\"{asking}\" RainWorld Companion has stayed open rather than leave you with "
                    + "a dialog and nothing else. Deal with the installer window first.");
            }

            // Running, and it has said nothing to suggest otherwise. That is the whole of what can
            // be known from here, so it is all this claims.
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
            // Nothing to read is the normal case a second into an install, not a fault.
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
            // A stale log only costs the accuracy of the message below, and the timestamps in it
            // still tell this run's entries from the last one's.
        }
    }
}

file static class ProcessStartInfoExtensions
{
    /// <summary>
    /// Adds arguments one at a time. ArgumentList rather than a single Arguments string, so the
    /// log path is passed as one argument whatever is in it and nothing has to be quoted by hand.
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
