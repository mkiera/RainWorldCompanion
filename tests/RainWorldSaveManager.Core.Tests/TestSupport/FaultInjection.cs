using System.Diagnostics;

using RainWorldSaveManager.Core.Backups;

namespace RainWorldSaveManager.Tests;

/// <summary>
/// Reports progress synchronously like <see cref="CollectingProgress"/>, and runs an action when
/// a message matches.
///
/// The races these tests need to reproduce (a save file truncated by Steam Cloud between the
/// measurement and the copy, the game launched during a restore) all happen at a point the
/// service announces through its progress reporter. Driving them from the reporter makes them
/// exact instead of timing dependent.
/// </summary>
internal sealed class ProgressHook : IProgress<string>
{
    private readonly Func<string, bool> _matches;
    private readonly Action<int> _act;
    private readonly int _limit;
    private readonly List<string> _messages = new();
    private readonly object _gate = new();

    private int _fired;

    public ProgressHook(Func<string, bool> matches, Action<int> act, int limit = int.MaxValue)
    {
        _matches = matches;
        _act = act;
        _limit = limit;
    }

    public static ProgressHook On(string messagePrefix, Action<int> act, int limit = int.MaxValue)
        => new(message => message.StartsWith(messagePrefix, StringComparison.OrdinalIgnoreCase), act, limit);

    public int Fired
    {
        get
        {
            lock (_gate)
            {
                return _fired;
            }
        }
    }

    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_gate)
            {
                return _messages.ToArray();
            }
        }
    }

    public void Report(string value)
    {
        int index;

        lock (_gate)
        {
            _messages.Add(value);

            if (_fired >= _limit || !_matches(value))
            {
                return;
            }

            index = _fired;
            _fired++;
        }

        _act(index);
    }
}

/// <summary>
/// A scope that fails on a chosen walk of the save folder. Used to prove that a restore which
/// falls over after the first live write still comes back as a result carrying the safety
/// snapshot, rather than throwing it away with the exception.
/// </summary>
internal sealed class FailingScope : BackupScope
{
    private readonly int _failOnCall;
    private int _calls;

    public FailingScope(string saveRoot, int failOnCall)
        : base(saveRoot)
        => _failOnCall = failOnCall;

    public override ScopeScan Scan()
    {
        _calls++;
        if (_calls == _failOnCall)
        {
            throw new DirectoryNotFoundException("the save folder went away mid-restore");
        }

        return base.Scan();
    }
}

/// <summary>
/// Creates junctions and symlinks for the tests that prove neither one lets a copy or a delete
/// leave the save folder.
/// </summary>
internal static class Links
{
    private static readonly Lazy<bool> JunctionsWork = new(ProbeJunction);
    private static readonly Lazy<bool> SymlinksWork = new(ProbeSymlink);

    public static bool DirectoryJunctionsSupported => JunctionsWork.Value;

    public static bool FileSymlinksSupported => SymlinksWork.Value;

    /// <summary>A directory junction, which needs no privilege on NTFS.</summary>
    public static bool TryCreateDirectoryJunction(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateDirectory(targetPath);

            var parent = Path.GetDirectoryName(linkPath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            var start = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = Process.Start(start);
            process!.WaitForExit(20_000);

            return Directory.Exists(linkPath)
                && File.GetAttributes(linkPath).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>A file symlink, which needs Developer Mode or an elevated token.</summary>
    public static bool TryCreateFileSymbolicLink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            return File.Exists(linkPath)
                && File.GetAttributes(linkPath).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool ProbeJunction()
    {
        using var probe = new TempDirectory("link-probe");
        return TryCreateDirectoryJunction(probe.Resolve("link"), probe.Resolve("target"));
    }

    private static bool ProbeSymlink()
    {
        using var probe = new TempDirectory("symlink-probe");
        probe.WriteText("target.txt", "probe");
        return TryCreateFileSymbolicLink(probe.Resolve("link.txt"), probe.Resolve("target.txt"));
    }
}

/// <summary>A fact that needs directory junctions, which every NTFS volume allows.</summary>
public sealed class JunctionFactAttribute : FactAttribute
{
    public JunctionFactAttribute()
    {
        if (!Links.DirectoryJunctionsSupported)
        {
            Skip = "Directory junctions could not be created here, so the link cases cannot be set up.";
        }
    }
}

/// <summary>A fact that needs file symlinks, which need Developer Mode or an elevated token.</summary>
public sealed class SymlinkFactAttribute : FactAttribute
{
    public SymlinkFactAttribute()
    {
        if (!Links.FileSymlinksSupported)
        {
            Skip = "File symlinks could not be created here, so the link cases cannot be set up.";
        }
    }
}
