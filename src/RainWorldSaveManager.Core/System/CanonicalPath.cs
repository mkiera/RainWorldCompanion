// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RainWorldSaveManager.Core.System;

/// <summary>
/// Path comparisons that ask the filesystem rather than the string.
///
/// Path.GetFullPath is purely textual: it does not follow a junction, expand an 8.3 short name,
/// resolve a subst drive, or strip a \\?\ prefix. Every one of those is a second name for a
/// folder, so a containment test written on GetFullPath alone can be walked around by naming
/// the same folder a different way. The checks here open the path and ask Windows for the name
/// it resolves to, and fall back to the textual form when the path does not exist yet.
/// </summary>
public static class CanonicalPath
{
    private const uint FileShareAll = 0x00000001 | 0x00000002 | 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const int MaxResolvedLength = 32 * 1024;

    /// <summary>
    /// The name Windows resolves a path to, with trailing separators removed. Junctions,
    /// symlinks, short names, subst drives and \\?\ prefixes all collapse to the same answer.
    /// A path that does not exist, or that cannot be opened, falls back to
    /// <see cref="Path.GetFullPath(string)"/>, which is still the right answer for a folder
    /// that is about to be created.
    /// </summary>
    public static string Resolve(string path)
    {
        var textual = Trim(Path.GetFullPath(path));

        if (!OperatingSystem.IsWindows())
        {
            return textual;
        }

        try
        {
            var resolved = FinalPath(textual);
            return resolved is null ? textual : Trim(resolved);
        }
        catch (Exception)
        {
            return textual;
        }
    }

    /// <summary>
    /// True when <paramref name="candidate"/> resolves to a location strictly inside
    /// <paramref name="container"/>. Both sides go through <see cref="Resolve"/> first.
    /// </summary>
    public static bool IsInside(string container, string candidate)
        => IsInsideResolved(Resolve(container), Resolve(candidate));

    /// <summary>Separator-aware prefix test over two already resolved paths.</summary>
    public static bool IsInsideResolved(string container, string candidate)
    {
        if (candidate.Length <= container.Length)
        {
            return false;
        }

        if (!candidate.StartsWith(container, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var boundary = candidate[container.Length];
        return boundary == Path.DirectorySeparatorChar || boundary == Path.AltDirectorySeparatorChar;
    }

    /// <summary>
    /// True when the path itself is a junction, symlink or other reparse point. A path that
    /// does not exist is not a reparse point.
    /// </summary>
    public static bool IsLink(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception)
        {
            // Anything else means the answer is unknown, and an unknown link is treated as one
            // so a caller refuses rather than writes.
            return true;
        }
    }

    /// <summary>
    /// True when writing to <paramref name="candidate"/> could land somewhere other than where
    /// its text says: either the file itself is a link, or one of the folders between the root
    /// and it is. Existing entries only, because a path that is not there yet cannot redirect a
    /// write.
    /// </summary>
    public static bool LeadsThroughLink(string root, string candidate)
    {
        var rootFull = Trim(Path.GetFullPath(root));
        var candidateFull = Trim(Path.GetFullPath(candidate));

        if (!IsInsideResolved(rootFull, candidateFull))
        {
            return true;
        }

        // Walk the chain from the root down. Any reparse point on the way can send the write
        // somewhere else, whatever the text of the path says.
        var relative = candidateFull.Substring(rootFull.Length).TrimStart(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var segments = relative.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        var walked = rootFull;
        foreach (var segment in segments)
        {
            walked = Path.Combine(walked, segment);
            if (IsLink(walked))
            {
                return true;
            }
        }

        return false;
    }

    private static string Trim(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string? FinalPath(string path)
    {
        using var handle = CreateFileW(
            path,
            dwDesiredAccess: 0,
            FileShareAll,
            lpSecurityAttributes: IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            hTemplateFile: IntPtr.Zero);

        if (handle.IsInvalid)
        {
            return null;
        }

        var buffer = new char[MaxResolvedLength];
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length)
        {
            return null;
        }

        return StripPrefix(new string(buffer, 0, (int)length));
    }

    /// <summary>
    /// GetFinalPathNameByHandle always answers in the \\?\ form. Both prefixes are removed so
    /// the result compares against ordinary paths.
    /// </summary>
    private static string StripPrefix(string path)
    {
        const string UncPrefix = @"\\?\UNC\";
        const string DevicePrefix = @"\\?\";

        if (path.StartsWith(UncPrefix, StringComparison.Ordinal))
        {
            return @"\\" + path.Substring(UncPrefix.Length);
        }

        return path.StartsWith(DevicePrefix, StringComparison.Ordinal)
            ? path.Substring(DevicePrefix.Length)
            : path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle hFile,
        [Out] char[] lpszFilePath,
        uint cchFilePath,
        uint dwFlags);
}
