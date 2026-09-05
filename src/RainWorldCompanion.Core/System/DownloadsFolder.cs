using System.Runtime.InteropServices;

namespace RainWorldCompanion.Core.System;

public static class DownloadsFolder
{
    private static readonly Guid FolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    public static string GetPath()
    {
        if (OperatingSystem.IsWindows())
        {
            var folderId = FolderId;
            var result = SHGetKnownFolderPath(ref folderId, 0, IntPtr.Zero, out var pointer);
            try
            {
                if (result == 0)
                {
                    var path = Marshal.PtrToStringUni(pointer);
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        return path;
                    }
                }
            }
            finally
            {
                if (pointer != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(pointer);
                }
            }
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(
        ref Guid rfid,
        uint dwFlags,
        IntPtr hToken,
        out IntPtr ppszPath);
}
