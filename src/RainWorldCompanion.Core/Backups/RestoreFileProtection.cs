using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using RainWorldCompanion.Core.System;

namespace RainWorldCompanion.Core.Backups;

internal static class RestoreFileProtection
{
    private const uint GenericRead = 0x80000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint OpenExisting = 3;
    private const int FileDispositionInfo = 4;

    internal static void Copy(string saveRoot, string source, string destination,
        BackupSnapshot safety, ManifestFileEntry? captured)
    {
        RefuseLinks(saveRoot, destination);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        var existed = File.Exists(destination);
        if (existed)
        {
            ClearReadOnly(destination);
        }

        using var output = new FileStream(destination, existed ? FileMode.Open : FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None);
        if (existed)
        {
            VerifyCaptured(output, safety, captured, destination);
        }

        output.Position = 0;
        input.CopyTo(output);
        output.SetLength(output.Position);
        output.Flush(flushToDisk: true);
        File.SetLastWriteTimeUtc(output.SafeFileHandle, File.GetLastWriteTimeUtc(input.SafeFileHandle));
    }

    internal static void Delete(string saveRoot, string path, BackupSnapshot safety, ManifestFileEntry? captured)
    {
        RefuseLinks(saveRoot, path);
        if (captured is null)
        {
            throw Changed(path);
        }

        ClearReadOnly(path);
        if (!OperatingSystem.IsWindows())
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            VerifyCaptured(file, safety, captured, path);
            File.Delete(path);
            return;
        }

        using var handle = CreateFileW(path, GenericRead | DeleteAccess, 0, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new IOException($"Could not open {path} for deletion.", new Win32Exception(Marshal.GetLastWin32Error()));
        }

        using var locked = new FileStream(handle, FileAccess.Read);
        VerifyCaptured(locked, safety, captured, path);
        byte delete = 1;
        if (!SetFileInformationByHandle(handle, FileDispositionInfo, ref delete, 1))
        {
            throw new IOException($"Could not remove {path}.", new Win32Exception(Marshal.GetLastWin32Error()));
        }
    }

    private static void VerifyCaptured(FileStream current, BackupSnapshot safety, ManifestFileEntry? captured, string path)
    {
        if (captured is null
            || current.Length != captured.SizeBytes
            || !string.Equals(Hashing.ComputeSha256(current), captured.Sha256, StringComparison.OrdinalIgnoreCase)
            || !Hashing.FileMatchesHash(Path.Combine(safety.DirectoryPath, captured.RelativePath), captured.Sha256))
        {
            throw Changed(path);
        }
    }

    private static IOException Changed(string path) => new(
        $"{Path.GetFileName(path)} appeared or changed after the safety copy, or its safety copy is unavailable. Its current contents were left in place. Try restoring again after synchronization finishes.");

    private static void RefuseLinks(string root, string path)
    {
        if (CanonicalPath.LeadsThroughLink(root, path))
        {
            throw new IOException($"{path} is a link and cannot be changed by restore.");
        }
    }

    private static void ClearReadOnly(string path)
    {
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReadOnly))
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share,
        IntPtr securityAttributes, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass,
        ref byte information, uint size);
}
