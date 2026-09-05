// Usings sit above the namespace: RainWorldCompanion.Core.System would otherwise shadow System.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace RainWorldCompanion.Core.System;

// Steam marks Rain World as running from the moment a process asks under its app id until that
// process exits, and shutting the API down does not clear it. So the asking is done by a second
// copy of this program that exits with the answer, and the app itself never talks to Steam.
public static class MeadowSteamProcess
{
    public const string Switch = "--read-lobbies";

    // Steam's own answer takes about a second and is allowed the timeout. This is for a helper
    // that never gets that far, such as one stuck behind a Steam client that is still starting.
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General);

    public static bool IsHelperCall(IReadOnlyList<string> args) =>
        args.Count > 0 && string.Equals(args[0], Switch, StringComparison.Ordinal);

    public static IReadOnlyList<string> Arguments(string? gameInstallPath, string? meadowVersion, TimeSpan timeout) =>
        new[]
        {
            Switch,
            gameInstallPath ?? "",
            meadowVersion ?? "",
            Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds)).ToString(CultureInfo.InvariantCulture),
        };

    public static bool TryParse(
        IReadOnlyList<string> args,
        out string? gameInstallPath,
        out string? meadowVersion,
        out TimeSpan timeout)
    {
        gameInstallPath = null;
        meadowVersion = null;
        timeout = TimeSpan.Zero;

        if (args.Count != 4
            || !IsHelperCall(args)
            || !int.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out int seconds)
            || seconds <= 0)
        {
            return false;
        }

        gameInstallPath = args[1].Length == 0 ? null : args[1];
        meadowVersion = args[2].Length == 0 ? null : args[2];
        timeout = TimeSpan.FromSeconds(seconds);
        return true;
    }

    // The helper's whole job: read, write one JSON document, and be gone. Every outcome is JSON,
    // so the parent never has to tell a refusal from a crash by the exit code alone.
    public static int RunHelper(
        IReadOnlyList<string> args,
        TextWriter output,
        Func<string?, string?, TimeSpan, MeadowLobbyList>? read = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);

        if (!TryParse(args, out string? game, out string? version, out TimeSpan timeout))
        {
            output.Write(ToJson(MeadowLobbyList.Refused("The lobby read was started with the wrong arguments.")));
            output.Flush();
            return 2;
        }

        MeadowLobbyList list;
        try
        {
            list = read is null
                ? MeadowSteam.Read(game, version, timeout)
                : read(game, version, timeout);
        }
        catch (Exception ex)
        {
            list = MeadowLobbyList.Refused("Steam could not be read: " + ex.Message);
        }

        output.Write(ToJson(list));
        output.Flush();
        return 0;
    }

    public static string ToJson(MeadowLobbyList list) => JsonSerializer.Serialize(list, Json);

    public static MeadowLobbyList? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<MeadowLobbyList>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static MeadowLobbyList ReadThrough(
        string? programPath,
        string? gameInstallPath,
        string? meadowVersion,
        TimeSpan timeout,
        CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(programPath) || !File.Exists(programPath))
        {
            return MeadowLobbyList.Refused(
                "The app's own program file could not be found, so nothing could ask Steam for the lobby list.");
        }

        var start = new ProcessStartInfo(programPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (string argument in Arguments(gameInstallPath, meadowVersion, timeout))
        {
            start.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(start)
                ?? throw new InvalidOperationException("no process was started");
        }
        catch (Exception ex)
        {
            return MeadowLobbyList.Refused("The lobby read could not be started: " + ex.Message);
        }

        using (process)
        {
            // Both pipes are drained while waiting: a helper that fills one and blocks on it would
            // otherwise wait on the parent, which is waiting on it.
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> errors = process.StandardError.ReadToEndAsync();

            DateTimeOffset stop = DateTimeOffset.UtcNow + timeout + Grace;
            while (!process.WaitForExit(250))
            {
                if (cancellation.IsCancellationRequested || DateTimeOffset.UtcNow > stop)
                {
                    TryKill(process);
                    return MeadowLobbyList.Refused(cancellation.IsCancellationRequested
                        ? "The lobby read was stopped."
                        : "Steam did not answer in time.");
                }
            }

            string json = output.GetAwaiter().GetResult();
            string problem = errors.GetAwaiter().GetResult();

            return FromJson(json)
                ?? MeadowLobbyList.Refused(
                    "The lobby read ended without an answer"
                    + (process.ExitCode == 0 ? "" : $" (exit code {process.ExitCode})")
                    + (string.IsNullOrWhiteSpace(problem) ? "." : ": " + problem.Trim()));
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
        }
    }
}
