using System.Runtime.InteropServices;

namespace RainWorldCompanion.Core.System;

public interface IRecycleBin
{
    // Throws when the file or folder could not be recycled. A missing path is not an error.
    void Recycle(string path);
}

public sealed class WindowsRecycleBin : IRecycleBin
{
    public static IRecycleBin Instance { get; } = new WindowsRecycleBin();

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    private WindowsRecycleBin()
    {
    }

    public void Recycle(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full) && !Directory.Exists(full))
        {
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The Recycle Bin is only available on Windows.");
        }

        // A drive with no Recycle Bin would delete outright under FOF_ALLOWUNDO, so it is refused here.
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        int query = SHQueryRecycleBin(Path.GetPathRoot(full)!, ref info);
        if (query != 0)
        {
            throw new IOException(
                $"{Path.GetFileName(full)} was not deleted, because its drive has no Recycle Bin (error 0x{query:X8}).");
        }

        // Windows deletes outright, without failing, an item too big for the bin or on a drive whose bin is
        // turned off. A copy is kept until the bin's own record of this path proves the item went there.
        var bin = BinFolder(full);
        var recordsBefore = Records(bin);
        var keep = Path.Combine(Path.GetTempPath(), "RainWorldCompanion-recycle", Guid.NewGuid().ToString("N"));
        CopyItem(full, keep);

        int result = -1;
        var aborted = false;
        try
        {
            var operation = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = full + "\0\0",
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT,
            };
            result = SHFileOperation(ref operation);
            aborted = operation.fAnyOperationsAborted;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            result = e.HResult;
        }

        bool recycled;
        try
        {
            recycled = result == 0 && !aborted && !File.Exists(full) && !Directory.Exists(full)
                && Records(bin).Except(recordsBefore, StringComparer.OrdinalIgnoreCase)
                    .Any(record => string.Equals(OriginalPath(record), full, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            recycled = false;
        }

        if (recycled)
        {
            DeleteQuietly(keep);
            return;
        }

        try
        {
            CopyItem(keep, full);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            throw new IOException(
                $"{Path.GetFileName(full)} could not be confirmed in the Recycle Bin and could not be put back. A copy is at {keep}.", e);
        }

        DeleteQuietly(keep);
        throw new IOException(
            $"{Path.GetFileName(full)} was not deleted, because Windows did not keep it in the Recycle Bin (code 0x{result:X}). "
            + "It may be in use or too big for the Recycle Bin, or the Recycle Bin may be turned off for its drive.");
    }

    [global::System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string BinFolder(string full) => Path.Combine(
        Path.GetPathRoot(full)!,
        "$Recycle.Bin",
        global::System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value);

    private static string[] Records(string bin) =>
        Directory.Exists(bin) ? Directory.GetFiles(bin, "$I*") : [];

    // $I records hold the original path: version 1 at offset 24 in 520 bytes, version 2 after a length at 24.
    private static string? OriginalPath(string record)
    {
        var bytes = File.ReadAllBytes(record);
        if (bytes.Length < 28)
        {
            return null;
        }

        var version = BitConverter.ToInt64(bytes, 0);
        int offset, length;
        if (version == 1)
        {
            (offset, length) = (24, Math.Min(520, bytes.Length - 24));
        }
        else if (version == 2)
        {
            var characters = BitConverter.ToInt32(bytes, 24);
            if (characters <= 0 || characters > (bytes.Length - 28) / 2)
            {
                return null;
            }

            (offset, length) = (28, characters * 2);
        }
        else
        {
            return null;
        }

        var text = global::System.Text.Encoding.Unicode.GetString(bytes, offset, length & ~1);
        return text.Split('\0')[0];
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    // Puts back only what is missing, so a partly deleted folder is completed without overwriting what stayed.
    private static void CopyItem(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(source))
        {
            if (!File.Exists(destination)) File.Copy(source, destination);
            return;
        }

        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            if (!File.Exists(target)) File.Copy(file, target);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);
}
