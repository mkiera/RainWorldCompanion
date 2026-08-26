using System.Text.Json;
using System.Text.Json.Serialization;
using RainWorldCompanion.Core.Mods;
using RainWorldCompanion.Core.Saves.Models;

namespace RainWorldCompanion.Core.Backups;

public enum BackupKind
{
    Manual,
    PreRestoreSafety,
}

/// <summary>One file in a snapshot. Sha256 is of the copy inside the snapshot folder.</summary>
public sealed record ManifestFileEntry(string RelativePath, long SizeBytes, string Sha256, DateTime LastWriteUtc);

/// <summary>manifest.json. Written last, so its presence marks a finished snapshot.</summary>
public sealed class BackupManifest
{
    /// <summary>Version 1 manifests still load: fields added since deserialise to null or empty.</summary>
    public const int CurrentSchemaVersion = 2;

    private List<ManifestFileEntry> _files = new();
    private List<SlotMetadata> _slots = new();
    private List<string> _skippedLinks = new();

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>
    /// Which <see cref="BackupScope"/> rules decided this snapshot's contents. Initialised to zero
    /// rather than the current version on purpose: a default here would make every old snapshot
    /// claim today's rules and delete under them. Read <see cref="EffectiveScopeVersion"/>.
    /// </summary>
    public int ScopeVersion { get; set; }

    [JsonIgnore]
    public int EffectiveScopeVersion => ScopeVersion > 0 ? ScopeVersion : BackupScope.OriginalScopeVersion;

    public string AppVersion { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public string? Label { get; set; }
    public string? Note { get; set; }
    public BackupKind Kind { get; set; }

    // An explicit JSON null overwrites a field initialiser, and readers walk these unguarded.
    public List<ManifestFileEntry> Files
    {
        get => _files;
        set => _files = value ?? new List<ManifestFileEntry>();
    }

    public List<SlotMetadata> Slots
    {
        get => _slots;
        set => _slots = value ?? new List<SlotMetadata>();
    }

    public List<string> SkippedLinks
    {
        get => _skippedLinks;
        set => _skippedLinks = value ?? new List<string>();
    }

    /// <summary>
    /// The mods that were on when this snapshot was taken. Null means "no list to compare", not
    /// "no mods were on", so nothing may render it as an empty list. Nothing inside
    /// <see cref="ModListSnapshot"/> may be an enum: JsonStringEnumConverter throws on a value it
    /// has not heard of, which would cost the read of the whole manifest rather than one field.
    /// </summary>
    public ModListSnapshot? Mods { get; set; }
}

public static class BackupJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,

        // A manifest from a later schema version still loads well enough to list and read.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>
/// One backup folder on disk. Reading one never throws: a folder with a missing or broken
/// manifest still describes itself, so the UI can show it and the user can delete it.
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

    public string? Problem { get; }

    public long TotalSizeBytes { get; }

    /// <summary>From the manifest when there is one, otherwise the folder's creation time.</summary>
    public DateTime CreatedUtc { get; }

    public string? Label => Manifest?.Label;

    public BackupKind Kind => Manifest?.Kind ?? BackupKind.Manual;

    public string ManifestPath => Path.Combine(DirectoryPath, ManifestFileName);

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

/// <summary>What a restore would do to the live save folder, worked out without changing anything.</summary>
public sealed record RestorePlan(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Overwritten,
    IReadOnlyList<string> Unchanged,
    IReadOnlyList<string> Deleted)
{
    /// <summary>
    /// Live files the restore leaves rather than deletes, because the snapshot's own rules never
    /// covered them. A confirmation dialog has to show these separately from <see cref="Deleted"/>.
    /// </summary>
    public IReadOnlyList<string> LeftAlone { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Files the snapshot holds but the restore skips, because an exclusion added since covers
    /// them. A stale steam_autocloud.vdf tells Steam that files it has synced are current.
    /// </summary>
    public IReadOnlyList<string> NotRestored { get; init; } = Array.Empty<string>();

    /// <summary>
    /// How the snapshot's mods differ from the machine now, or null when there was no way to look.
    /// Shown to the user, never a reason to refuse the restore.
    /// </summary>
    public ModListDiff? Mods { get; init; }
}

/// <summary>
/// The outcome of a restore. Read <see cref="LiveFolderModified"/> before wording a failure: a
/// part-restored save folder can only be undone through <see cref="SafetySnapshot"/>.
/// </summary>
public sealed record RestoreResult(
    bool Success,
    BackupSnapshot? SafetySnapshot,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    bool LiveFolderModified)
{
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

/// <summary>The outcome of re-hashing a snapshot against its own manifest.</summary>
public sealed record VerifyResult(bool Ok, IReadOnlyList<string> Problems);

public sealed class GameRunningException : Exception
{
    public GameRunningException(string processName)
        : base($"Rain World is running (process \"{processName}\"). Close the game before backing up or restoring saves.")
    {
        ProcessName = processName;
    }

    public string ProcessName { get; }
}

/// <summary>Two overlapping operations would pick the same folder name and write into each other.</summary>
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
