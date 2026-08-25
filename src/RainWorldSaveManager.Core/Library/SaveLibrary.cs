// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Globalization;
using System.Text.Json;
using RainWorldSaveManager.Core.Backups;
using RainWorldSaveManager.Core.Saves;
using RainWorldSaveManager.Core.Saves.Models;
using RainWorldSaveManager.Core.Settings;
using RainWorldSaveManager.Core.System;

namespace RainWorldSaveManager.Core.Library;

/// <summary>
/// A folder of named saves, so a player is not held to the three slots the game gives them.
///
/// The same three rules that shape the backup service shape this one. Save files are copied byte
/// for byte and never decoded or rewritten, because the UTF-8 BOM and the trailing NUL padding are
/// part of what the game reads back. Nothing destructive happens until the step before it has
/// succeeded. And every integrity check compares two independent things, never a copy against
/// itself.
///
/// Only <see cref="LoadEntry"/> writes into the live save folder, and it does so through the same
/// ladder a slot copy runs: safety snapshot first, operation lock, re-checks, then one copy proved
/// against the hash recorded when the entry was stored. Storing, renaming, deleting, importing and
/// exporting never touch the save folder at all.
/// </summary>
public sealed class SaveLibrary
{
    private static readonly StringComparer HashComparer = StringComparer.OrdinalIgnoreCase;

    private readonly BackupService _backups;
    private readonly IGameProcessDetector _gameDetector;
    private readonly string _appVersion;

    public SaveLibrary(BackupService backups, string libraryRoot, IGameProcessDetector gameDetector, string appVersion)
    {
        _backups = backups ?? throw new ArgumentNullException(nameof(backups));
        _gameDetector = gameDetector ?? throw new ArgumentNullException(nameof(gameDetector));
        _appVersion = appVersion ?? "";

        if (string.IsNullOrWhiteSpace(libraryRoot))
        {
            throw new ArgumentException("Library root must not be empty.", nameof(libraryRoot));
        }

        LibraryRoot = Path.GetFullPath(libraryRoot.Trim());

        // The settings dialog checks this too. Repeated here for the same reason BackupService
        // repeats its own pair check: a service handed a bad triple would go on to write into a
        // folder that another part of the app is allowed to delete.
        var problem = SettingsValidation.Validate(_backups.SaveRoot, _backups.BackupRoot, LibraryRoot);
        if (problem is not null)
        {
            throw new ArgumentException(problem, nameof(libraryRoot));
        }
    }

    public string LibraryRoot { get; }

    /// <summary>Where the entries are loaded from and stored to.</summary>
    public string SaveRoot => _backups.SaveRoot;

    /// <summary>
    /// Every entry folder, newest first. A folder that cannot be read is listed with a Problem
    /// rather than dropped, because a save that half arrived is still something the user has to be
    /// told about.
    /// </summary>
    public IReadOnlyList<LibraryEntry> ListEntries()
    {
        var entries = new List<LibraryEntry>();

        try
        {
            if (!Directory.Exists(LibraryRoot))
            {
                return entries;
            }

            foreach (var directory in Directory.EnumerateDirectories(LibraryRoot))
            {
                entries.Add(LibraryEntry.Load(directory));
            }
        }
        catch (Exception)
        {
            // A library root that cannot be listed shows as empty. The settings screen is where a
            // broken path is reported, not here.
            return entries;
        }

        entries.Sort(static (a, b) => b.CreatedUtc.CompareTo(a.CreatedUtc));
        return entries;
    }

    /// <summary>
    /// Copies one live slot into a new entry.
    ///
    /// Refuses while the game is running, by throwing, which is what CreateBackup and CopySlot do
    /// and means one handler covers a running game wherever it is met. The copy is proved against
    /// its source before the manifest records it, so a save the game rewrote mid-copy abandons the
    /// entry rather than being stored as though it were sound.
    /// </summary>
    public LibraryEntry StoreSlot(
        SaveSlotRef source,
        string name,
        string? note,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var trimmedName = (name ?? "").Trim();
        if (trimmedName.Length == 0)
        {
            throw new ArgumentException("A library save needs a name.", nameof(name));
        }

        EnsureGameNotRunning();
        ct.ThrowIfCancellationRequested();

        var sourcePath = ResolveSlotPath(source);

        // Reading the live folder is held against the same lock a restore takes, so a restore in
        // another window cannot rewrite the slot half way through this copy.
        using var lease = _backups.AcquireOperationLock();

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException($"{source.FileName} is not in the save folder, so there is nothing to store.", sourcePath);
        }

