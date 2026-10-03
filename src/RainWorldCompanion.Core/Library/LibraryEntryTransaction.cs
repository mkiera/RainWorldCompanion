using System.Text.Json;
using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.System;

namespace RainWorldCompanion.Core.Library;

internal sealed class LibraryEntryTransaction : IDisposable
{
    internal const string PendingFolderName = ".update.pending";
    private const string PreparingFolderName = ".update.preparing";
    private const string CompletedFolderName = ".update.completed";
    private const string InventoryFileName = "files.json";
    private static readonly string[] ContentNames =
    [
        LibraryEntry.SaveFileName,
        LibraryEntry.CampaignFileName,
        LibraryEntry.PreviousSaveFileName,
        LibraryEntry.PreviousCampaignFileName,
    ];
    private static readonly string[] ConfigNames = [LibraryEntry.ConfigsFolderName, LibraryEntry.PreviousConfigsFolderName];
    private readonly string _directory;
    private readonly FileStream _lease;
    private bool _committed;

    private LibraryEntryTransaction(string directory, FileStream lease)
    {
        _directory = directory;
        _lease = lease;
    }

    internal static LibraryEntryTransaction Begin(string directory)
    {
        EnsureNotLink(directory);
        var lease = Acquire(directory);
        try
        {
            RecoverLocked(directory);
            DeleteDirectory(Path.Combine(directory, CompletedFolderName));
            var preparing = Path.Combine(directory, PreparingFolderName);
            DeleteDirectory(preparing);
            Directory.CreateDirectory(preparing);
            var inventory = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in ManagedFiles(directory))
            {
                var relative = Path.GetRelativePath(directory, path);
                var destination = Path.Combine(preparing, relative);
                CopyDurably(path, destination);
                inventory.Add(relative, Hashing.ComputeFileSha256(destination));
            }

            var inventoryPath = Path.Combine(preparing, InventoryFileName);
            File.WriteAllText(inventoryPath, JsonSerializer.Serialize(inventory));
            Flush(inventoryPath);
            Directory.Move(preparing, Path.Combine(directory, PendingFolderName));
            return new LibraryEntryTransaction(directory, lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    internal void Commit()
    {
        foreach (var path in ManagedFiles(_directory))
        {
            Flush(path);
        }

        Directory.Move(Path.Combine(_directory, PendingFolderName), Path.Combine(_directory, CompletedFolderName));
        _committed = true;
        TryDeleteCompleted(_directory);
    }

    public void Dispose()
    {
        try
        {
            if (!_committed)
            {
                RecoverLocked(_directory);
            }
        }
        finally
        {
            _lease.Dispose();
        }
    }

    internal static void Recover(string directory)
    {
        if (!Directory.Exists(Path.Combine(directory, PendingFolderName)))
        {
            return;
        }

        using var lease = Acquire(directory);
        RecoverLocked(directory);
    }

    private static FileStream Acquire(string directory)
    {
        var path = Path.Combine(directory, ".update.lock");
        EnsureNotLink(path);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private static void RecoverLocked(string directory)
    {
        var pending = Path.Combine(directory, PendingFolderName);
        if (!Directory.Exists(pending))
        {
            return;
        }

        EnsureNotLink(pending);
        var inventory = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(pending, InventoryFileName)))
            ?? throw new IOException("The pending library update has no recovery record. Its saved files were kept.");
        var files = ManagedFiles(pending).ToArray();
        if (files.Length != inventory.Count
            || !inventory.ContainsKey(LibraryEntry.ManifestFileName)
            || files.Any(path => !inventory.TryGetValue(Path.GetRelativePath(pending, path), out var hash)
                                 || !Hashing.FileMatchesHash(path, hash)))
        {
            throw new IOException("The pending library update failed its recovery checksum check. Its saved files were kept.");
        }

        foreach (var name in ContentNames)
        {
            RestoreFile(Path.Combine(pending, name), Path.Combine(directory, name));
        }

        foreach (var name in ConfigNames)
        {
            RestoreDirectory(Path.Combine(pending, name), Path.Combine(directory, name));
        }

        RestoreFile(Path.Combine(pending, LibraryEntry.ManifestFileName), Path.Combine(directory, LibraryEntry.ManifestFileName));
        DeleteDirectory(Path.Combine(directory, CompletedFolderName));
        Directory.Move(pending, Path.Combine(directory, CompletedFolderName));
        TryDeleteCompleted(directory);
    }

    private static IEnumerable<string> ManagedFiles(string directory)
    {
        foreach (var name in ContentNames.Append(LibraryEntry.ManifestFileName))
        {
            var path = Path.Combine(directory, name);
            EnsureNotLink(path);
            if (File.Exists(path))
            {
                yield return path;
            }
        }

        foreach (var name in ConfigNames)
        {
            foreach (var path in FilesBelow(Path.Combine(directory, name)))
            {
                yield return path;
            }
        }
    }

    private static IEnumerable<string> FilesBelow(string directory)
    {
        EnsureNotLink(directory);
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            EnsureNotLink(path);
            if (Directory.Exists(path))
            {
                foreach (var nested in FilesBelow(path))
                {
                    yield return nested;
                }
            }
            else
            {
                yield return path;
            }
        }
    }

    private static void RestoreFile(string source, string destination)
    {
        EnsureNotLink(destination);
        if (!File.Exists(source))
        {
            File.Delete(destination);
            return;
        }

        if (Hashing.FilesMatch(source, destination))
        {
            return;
        }

        var temporary = destination + ".recovering";
        CopyDurably(source, temporary);
        File.Move(temporary, destination, overwrite: true);
    }

    private static void RestoreDirectory(string source, string destination)
    {
        var existing = FilesBelow(destination).ToArray();
        if (!Directory.Exists(source))
        {
            DeleteDirectory(destination);
            return;
        }

        foreach (var path in FilesBelow(source))
        {
            RestoreFile(path, Path.Combine(destination, Path.GetRelativePath(source, path)));
        }

        foreach (var path in existing)
        {
            if (!File.Exists(Path.Combine(source, Path.GetRelativePath(destination, path))))
            {
                File.Delete(path);
            }
        }
    }

    private static void CopyDurably(string source, string destination)
    {
        EnsureNotLink(source);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        EnsureNotLink(destination);
        File.Copy(source, destination, overwrite: true);
        Flush(destination);
    }

    private static void Flush(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
        stream.Flush(flushToDisk: true);
    }

    private static void EnsureNotLink(string path)
    {
        if (CanonicalPath.IsLink(path))
        {
            throw new IOException("Library updates cannot replace linked files or folders.");
        }
    }

    private static void DeleteDirectory(string path)
    {
        EnsureNotLink(path);
        if (Directory.Exists(path))
        {
            _ = FilesBelow(path).ToArray();
            Directory.Delete(path, recursive: true);
        }
    }

    private static void TryDeleteCompleted(string directory)
    {
        try
        {
            DeleteDirectory(Path.Combine(directory, CompletedFolderName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
