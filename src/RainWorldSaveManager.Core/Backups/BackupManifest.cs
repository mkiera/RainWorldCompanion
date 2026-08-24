using System.Text.Json;
using System.Text.Json.Serialization;
using RainWorldSaveManager.Core.Saves.Models;

namespace RainWorldSaveManager.Core.Backups;

/// <summary>
/// Why a snapshot was taken. <see cref="PreRestoreSafety"/> snapshots are made automatically
/// just before a restore overwrites the live folder.
/// </summary>
public enum BackupKind
{
    Manual,
    PreRestoreSafety,
}

/// <summary>
/// One file recorded in a snapshot. The hash is of the copy inside the snapshot folder.
/// </summary>
public sealed record ManifestFileEntry(string RelativePath, long SizeBytes, string Sha256, DateTime LastWriteUtc);

/// <summary>
/// The contents of manifest.json. Written last, so its presence marks a finished snapshot.
/// </summary>
public sealed class BackupManifest
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string AppVersion { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public string? Label { get; set; }
    public string? Note { get; set; }
    public BackupKind Kind { get; set; }
    public List<ManifestFileEntry> Files { get; set; } = new();
    public List<SlotMetadata> Slots { get; set; } = new();

    /// <summary>
    /// In-scope paths that were passed over because they are junctions or symlinks. A snapshot
    /// with entries here does not hold everything the scope names, and says so.
    /// </summary>
    public List<string> SkippedLinks { get; set; } = new();
}

/// <summary>
/// Serialiser settings for manifest.json. Unknown members are skipped so a manifest written by
/// a later schema version still loads well enough to be listed and read.
/// </summary>
public static class BackupJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>
/// One backup folder on disk, plus whatever could be learned about it. Reading a snapshot never
/// throws: a folder with a missing or broken manifest still describes itself so the UI can show
/// it and the user can delete it.
/// </summary>
public sealed class BackupSnapshot
{
    public const string ManifestFileName = "manifest.json";

    public BackupSnapshot(
        string directoryPath,
        BackupManifest? manifest,
        string? problem,
        long totalSizeBytes,
        DateTime createdUtc)
    {
        DirectoryPath = Path.GetFullPath(directoryPath);
        Id = Path.GetFileName(DirectoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        Manifest = manifest;
        Problem = problem;
        TotalSizeBytes = totalSizeBytes;
        CreatedUtc = createdUtc;
    }

    /// <summary>The snapshot folder name, for example 2026-08-24_19-31-07.</summary>
    public string Id { get; }

    public string DirectoryPath { get; }

    /// <summary>Null when manifest.json is missing or could not be deserialised.</summary>
    public BackupManifest? Manifest { get; }

    public bool IsComplete => Manifest is not null;

    /// <summary>Set when the snapshot is unusable, explaining why.</summary>
    public string? Problem { get; }

    public long TotalSizeBytes { get; }

    /// <summary>From the manifest when there is one, otherwise the folder's creation time.</summary>
    public DateTime CreatedUtc { get; }

    public string? Label => Manifest?.Label;

    public BackupKind Kind => Manifest?.Kind ?? BackupKind.Manual;

    public string ManifestPath => Path.Combine(DirectoryPath, ManifestFileName);

    /// <summary>
    /// Reads a snapshot folder. Never throws.
    /// </summary>
    public static BackupSnapshot Load(string directoryPath)
    {
        var full = Path.GetFullPath(directoryPath);

        if (!Directory.Exists(full))
        {
            return new BackupSnapshot(full, null, "the backup folder no longer exists", 0, DateTime.UtcNow);
        }

        BackupManifest? manifest = null;
        string? problem = null;

        var manifestPath = Path.Combine(full, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            problem = "manifest.json is missing, so this backup did not finish";
        }
        else
        {
            try
            {
                var json = File.ReadAllText(manifestPath);
                manifest = JsonSerializer.Deserialize<BackupManifest>(json, BackupJson.Options);
                if (manifest is null)
                {
                    problem = "manifest.json is empty";
                }
            }
            catch (Exception ex)
            {
                problem = $"manifest.json could not be read: {ex.Message}";
            }
        }

        long size = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
            {
                try
                {
                    size += new FileInfo(file).Length;
                }
                catch (IOException)
                {
                }
            }
        }
        catch (Exception)
        {
            // Size is decoration. A folder that cannot be measured is still listed.
        }

        var created = manifest is not null && manifest.CreatedUtc != default
            ? manifest.CreatedUtc
            : SafeCreationTimeUtc(full);

        return new BackupSnapshot(full, manifest, problem, size, created);
    }

    private static DateTime SafeCreationTimeUtc(string path)
    {
        try
        {
            return Directory.GetCreationTimeUtc(path);
        }
        catch (Exception)
        {
            return DateTime.UtcNow;
        }
    }
}

/// <summary>
/// What a restore would do to the live save folder, worked out without changing anything.
/// </summary>
public sealed record RestorePlan(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Overwritten,
    IReadOnlyList<string> Unchanged,
    IReadOnlyList<string> Deleted);

/// <summary>
/// The outcome of a restore.
///
/// <para><see cref="Success"/> means every file in the manifest landed in the save folder and
/// hashed correctly afterwards. It is not "no message was recorded": a note that does not
/// affect the restored save data, such as an empty folder that could not be tidied away, goes
/// in <see cref="Warnings"/> and leaves the restore successful.</para>
///
/// <para><see cref="LiveFolderModified"/> is the field a caller must read before wording a
/// failure. A restore that fails after the first live write leaves the save folder part
/// restored, which is a different thing to tell the user than a restore that refused to start,
/// and only <see cref="SafetySnapshot"/> can undo it.</para>
/// </summary>
public sealed record RestoreResult(
    bool Success,
    BackupSnapshot? SafetySnapshot,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    bool LiveFolderModified)
{
    /// <summary>
    /// The line to lead a report with. The wording lives here rather than in the UI so that
    /// "nothing was changed" can never be printed over a save folder that was in fact written
    /// to, and so the sentence naming the safety snapshot is the same sentence in every caller.
    /// </summary>
    public string Headline()
    {
        if (Success)
        {
            return "Restore finished.";
        }

        if (!LiveFolderModified)
        {
            return "The restore did not finish, so nothing in the save folder was changed.";
        }

        var safety = SafetySnapshot?.Id;
        return safety is null
            ? "The restore did not finish and the save folder is part restored."
            : $"The restore did not finish and the save folder is part restored. Backup {safety} still holds the saves as they were, and restoring it puts them back.";
    }
}

/// <summary>
/// The outcome of re-hashing a snapshot against its own manifest.
/// </summary>
public sealed record VerifyResult(bool Ok, IReadOnlyList<string> Problems);

/// <summary>
/// Thrown instead of touching save files while Rain World is running.
/// </summary>
public sealed class GameRunningException : Exception
{
    public GameRunningException(string processName)
        : base($"Rain World is running (process \"{processName}\"). Close the game before backing up or restoring saves.")
    {
        ProcessName = processName;
    }

    public string ProcessName { get; }
}

/// <summary>
/// Thrown when another backup or restore already holds the backup folder. Two operations that
/// overlap can pick the same snapshot folder name and write into each other's copies, so the
/// second one is refused rather than allowed to interleave.
/// </summary>
public sealed class BackupBusyException : Exception
{
    public BackupBusyException(string backupRoot, Exception? inner = null)
        : base($"Another backup or restore is already running against {backupRoot}. " +
               "Wait for it to finish, or close the other window, and try again.", inner)
    {
        BackupRoot = backupRoot;
    }

    public string BackupRoot { get; }
}