        if (CanonicalPath.IsLink(sourcePath))
        {
            throw new IOException($"{source.FileName} is a link, and this app copies only real files inside the save folder.");
        }

        var directory = TimestampedFolders.Create(LibraryRoot, LibraryEntry.ClaimFileName, "library folder");
        var savePath = Path.Combine(directory, LibraryEntry.SaveFileName);

        var copied = CopyProving(sourcePath, savePath, source.FileName, progress);

        progress?.Report($"Reading what is in {source.FileName}");
        var metadata = SaveMetadataExtractor.Extract(savePath, source.Slot, source.Realm);

        var manifest = new LibraryManifest
        {
            SchemaVersion = LibraryManifest.CurrentSchemaVersion,
            Name = trimmedName,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            CreatedUtc = DateTime.UtcNow,
            AppVersion = _appVersion,
            SourceFileName = source.FileName,
            SourceRealm = source.Realm,
            SourceSlot = source.Slot,
            SizeBytes = copied.SizeBytes,
            Sha256 = copied.Sha256,
            Metadata = metadata,
        };

        TimestampedFolders.ReleaseClaim(directory, LibraryEntry.ClaimFileName);
        WriteManifest(directory, manifest);

        progress?.Report("Stored");
        return LibraryEntry.Load(directory);
    }

    /// <summary>
    /// Replaces an entry's save with what is in a live slot now, which is how an hour of play gets
    /// back into the entry it came from.
    ///
    /// The bytes being replaced are kept as save.previous.bin so the update can be undone. One
    /// generation only: the next update replaces it.
    /// </summary>
    public LibraryEntry UpdateEntry(
        LibraryEntry entry,
        SaveSlotRef source,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(source);

        EnsureOwnedEntry(entry, "update");

        if (entry.Manifest is not { } manifest)
        {
            throw new InvalidOperationException(
                $"\"{entry.Name}\" did not finish being stored ({entry.Problem}), so it cannot be updated.");
        }

        EnsureGameNotRunning();
        ct.ThrowIfCancellationRequested();

        var sourcePath = ResolveSlotPath(source);

        using var lease = _backups.AcquireOperationLock();

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException($"{source.FileName} is not in the save folder, so there is nothing to update from.", sourcePath);
        }

        if (CanonicalPath.IsLink(sourcePath))
        {
            throw new IOException($"{source.FileName} is a link, and this app copies only real files inside the save folder.");
        }

        // The new bytes land beside the old ones and are proved before anything is replaced, so a
        // copy that goes wrong leaves the entry exactly as it was.
        var stagedPath = Path.Combine(entry.DirectoryPath, LibraryEntry.SaveFileName + ".tmp");
        var copied = CopyProving(sourcePath, stagedPath, source.FileName, progress);

        progress?.Report($"Reading what is in {source.FileName}");
        var metadata = SaveMetadataExtractor.Extract(stagedPath, source.Slot, source.Realm);

        File.Move(entry.SavePath, entry.PreviousSavePath, overwrite: true);
        File.Move(stagedPath, entry.SavePath, overwrite: true);

        manifest.PreviousSha256 = manifest.Sha256;
        manifest.PreviousSizeBytes = manifest.SizeBytes;
        manifest.PreviousReplacedUtc = DateTime.UtcNow;
        manifest.PreviousMetadata = manifest.Metadata;

        manifest.SizeBytes = copied.SizeBytes;
        manifest.Sha256 = copied.Sha256;
        manifest.Metadata = metadata;
        manifest.SourceFileName = source.FileName;
        manifest.SourceRealm = source.Realm;
        manifest.SourceSlot = source.Slot;

        WriteManifest(entry.DirectoryPath, manifest);

        progress?.Report("Updated");
        return LibraryEntry.Load(entry.DirectoryPath);
    }

    /// <summary>
    /// Puts back the save an update replaced. Refuses when the previous bytes are not both on disk
    /// and recorded, and proves them against the recorded hash before swapping them in.
    /// </summary>
    public LibraryEntry UndoUpdate(LibraryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        EnsureOwnedEntry(entry, "undo the update of");

        if (entry.Manifest is not { } manifest)
        {
            throw new InvalidOperationException(
                $"\"{entry.Name}\" did not finish being stored ({entry.Problem}), so there is nothing to undo.");
        }

        if (manifest.PreviousSha256 is not { Length: > 0 } previousHash || !File.Exists(entry.PreviousSavePath))
        {
            throw new InvalidOperationException($"\"{entry.Name}\" has no earlier save to go back to.");
        }

        var actual = Hashing.ComputeFileSha256(entry.PreviousSavePath);
        if (!HashComparer.Equals(actual, previousHash))
        {
            throw new IOException(
                $"The earlier save kept for \"{entry.Name}\" does not match its recorded checksum, so it was not put back.");
        }

        var metadata = manifest.PreviousMetadata;

        var stagedPath = Path.Combine(entry.DirectoryPath, LibraryEntry.SaveFileName + ".tmp");
        File.Copy(entry.PreviousSavePath, stagedPath, overwrite: true);
        File.Move(stagedPath, entry.SavePath, overwrite: true);

        manifest.SizeBytes = manifest.PreviousSizeBytes ?? new FileInfo(entry.SavePath).Length;
        manifest.Sha256 = previousHash;
        manifest.Metadata = metadata;

        manifest.PreviousSha256 = null;
        manifest.PreviousSizeBytes = null;
        manifest.PreviousReplacedUtc = null;
        manifest.PreviousMetadata = null;

        WriteManifest(entry.DirectoryPath, manifest);

        TryDelete(entry.PreviousSavePath);

        return LibraryEntry.Load(entry.DirectoryPath);
    }

    /// <summary>
    /// Changes the name and note. Rewrites entry.json and nothing else: the folder keeps its
    /// timestamp name and the save bytes are not touched.
    /// </summary>
    public LibraryEntry RenameEntry(LibraryEntry entry, string newName, string? newNote)
    {
        ArgumentNullException.ThrowIfNull(entry);

        EnsureOwnedEntry(entry, "rename");

        var trimmedName = (newName ?? "").Trim();
        if (trimmedName.Length == 0)
        {
            throw new ArgumentException("A library save needs a name.", nameof(newName));
        }

        if (entry.Manifest is not { } manifest)
        {
            throw new InvalidOperationException(
                $"\"{entry.Name}\" did not finish being stored ({entry.Problem}), so it cannot be renamed.");
        }

        manifest.Name = trimmedName;
        manifest.Note = string.IsNullOrWhiteSpace(newNote) ? null : newNote.Trim();

        WriteManifest(entry.DirectoryPath, manifest);
        return LibraryEntry.Load(entry.DirectoryPath);
    }

    /// <summary>Deletes an entry folder. Refuses anything that is not a direct child of the root.</summary>
    public void DeleteEntry(LibraryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        EnsureOwnedEntry(entry, "delete");

        if (!Directory.Exists(entry.DirectoryPath))
        {
            return;
        }

        Directory.Delete(entry.DirectoryPath, recursive: true);
    }

    /// <summary>
    /// Re-hashes the stored save against the manifest. This is what the background sweep runs, and
    /// what a load repeats before it writes anything.
    /// </summary>
    public VerifyResult VerifyEntry(LibraryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var problems = new List<string>();

        if (entry.Manifest is not { } manifest)
        {
            problems.Add(entry.Problem ?? "entry.json is missing");
            return new VerifyResult(false, problems);
        }

        FileInfo info;
        try
        {
            info = new FileInfo(entry.SavePath);
            if (!info.Exists)
            {
                problems.Add("the stored save is missing");
                return new VerifyResult(false, problems);
            }
        }
        catch (Exception ex)
        {
            problems.Add("the stored save could not be read: " + ex.Message);
            return new VerifyResult(false, problems);
        }

        if (info.Length != manifest.SizeBytes)
        {
            problems.Add($"the stored save is {info.Length} bytes and was recorded as {manifest.SizeBytes}");
        }

        try
        {
            var actual = Hashing.ComputeFileSha256(entry.SavePath);
            if (!HashComparer.Equals(actual, manifest.Sha256))
            {
                problems.Add("the stored save does not match its recorded checksum");
            }
        }
        catch (Exception ex)
        {
            problems.Add("the stored save could not be read: " + ex.Message);
        }

        return new VerifyResult(problems.Count == 0, problems);
    }

    /// <summary>
    /// Copies a file and proves the copy against what it came from, the same discipline a backup
    /// copy follows. A file that moves under the copy is copied again, and one that will not sit
    /// still throws rather than being recorded as sound.
    /// </summary>
    private static (long SizeBytes, string Sha256) CopyProving(
        string sourcePath,
        string destinationPath,
        string label,
        IProgress<string>? progress)
    {
        var info = new FileInfo(sourcePath);
        var expectedLength = info.Length;
        var expectedWrite = info.LastWriteTimeUtc;
        var problem = "it could not be read";

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            if (attempt == 2)
            {
                var refreshed = new FileInfo(sourcePath);
                if (!refreshed.Exists)
                {
                    break;
                }

                expectedLength = refreshed.Length;
                expectedWrite = refreshed.LastWriteTimeUtc;
            }

            progress?.Report(attempt == 1
                ? $"Copying {label} ({SlotCopyService.FormatSize(expectedLength)})"
                : $"Copying {label} again, it moved during the first copy");

            File.Copy(sourcePath, destinationPath, overwrite: true);

            progress?.Report($"Checking {label}");

            var source = new FileInfo(sourcePath);
            var copy = new FileInfo(destinationPath);

            if (!source.Exists)
            {
                problem = "it disappeared while it was being copied";
            }
            else if (source.Length != expectedLength || source.LastWriteTimeUtc != expectedWrite)
            {
                problem = $"it changed while it was being copied: it was {expectedLength} bytes and is now {source.Length} bytes";
            }
            else if (copy.Length != source.Length)
            {
                problem = $"the copy is {copy.Length} bytes and the file is {source.Length} bytes";
            }
            else
            {
                var sourceHash = Hashing.ComputeFileSha256(sourcePath);
                var copyHash = Hashing.ComputeFileSha256(destinationPath);

                if (HashComparer.Equals(sourceHash, copyHash))
                {
                    return (copy.Length, copyHash);
                }

                problem = "the copy does not match the file it was copied from";
            }
        }

        throw new IOException(
            $"{label} could not be copied: {problem}. Nothing was stored rather than storing a save that " +
            "cannot be shown to match. Close Steam, or wait for Steam Cloud to finish syncing, and try again.");
    }

    /// <summary>
    /// Writes entry.json through a temp file. It goes last on a new entry, so its presence is what
    /// marks the entry finished.
    /// </summary>
    private static void WriteManifest(string directory, LibraryManifest manifest)
    {
        var path = Path.Combine(directory, LibraryEntry.ManifestFileName);
        var temp = path + ".tmp";

        File.WriteAllText(temp, JsonSerializer.Serialize(manifest, BackupJson.Options));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// Refuses to act on anything that is not an entry folder directly inside the library root, and
    /// on anything inside the save folder. The same guard DeleteBackup uses, and for the same
    /// reason: a path that arrived from somewhere else is not one to write to or delete.
    /// </summary>
    private void EnsureOwnedEntry(LibraryEntry entry, string verb)
    {
        var target = TrimSeparators(Path.GetFullPath(entry.DirectoryPath));
        var root = TrimSeparators(LibraryRoot);
        var parent = Path.GetDirectoryName(target);

        if (string.IsNullOrEmpty(parent) || !HashComparer.Equals(TrimSeparators(parent), root))
        {
            throw new InvalidOperationException(
                $"Refusing to {verb} \"{target}\": it is not a library folder directly inside {root}.");
        }

        if (CanonicalPath.IsInside(SaveRoot, target))
        {
            throw new InvalidOperationException(
                $"Refusing to {verb} \"{target}\": it sits inside the save folder {SaveRoot}.");
        }
    }

    private string ResolveSlotPath(SaveSlotRef slot)
    {
        if (!slot.IsRealSlot)
        {
            throw new ArgumentException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Slot {0} is not a Rain World slot. The game has slots {1} to {2}.",
                    slot.Slot,
                    SaveSlotRef.MinSlot,
                    SaveSlotRef.MaxSlot),
                nameof(slot));
        }

        return Path.Combine(SaveRoot, slot.FileName);
    }

    private void EnsureGameNotRunning()
    {
        if (_gameDetector.IsGameRunning(out var processName))
        {
            throw new GameRunningException(string.IsNullOrWhiteSpace(processName) ? "Rain World" : processName);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // A leftover previous save costs disk space and nothing else. The manifest no longer
            // points at it, so it cannot be mistaken for one that can be restored.
        }
    }

    private static string TrimSeparators(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
