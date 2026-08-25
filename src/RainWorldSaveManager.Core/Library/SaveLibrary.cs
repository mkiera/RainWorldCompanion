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

        // Newest content first, so a save that was just updated with an hour of play moves to the
        // top rather than sitting wherever it was first stored.
        entries.Sort(static (a, b) => b.ModifiedUtc.CompareTo(a.ModifiedUtc));
        return entries;
    }

    /// <summary>
    /// What loading an entry onto a slot would do, worked out without changing anything.
    ///
    /// The entry's own bytes are checked here rather than only at the moment of the write, so a
    /// damaged save is reported in the dialog instead of after the user has agreed to overwrite
    /// something with it.
    /// </summary>
    public LibraryLoadPlan PlanLoad(LibraryEntry entry, SaveSlotRef target)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(target);

        var problems = new List<string>();
        var warnings = new List<string>();

        var side = _backups.SlotCopies.ReadSide(target);

        if (!target.IsRealSlot)
        {
            problems.Add(string.Format(
                CultureInfo.InvariantCulture,
                "Slot {0} is not a Rain World slot. The game has slots {1} to {2}.",
                target.Slot,
                SaveSlotRef.MinSlot,
                SaveSlotRef.MaxSlot));

            return new LibraryLoadPlan(entry, side, problems, warnings);
        }

        if (_gameDetector.IsGameRunning(out var processName))
        {
            problems.Add($"Rain World is running (process \"{processName ?? "Rain World"}\"). Close the game before loading a save.");
        }

        if (entry.Manifest is not { } manifest)
        {
            problems.Add($"\"{entry.Name}\" did not finish being stored ({entry.Problem}), so it cannot be loaded.");
            return new LibraryLoadPlan(entry, side, problems, warnings);
        }

        var verification = VerifyEntry(entry);
        if (!verification.Ok)
        {
            problems.Add($"\"{entry.Name}\" failed its checksum check, so it will not be written to a slot.");
            problems.AddRange(verification.Problems);
        }

        // The target has to be a file the backup scope covers, because the safety snapshot taken
        // before the load is what makes overwriting it undoable. A target outside the scope would
        // be written over with no copy of it anywhere.
        if (!_backups.Scope.IsInScope(side.FileName))
        {
            problems.Add($"{side.FileName} is not one of the files this app manages, so it will not be written to.");
        }
        else if (CanonicalPath.LeadsThroughLink(SaveRoot, side.FullPath))
        {
            problems.Add($"{side.FileName} is a link, so writing to it would land outside the save folder.");
        }

        if (manifest.Metadata?.ParseError is { } parseError)
        {
            warnings.Add($"\"{entry.Name}\" cannot be read by this app ({parseError}). It will still be loaded exactly as it is.");
        }

        if (manifest.Metadata?.ChecksumValid == false)
        {
            warnings.Add($"\"{entry.Name}\" has a checksum the game will reject, and loading it does not repair that.");
        }

        if (side.Exists && side.Metadata?.ParseError is { } targetError)
        {
            warnings.Add($"{side.FileName} cannot be read by this app ({targetError}), so what is about to be replaced cannot be described.");
        }

        var entryCampaigns = manifest.Metadata?.Campaigns.Count ?? 0;
        var targetCampaigns = side.Metadata?.Campaigns.Count ?? 0;

        if (entryCampaigns == 0 && targetCampaigns > 0)
        {
            // A save with records but no SAVE STATE is not an empty file. It is a Rain Meadow
            // online save holding the explored map and the progression record, and the game will
            // still show the slot as having no campaign in it.
            warnings.Add(manifest.Metadata?.RecordCount > 0
                ? $"\"{entry.Name}\" holds no campaign, only map and progression data, so this leaves {side.FileName} with no campaign in it."
                : $"\"{entry.Name}\" holds no campaign, so this replaces {side.FileName} with an empty slot.");
        }

        return new LibraryLoadPlan(entry, side, problems, warnings);
    }

    /// <summary>
    /// Writes an entry's save over a live slot.
    ///
    /// This is the one thing in the library that touches the save folder, and it runs the same
    /// ladder a slot copy runs: safety snapshot first, proved to hold the file being replaced, then
    /// the operation lock, then the re-checks, then one copy. The entry's recorded digest goes down
    /// with it, so a save that was damaged since it was stored is refused under the lock rather
    /// than written.
    /// </summary>
    public LibraryLoadResult LoadEntry(
        LibraryEntry entry,
        SaveSlotRef target,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(target);

        var errors = new List<string>();

        EnsureGameNotRunning();

        var plan = PlanLoad(entry, target);
        var warnings = new List<string>(plan.Warnings);

        if (!plan.CanLoad)
        {
            errors.AddRange(plan.Problems);
            return new LibraryLoadResult(false, null, errors, warnings, false, 0, plan);
        }

        ct.ThrowIfCancellationRequested();

        var manifest = entry.Manifest!;
        var quotedName = "\"" + entry.Name + "\"";

        var job = new SlotWriteJob(
            plan.Target,
            entry.SavePath,
            quotedName,
            manifest.Sha256,
            OperationNoun: "load",
            ProgressVerb: "Loading",
            SafetyLabel: $"Before loading {quotedName} into {plan.Target.FileName}",
            SafetyNote: targetExists => targetExists
                ? $"Automatic copy taken before the library save {quotedName} was loaded over {plan.Target.FileName} ({plan.Target.Describe()})."
                : $"Automatic copy taken before the library save {quotedName} was loaded into {plan.Target.FileName}, which did not exist yet.");

        var outcome = _backups.SlotCopies.CopyOntoSlot(job, progress, ct);

        errors.AddRange(outcome.Errors);
        warnings.AddRange(outcome.Warnings);

        var success = outcome.Success && errors.Count == 0;

        if (success)
        {
            RecordSlotLink(entry, manifest, plan.Target.FullPath, target);
        }

        return new LibraryLoadResult(
            success,
            outcome.SafetySnapshot,
            errors,
            warnings,
            outcome.LiveFolderModified,
            outcome.BytesCopied,
            plan);
    }

    /// <summary>
    /// Records that this entry and the slot file now hold the same bytes, and takes the slot away
    /// from whichever entry claimed it before.
    ///
    /// One slot, one claimant. Without the second half, an entry loaded into sav a week ago would
    /// still say it is in sav after another entry replaced it there, and the row would report a
    /// played slot rather than one holding a different save entirely.
    ///
    /// Best effort on purpose. The bytes are already where they belong by this point, and a manifest
    /// that could not be rewritten costs a hint on a row. Turning that into a reported failure would
    /// tell the user their load did not work when it did.
    /// </summary>
    private void RecordSlotLink(LibraryEntry entry, LibraryManifest manifest, string slotPath, SaveSlotRef slot)
    {
        try
        {
            var info = new FileInfo(slotPath);

            manifest.LastLoadedRealm = slot.Realm;
            manifest.LastLoadedSlot = slot.Slot;
            manifest.LastLoadedUtc = DateTime.UtcNow;
            manifest.LastLoadedSizeBytes = info.Exists ? info.Length : null;
            manifest.LastLoadedWriteUtc = info.Exists ? info.LastWriteTimeUtc : null;

            WriteManifest(entry.DirectoryPath, manifest);
        }
        catch (Exception)
        {
        }

        ReleaseSlot(slot, exceptEntryId: entry.Id);
    }

    /// <summary>
    /// Forgets that any entry is in this slot. Call it when something other than a library load
    /// writes to the slot, such as a restore or a slot copy, because whatever was there is not the
    /// entry that claimed it any more.
    /// </summary>
    public void ReleaseSlot(SaveSlotRef slot) => ReleaseSlot(slot, exceptEntryId: null);

    /// <summary>Forgets every slot claim, which is what a whole folder restore invalidates.</summary>
    public void ReleaseAllSlots()
    {
        foreach (var entry in ListEntries())
        {
            if (entry.Manifest is { LastLoadedRealm: not null } manifest)
            {
                ClearSlotLink(entry, manifest);
            }
        }
    }

    private void ReleaseSlot(SaveSlotRef slot, string? exceptEntryId)
    {
        ArgumentNullException.ThrowIfNull(slot);

        foreach (var entry in ListEntries())
        {
            if (string.Equals(entry.Id, exceptEntryId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (entry.Manifest is { } manifest
                && manifest.LastLoadedRealm == slot.Realm
                && manifest.LastLoadedSlot == slot.Slot)
            {
                ClearSlotLink(entry, manifest);
            }
        }
    }

    private static void ClearSlotLink(LibraryEntry entry, LibraryManifest manifest)
    {
        try
        {
            manifest.LastLoadedRealm = null;
            manifest.LastLoadedSlot = null;
            manifest.LastLoadedUtc = null;
            manifest.LastLoadedSizeBytes = null;
            manifest.LastLoadedWriteUtc = null;

            WriteManifest(entry.DirectoryPath, manifest);
        }
        catch (Exception)
        {
        }
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
        manifest.UpdatedUtc = DateTime.UtcNow;

        WriteManifest(entry.DirectoryPath, manifest);

        // The entry now holds exactly what the slot holds, so the link is new again. Skipping this
        // left a row reading "changed since" about the very slot it had just been brought level
        // with, and no amount of updating would clear it.
        RecordSlotLink(entry, manifest, sourcePath, source);

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

        manifest.UpdatedUtc = DateTime.UtcNow;

        // The entry now holds the older save again, which is not what any slot holds, so it is in
        // no slot until it is loaded somewhere.
        manifest.LastLoadedRealm = null;
        manifest.LastLoadedSlot = null;
        manifest.LastLoadedUtc = null;
        manifest.LastLoadedSizeBytes = null;
        manifest.LastLoadedWriteUtc = null;

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
    /// Writes an entry out as a single .rwsave file, which is what gets sent to someone else or
    /// carried to another machine.
    /// </summary>
    public void ExportEntry(LibraryEntry entry, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new ArgumentException("An export needs somewhere to write to.", nameof(destinationPath));
        }

        if (entry.Manifest is not { } manifest)
        {
            throw new InvalidOperationException(
                $"\"{entry.Name}\" did not finish being stored ({entry.Problem}), so it cannot be exported.");
        }

        var verification = VerifyEntry(entry);
        if (!verification.Ok)
        {
            throw new IOException(
                $"\"{entry.Name}\" failed its checksum check, so it was not exported: " + string.Join("; ", verification.Problems));
        }

        SaveBundle.Write(Path.GetFullPath(destinationPath), manifest, entry.SavePath);
    }

    /// <summary>
    /// Reads a .rwsave bundle, or a bare save file, into a new entry.
    ///
    /// An import never writes into the save folder. It lands in the library and is loaded from
    /// there like anything else, which keeps the guarded write path the only way a file that
    /// arrived from outside reaches a live slot.
    /// </summary>
    public LibraryImportResult ImportFile(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return new LibraryImportResult(null, Array.Empty<string>(), new[] { "No file was chosen." });
        }

        var full = Path.GetFullPath(sourcePath.Trim());
        var warnings = new List<string>();

        if (!File.Exists(full))
        {
            return new LibraryImportResult(null, warnings, new[] { $"{Path.GetFileName(full)} is not there." });
        }

        var kind = SaveBundle.Sniff(full);
        if (kind == BundleKind.Unknown)
        {
            return new LibraryImportResult(null, warnings, new[]
            {
                $"{Path.GetFileName(full)} is not a Rain World save or a {SaveBundle.Extension} file.",
            });
        }

        var directory = TimestampedFolders.Create(LibraryRoot, LibraryEntry.ClaimFileName, "library folder");

        try
        {
            var manifest = kind == BundleKind.Bundle
                ? ImportBundle(full, directory)
                : ImportBareContainer(full, directory, warnings);

            manifest.SchemaVersion = LibraryManifest.CurrentSchemaVersion;

            // The load history belonged to whoever exported it and says nothing about this machine.
            manifest.LastLoadedRealm = null;
            manifest.LastLoadedSlot = null;
            manifest.LastLoadedUtc = null;
            manifest.LastLoadedSizeBytes = null;
            manifest.LastLoadedWriteUtc = null;

            // The bundle carries one save, so there is no earlier generation to go back to here.
            manifest.PreviousSha256 = null;
            manifest.PreviousSizeBytes = null;
            manifest.PreviousReplacedUtc = null;
            manifest.PreviousMetadata = null;

            TimestampedFolders.ReleaseClaim(directory, LibraryEntry.ClaimFileName);
            WriteManifest(directory, manifest);

            return new LibraryImportResult(LibraryEntry.Load(directory), warnings, Array.Empty<string>());
        }
        catch (Exception ex)
        {
            TryDeleteDirectory(directory);
            return new LibraryImportResult(null, warnings, new[] { ex.Message });
        }
    }

    private static LibraryManifest ImportBundle(string sourcePath, string directory)
    {
        var manifest = SaveBundle.Extract(sourcePath, directory);

        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            manifest.Name = Path.GetFileNameWithoutExtension(sourcePath);
        }

        return manifest;
    }

    /// <summary>
    /// Takes a save file straight out of somebody's save folder. There is no recorded hash to check
    /// it against, so unlike a bundle a damaged one is imported with a warning rather than refused:
    /// getting a broken save into the library is how somebody looks at what is left of it.
    /// </summary>
    private LibraryManifest ImportBareContainer(string sourcePath, string directory, List<string> warnings)
    {
        var savePath = Path.Combine(directory, LibraryEntry.SaveFileName);
        var fileName = Path.GetFileName(sourcePath);

        var copied = CopyProving(sourcePath, savePath, fileName, progress: null);

        var slot = SaveSlotRef.ForFileName(fileName);
        var realm = slot?.Realm ?? SaveSlotRef.RealmForFileName(fileName);
        var metadata = SaveMetadataExtractor.Extract(savePath, slot?.Slot ?? 0, realm);

        if (metadata.ParseError is { } parseError)
        {
            warnings.Add($"{fileName} could not be read by this app ({parseError}). It was imported exactly as it is.");
        }
        else if (metadata.ChecksumValid == false)
        {
            warnings.Add($"{fileName} has a checksum the game will reject. It was imported exactly as it is, and importing does not repair that.");
        }

        return new LibraryManifest
        {
            SchemaVersion = LibraryManifest.CurrentSchemaVersion,
            Name = Path.GetFileNameWithoutExtension(fileName) is { Length: > 0 } stem ? stem : fileName,
            Note = null,
            CreatedUtc = DateTime.UtcNow,
            AppVersion = _appVersion,
            SourceFileName = fileName,
            SourceRealm = realm,
            SourceSlot = slot?.Slot ?? 0,
            SizeBytes = copied.SizeBytes,
            Sha256 = copied.Sha256,
            Metadata = metadata,
        };
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

    /// <summary>Clears away a folder an import claimed and then could not fill.</summary>
    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception)
        {
            // A folder left behind has no entry.json, so it lists as an import that did not finish
            // rather than as a save the user can act on.
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
