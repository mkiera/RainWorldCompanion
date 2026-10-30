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
        // turned off. Only the bin's item count shows that, so a copy is kept to put the item back.
        var before = info.i64NumItems;
        var keep = Path.Combine(Path.GetTempPath(), "RainWorldCompanion-recycle", Guid.NewGuid().ToString("N"));
        CopyItem(full, keep);
        var keepCopy = false;
        try
        {
            var operation = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = full + "\0\0",
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT,
            };

            int result = SHFileOperation(ref operation);
            if (result != 0 || operation.fAnyOperationsAborted)
            {
                throw new IOException(
                    $"{Path.GetFileName(full)} could not be moved to the Recycle Bin, so it was not deleted (code 0x{result:X}).");
            }

            if (File.Exists(full) || Directory.Exists(full))
            {
                throw new IOException($"{Path.GetFileName(full)} is still there after being sent to the Recycle Bin.");
            }

            var after = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            if (SHQueryRecycleBin(Path.GetPathRoot(full)!, ref after) == 0 && after.i64NumItems <= before)
            {
                try
                {
                    CopyItem(keep, full);
                }
                catch (Exception e)
                {
                    keepCopy = true;
                    throw new IOException(
                        $"{Path.GetFileName(full)} was deleted instead of recycled and could not be put back. A copy is at {keep}.", e);
                }

                throw new IOException(
                    $"{Path.GetFileName(full)} was not deleted, because Windows would not keep it in the Recycle Bin. "
                    + "It may be too big for the Recycle Bin, or the Recycle Bin may be turned off for its drive.");
            }
        }
        finally
        {
            try
            {
                if (!keepCopy && Directory.Exists(keep)) Directory.Delete(keep, recursive: true);
                else if (!keepCopy && File.Exists(keep)) File.Delete(keep);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static void CopyItem(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(source))
        {
            File.Copy(source, destination);
            return;
        }

        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
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
