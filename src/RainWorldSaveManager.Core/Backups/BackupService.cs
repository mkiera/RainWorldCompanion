using System.Globalization;
using System.Text.Json;
using RainWorldSaveManager.Core.Saves;
using RainWorldSaveManager.Core.Saves.Models;
using RainWorldSaveManager.Core.Settings;
using RainWorldSaveManager.Core.System;

namespace RainWorldSaveManager.Core.Backups;

/// <summary>
/// Copies the in-scope save files into timestamped snapshot folders and puts them back again.
///
/// Three rules shape everything here. Save files are copied byte for byte and never decoded or
/// rewritten, because the UTF-8 BOM and the trailing NUL padding are part of what the game reads
/// back. Nothing destructive happens until the step before it has succeeded: the game must be
/// closed, the snapshot must verify, and a safety copy of the current saves must exist on disk
/// before a restore overwrites anything. And every integrity check compares two independent
/// things, never a copy against itself, so a snapshot cannot certify its own damage.
/// </summary>
public sealed class BackupService
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>Marks the backup folder as in use for the length of one operation.</summary>
    private const string LockFileName = ".operation-lock";

    /// <summary>Claims a snapshot folder name while the snapshot is being written.</summary>
    internal const string ClaimFileName = ".creating";

    /// <summary>
    /// How often the restore loop re-checks for the game. Every file would mean a full process
    /// enumeration per file, which on a large dvrmentSaveStates tree costs more than it buys.
    /// </summary>
    private const int GameCheckInterval = 16;

    private readonly IGameProcessDetector _gameDetector;
    private readonly string _appVersion;
    private readonly object _lockGate = new();

    private FileStream? _operationLock;
    private int _operationDepth;
    private SlotCopyService? _slotCopies;

    public BackupService(string saveRoot, string backupRoot, IGameProcessDetector gameDetector, string appVersion)
        : this(saveRoot, backupRoot, gameDetector, appVersion, scope: null)
    {
    }

    /// <summary>
    /// The scope may be supplied by the caller. Everything this class copies, overwrites and
    /// deletes comes from it, so a substitute scope is the whole of what the service is allowed
    /// to touch.
    /// </summary>
    internal BackupService(
        string saveRoot,
        string backupRoot,
        IGameProcessDetector gameDetector,
        string appVersion,
        BackupScope? scope)
    {
        if (string.IsNullOrWhiteSpace(saveRoot))
        {
            throw new ArgumentException("Save root must not be empty.", nameof(saveRoot));
        }

        if (string.IsNullOrWhiteSpace(backupRoot))
        {
            throw new ArgumentException("Backup root must not be empty.", nameof(backupRoot));
        }

        _gameDetector = gameDetector ?? throw new ArgumentNullException(nameof(gameDetector));
        _appVersion = appVersion ?? "";

        SaveRoot = Path.GetFullPath(saveRoot);
        BackupRoot = Path.GetFullPath(backupRoot);

        // The same check the settings screen runs. Repeating it here means the invariant holds
        // however the service was constructed, not only when the UI asked first.
        var problem = SettingsValidation.Validate(SaveRoot, BackupRoot);
        if (problem is not null)
        {
            throw new ArgumentException(problem, nameof(backupRoot));
        }

        Scope = scope ?? new BackupScope(SaveRoot);
    }

    public string SaveRoot { get; }

    public string BackupRoot { get; }

    /// <summary>The set of files this service is allowed to read, write, and delete.</summary>
    public BackupScope Scope { get; }

    /// <summary>
    /// Copies every in-scope file into a new snapshot folder. The manifest is written last, so a
    /// folder without one is a snapshot that did not finish. A partial folder is left on disk on
    /// failure: a half copy of a save is still evidence of what happened.
    ///
    /// Each copy is proved against its source before it is recorded. A file that moves under the
    /// copy is copied a second time, and a file that will not sit still abandons the whole
    /// snapshot rather than being written into the manifest as if it were sound.
    /// </summary>
    public BackupSnapshot CreateBackup(
        string? label,
        string? note,
        BackupKind kind = BackupKind.Manual,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        EnsureGameNotRunning();
        ct.ThrowIfCancellationRequested();

        using var lease = AcquireOperationLock();

        var scan = Scope.Scan();
        var directory = CreateSnapshotDirectory();

        var manifest = new BackupManifest
        {
            SchemaVersion = BackupManifest.CurrentSchemaVersion,

            // Read off the scope that just produced scan, rather than off the current rules, so
            // the snapshot says which rules actually decided its contents. A restore reads this
            // back before it deletes anything.
            ScopeVersion = Scope.Version,
            AppVersion = _appVersion,
            CreatedUtc = DateTime.UtcNow,
            Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            Kind = kind,
        };

        foreach (var skipped in scan.SkippedLinks)
        {
            manifest.SkippedLinks.Add(skipped);
            progress?.Report($"Not backing up {skipped}: it is a link, and this app copies only real files inside the save folder");
        }

        foreach (var entry in scan.Files)
        {
            ct.ThrowIfCancellationRequested();

            var destination = Path.Combine(directory, entry.RelativePath);
            var parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            manifest.Files.Add(CopyIntoSnapshot(entry, destination, progress));
        }

        progress?.Report("Reading save slots");
        manifest.Slots.AddRange(ReadSlots(directory));

        progress?.Report("Writing manifest");
        ReleaseClaim(directory);
        WriteManifest(directory, manifest);

        return BackupSnapshot.Load(directory);
    }

    /// <summary>
    /// Every snapshot folder under the backup root, newest first. A folder with a missing or
    /// broken manifest is still listed, so the user can see it and delete it.
    /// </summary>
    public IReadOnlyList<BackupSnapshot> ListBackups()
    {
        var snapshots = new List<BackupSnapshot>();

        if (!Directory.Exists(BackupRoot))
        {
            return snapshots;
        }

        List<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(BackupRoot, "*", SearchOption.TopDirectoryOnly).ToList();
        }
        catch (Exception)
        {
            return snapshots;
        }

        foreach (var directory in directories)
        {
            try
            {
                snapshots.Add(BackupSnapshot.Load(directory));
            }
            catch (Exception ex)
            {
                snapshots.Add(new BackupSnapshot(
                    directory,
                    null,
                    $"this folder could not be read: {ex.Message}",
                    0,
                    DateTime.UtcNow));
            }
        }

        snapshots.Sort(static (a, b) =>
        {
            var byDate = b.CreatedUtc.CompareTo(a.CreatedUtc);
            return byDate != 0 ? byDate : PathComparer.Compare(b.Id, a.Id);
        });

        return snapshots;
    }

    /// <summary>
    /// What restoring this snapshot would do, worked out by comparing it against the live folder.
    /// Timestamps are ignored: sameness means the bytes hash the same.
    ///
    /// The deletion list is judged by the scope rules the snapshot was taken under, so it says
    /// what the restore will really remove rather than what today's wider rules would remove.
    /// </summary>
    public RestorePlan PlanRestore(BackupSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var added = new List<string>();
        var overwritten = new List<string>();
        var unchanged = new List<string>();
        var deleted = new List<string>();
        var leftAlone = new List<string>();
        var notRestored = new List<string>();

        if (snapshot.Manifest is not { } manifest)
        {
            return new RestorePlan(added, overwritten, unchanged, deleted);
        }

        var snapshotScopeVersion = manifest.EffectiveScopeVersion;

        var live = new Dictionary<string, ScopeEntry>(PathComparer);
        foreach (var entry in Scope.Enumerate())
        {
            live[entry.RelativePath] = entry;
        }

        var inSnapshot = new HashSet<string>(PathComparer);

        foreach (var file in manifest.Files)
        {
            var relative = NormaliseRelative(file.RelativePath);
            if (relative.Length == 0)
            {
                continue;
            }

            inSnapshot.Add(relative);

            // A file the snapshot holds and today's rules no longer cover is not put back, so it
            // belongs in neither "added" nor "overwritten". Listing it as added would promise the
            // user a file the restore is about to skip.
            if (!Scope.IsInScope(relative)
                && Scope.IsExcludedSinceScopeVersion(relative, snapshotScopeVersion))
            {
                notRestored.Add(relative);
                continue;
            }

            if (!live.TryGetValue(relative, out var liveEntry))
            {
                added.Add(relative);
                continue;
            }

            if (liveEntry.Length != file.SizeBytes)
            {
                overwritten.Add(relative);
                continue;
            }

            string liveHash;
            try
            {
                liveHash = Hashing.ComputeFileSha256(liveEntry.FullPath);
            }
            catch (Exception)
            {
                // A file that cannot be read cannot be shown to match, so it counts as changed.
                overwritten.Add(relative);
                continue;
            }

            if (PathComparer.Equals(liveHash, file.Sha256))
            {
                unchanged.Add(relative);
            }
            else
            {
                overwritten.Add(relative);
            }
        }

        foreach (var liveEntry in live.Values)
        {
            if (inSnapshot.Contains(liveEntry.RelativePath))
            {
                continue;
            }

            // Both conditions come from the scope, so nothing outside it can reach either list.
            if (!Scope.IsInScope(liveEntry.RelativePath))
            {
                continue;
            }

            if (IsDeletableByRestore(liveEntry.RelativePath, snapshotScopeVersion))
            {
                deleted.Add(liveEntry.RelativePath);
            }
            else
            {
                leftAlone.Add(liveEntry.RelativePath);
            }
        }

        added.Sort(PathComparer);
        overwritten.Sort(PathComparer);
        unchanged.Sort(PathComparer);
        deleted.Sort(PathComparer);
        leftAlone.Sort(PathComparer);
        notRestored.Sort(PathComparer);

        return new RestorePlan(added, overwritten, unchanged, deleted)
        {
            LeftAlone = leftAlone,
            NotRestored = notRestored,
        };
    }

    /// <summary>
    /// Whether restoring a snapshot taken under <paramref name="snapshotScopeVersion"/> may delete
    /// a live file the snapshot does not hold.
    ///
    /// A file is only ever missing from a snapshot for two reasons: it was not there when the
    /// snapshot was taken, or the rules of the day did not cover it. Only the first is the user
    /// deleting something. Asking the scope under both versions separates them: the file has to be
    /// one this app manages today and one the snapshot would have captured, so restoring a backup
    /// from before Rain Meadow support leaves meadow.json alone instead of reading its absence as
    /// an instruction.
    /// </summary>
    private bool IsDeletableByRestore(string relativePath, int snapshotScopeVersion) =>
        Scope.IsInScope(relativePath) && Scope.IsInScope(relativePath, snapshotScopeVersion);

    /// <summary>
    /// Re-hashes the files inside a snapshot against its own manifest and reports every
    /// mismatch, every missing file, and every file in the folder the manifest does not mention.
    /// </summary>
    public VerifyResult Verify(BackupSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var problems = new List<string>();

        if (snapshot.Manifest is not { } manifest)
        {
            problems.Add(snapshot.Problem ?? "manifest.json is missing or unreadable");
            return new VerifyResult(false, problems);
        }

        if (!Directory.Exists(snapshot.DirectoryPath))
        {
            problems.Add("the backup folder no longer exists");
            return new VerifyResult(false, problems);
        }

        var expected = new HashSet<string>(PathComparer);

        foreach (var file in manifest.Files)
        {
            var relative = NormaliseRelative(file.RelativePath);

            if (!TryResolveInside(snapshot.DirectoryPath, relative, out var fullPath))
            {
                problems.Add($"\"{file.RelativePath}\" is not a valid path inside the backup folder");
                continue;
            }

            expected.Add(relative);

            if (!File.Exists(fullPath))
            {
                problems.Add($"{relative} is listed in the manifest but missing from the backup");
                continue;
            }

            var info = new FileInfo(fullPath);
            if (info.Length != file.SizeBytes)
            {
                problems.Add($"{relative} is {info.Length} bytes, the manifest says {file.SizeBytes}");
                continue;
            }

            string actual;
            try
            {
                actual = Hashing.ComputeFileSha256(fullPath);
            }
            catch (Exception ex)
            {
                problems.Add($"{relative} could not be read: {ex.Message}");
                continue;
            }

            if (!PathComparer.Equals(actual, file.Sha256))
            {
                problems.Add($"{relative} does not match its recorded checksum");
            }
        }

        try
        {
            foreach (var fullPath in Directory.EnumerateFiles(snapshot.DirectoryPath, "*", SearchOption.AllDirectories))
            {
                var relative = NormaliseRelative(Path.GetRelativePath(snapshot.DirectoryPath, fullPath));

                if (PathComparer.Equals(relative, BackupSnapshot.ManifestFileName)
                    || PathComparer.Equals(relative, BackupSnapshot.ManifestFileName + ".tmp")
                    || PathComparer.Equals(relative, ClaimFileName))
                {
                    continue;
                }

                if (!expected.Contains(relative))
                {
                    problems.Add($"{relative} is in the backup folder but not in the manifest");
                }
            }
        }
        catch (Exception ex)
        {
            problems.Add($"the backup folder could not be listed: {ex.Message}");
        }

        problems.Sort(PathComparer);
        return new VerifyResult(problems.Count == 0, problems);
    }

    /// <summary>
    /// Puts a snapshot back over the live save folder.
    ///
    /// Order matters and is fixed: refuse while the game runs, refuse an unfinished snapshot,
    /// verify the snapshot before touching anything live, take a safety copy of the current
    /// saves and abort if that copy fails, then overwrite, then remove in-scope files the
    /// snapshot does not have, then re-hash what landed.
    ///
    /// The deletion step only runs when every file in the manifest went back. A restore that put
    /// nothing back must not be the thing that deletes today's files.
    /// </summary>
    public RestoreResult RestoreBackup(
        BackupSnapshot snapshot,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // (a) The game must be closed before anything else is considered.
        EnsureGameNotRunning();

        var errors = new List<string>();
        var warnings = new List<string>();

        // (b) An unfinished snapshot is not a restore source.
        if (snapshot.Manifest is not { } manifest)
        {
            errors.Add($"Backup {snapshot.Id} did not finish ({snapshot.Problem ?? "manifest.json is missing"}), so nothing was changed.");
            return new RestoreResult(false, null, errors, warnings, false);
        }

        using var lease = AcquireOperationLock();

        // (c) Verify before touching anything live.
        progress?.Report("Checking the backup");
        var verification = Verify(snapshot);
        if (!verification.Ok)
        {
            errors.Add($"Backup {snapshot.Id} failed its checksum check, so nothing was changed.");
            errors.AddRange(verification.Problems);
            return new RestoreResult(false, null, errors, warnings, false);
        }

        foreach (var skipped in manifest.SkippedLinks)
        {
            warnings.Add($"{skipped} was a link when this backup was taken, so the backup does not hold it and the restore leaves it alone.");
        }

        // Last point where abandoning the job costs nothing.
        ct.ThrowIfCancellationRequested();

        // (d) Safety copy of the current live state. No safety copy, no restore.
        BackupSnapshot safety;
        try
        {
            progress?.Report("Saving a copy of the current saves first");
            var safetyLabel = $"Before restoring {snapshot.Id}";
            var safetyNote = string.IsNullOrWhiteSpace(manifest.Label)
                ? $"Automatic copy taken before restoring backup {snapshot.Id}."
                : $"Automatic copy taken before restoring backup {snapshot.Id} (\"{manifest.Label}\").";

            safety = CreateBackup(safetyLabel, safetyNote, BackupKind.PreRestoreSafety, progress, ct);
        }
        catch (GameRunningException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // Nothing live has been touched yet, so cancelling here is clean.
            throw;
        }
        catch (Exception ex)
        {
            errors.Add($"The safety copy of the current saves failed ({ex.Message}), so the restore was abandoned and nothing was changed.");
            return new RestoreResult(false, null, errors, warnings, false);
        }

        if (!safety.IsComplete)
        {
            errors.Add($"The safety copy {safety.Id} did not finish ({safety.Problem}), so the restore was abandoned and nothing was changed.");
            return new RestoreResult(false, safety, errors, warnings, false);
        }

        // The safety copy took several seconds of hashing and copying. Check again before the
        // first live write, because the player may have started the game during it. Nothing has
        // been overwritten yet, so this can still refuse outright.
        EnsureGameNotRunning();

        // (e) Overwrite the live files. Per-file failures are collected so one locked file does
        // not stop the rest, and every one of them is reported.
        var restored = new List<ManifestFileEntry>(manifest.Files.Count);
        var notRestored = new List<string>();
        var liveModified = false;
        string? stopped = null;
        var snapshotScopeVersion = manifest.EffectiveScopeVersion;

        for (var index = 0; index < manifest.Files.Count; index++)
        {
            if (ct.IsCancellationRequested)
            {
                stopped = "it was cancelled";
                break;
            }

            // The game can be launched while this loop runs, and it would then write its own
            // state back over whatever is restored under it.
            if (index % GameCheckInterval == 0 && _gameDetector.IsGameRunning(out var processName))
            {
                stopped = $"Rain World ({processName ?? "the game"}) started while the restore was running";
                break;
            }

            var file = manifest.Files[index];
            var relative = NormaliseRelative(file.RelativePath);

            // An exclusion added since the snapshot was taken is not a broken manifest. The rules
            // that wrote this snapshot took the file, today's rules leave it out, and putting a
            // stale steam_autocloud.vdf back is exactly what excluding it is for. Skipping it is a
            // note, not a failure: treating it as one would fail the whole restore and, worse,
            // skip the deletion step below, turning a return to one moment into a merge.
            if (!Scope.IsInScope(relative)
                && Scope.IsExcludedSinceScopeVersion(relative, snapshotScopeVersion))
            {
                notRestored.Add(relative);
                warnings.Add(
                    $"{relative} is in this backup but is no longer one of the files this app manages, " +
                    "so it was left as it is rather than written back.");
                continue;
            }

            if (!Scope.IsInScope(relative)
                || !TryResolveInside(snapshot.DirectoryPath, relative, out var source)
                || !TryResolveInside(SaveRoot, relative, out var destination))
            {
                errors.Add($"Skipped \"{file.RelativePath}\": it is not one of the files this app manages.");
                continue;
            }

            // TryResolveInside is textual, and text cannot see a junction or a symlink. A copy
            // onto a link writes straight through it, over a file the user never named and the
            // safety snapshot never copied.
            if (CanonicalPath.LeadsThroughLink(SaveRoot, destination))
            {
                errors.Add($"Skipped {relative}: it is a link, so restoring it would write outside the save folder.");
                continue;
            }

            try
            {
                var parent = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                progress?.Report($"Restoring {relative} ({FormatSize(file.SizeBytes)})");
                ClearReadOnly(destination);
                File.Copy(source, destination, overwrite: true);
                liveModified = true;
                restored.Add(file);
            }
            catch (Exception ex)
            {
                errors.Add($"Could not restore {relative}: {ex.Message}");
            }
        }

        // A file skipped because today's rules exclude it counts as landed. It is where the
        // snapshot's own rules would leave it, and the alternative is that one such entry stops
        // the deletion step from running at all.
        var everyFileLanded = stopped is null
            && errors.Count == 0
            && restored.Count + notRestored.Count == manifest.Files.Count;

        try
        {
            // (f) Remove in-scope live files the snapshot does not have, then tidy up folders
            // that this left empty. Only reached when the whole snapshot went back: deleting
            // today's files on behalf of a restore that put nothing back is pure loss.
            if (everyFileLanded)
            {
                var keep = new HashSet<string>(PathComparer);
                foreach (var file in manifest.Files)
                {
                    keep.Add(NormaliseRelative(file.RelativePath));
                }

                progress?.Report("Removing files the backup does not have");

                var leftAlone = new List<string>();
                var removed = new List<string>();

                foreach (var liveEntry in Scope.Enumerate())
                {
                    if (ct.IsCancellationRequested)
                    {
                        stopped = "it was cancelled";
                        break;
                    }

                    if (keep.Contains(liveEntry.RelativePath))
                    {
                        continue;
                    }

                    if (!IsDeletableByRestore(liveEntry.RelativePath, snapshotScopeVersion))
                    {
                        // Out of scope entirely, or in scope only under rules newer than this
                        // snapshot. Either way its absence from the manifest says nothing.
                        if (Scope.IsInScope(liveEntry.RelativePath))
                        {
                            leftAlone.Add(liveEntry.RelativePath);
                        }

                        continue;
                    }

                    try
                    {
                        progress?.Report($"Removing {liveEntry.RelativePath}");
                        ClearReadOnly(liveEntry.FullPath);
                        File.Delete(liveEntry.FullPath);
                        liveModified = true;
                        removed.Add(liveEntry.RelativePath);
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Could not remove {liveEntry.RelativePath}: {ex.Message}");
                    }
                }

                if (leftAlone.Count > 0)
                {
                    leftAlone.Sort(PathComparer);
                    warnings.Add(
                        $"Backup {snapshot.Id} was taken when this app backed up fewer kinds of file, so it does not hold " +
                        $"{FormatPathList(leftAlone)}. Those files were left as they are rather than deleted.");
                }

                // An empty folder left behind changes nothing about the saves, so it is a note
                // rather than a failure.
                RemoveEmptiedScopeFolders(warnings, snapshotScopeVersion, removed);
            }
            else
            {
                warnings.Add("Files the backup does not have were left in the save folder, because the restore did not put every file back.");
            }
        }
        catch (Exception ex)
        {
            // Anything thrown from here on has to come back as a result. The live files have
            // already been written, and the caller needs the safety snapshot id to undo that.
            errors.Add($"The restore could not finish tidying the save folder: {ex.Message}");
        }

        try
        {
            // (g) Prove the live files match the manifest now.
            foreach (var file in restored)
            {
                var relative = NormaliseRelative(file.RelativePath);
                if (!TryResolveInside(SaveRoot, relative, out var destination))
                {
                    continue;
                }

                try
                {
                    if (!File.Exists(destination))
                    {
                        errors.Add($"{relative} is missing after the restore.");
                        continue;
                    }

                    if (!PathComparer.Equals(Hashing.ComputeFileSha256(destination), file.Sha256))
                    {
                        errors.Add($"{relative} does not match the backup after the restore.");
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"{relative} could not be checked after the restore: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add($"The restored files could not all be checked: {ex.Message}");
        }

        if (stopped is not null)
        {
            errors.Add($"The restore stopped after {restored.Count} of {manifest.Files.Count} files because {stopped}.");
        }

        var success = errors.Count == 0;
        progress?.Report(success ? "Restore finished" : "Restore finished with problems");

        return new RestoreResult(success, safety, errors, warnings, liveModified);
    }

    /// <summary>
    /// Deletes a snapshot folder. Refuses anything that is not a direct child of the backup root.
    /// </summary>
    public void DeleteBackup(BackupSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var target = TrimSeparators(Path.GetFullPath(snapshot.DirectoryPath));
        var root = TrimSeparators(BackupRoot);
        var parent = Path.GetDirectoryName(target);

        if (string.IsNullOrEmpty(parent) || !PathComparer.Equals(TrimSeparators(parent), root))
        {
            throw new InvalidOperationException(
                $"Refusing to delete \"{target}\": it is not a backup folder directly inside {root}.");
        }

        if (IsInside(SaveRoot, target))
        {
            throw new InvalidOperationException(
                $"Refusing to delete \"{target}\": it sits inside the save folder {SaveRoot}.");
        }

        if (!Directory.Exists(target))
        {
            return;
        }

        using var lease = AcquireOperationLock();
        Directory.Delete(target, recursive: true);
    }

    /// <summary>
    /// Slot metadata for the live save folder. Fail-soft: a slot that cannot be parsed comes back
    /// with its ParseError set rather than throwing.
    /// </summary>
    public IReadOnlyList<SlotMetadata> ReadLiveSlots() => ReadSlots(SaveRoot);

    /// <summary>
    /// Copying one whole slot file onto another. It borrows this service's safety snapshot and
    /// this service's scope, so it hangs off the same object rather than being built separately.
    /// </summary>
    public SlotCopyService SlotCopies => _slotCopies ??= new SlotCopyService(this, _gameDetector);

    /// <summary>
    /// Copies one whole save slot file onto another, byte for byte. See
    /// <see cref="SlotCopyService.CopySlot"/> for what that does and does not touch.
    /// </summary>
    public SlotCopyResult CopySlot(
        SaveSlotRef from,
        SaveSlotRef to,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
        => SlotCopies.CopySlot(from, to, progress, ct);

    private void EnsureGameNotRunning()
    {
        if (_gameDetector.IsGameRunning(out var processName))
        {
            throw new GameRunningException(string.IsNullOrWhiteSpace(processName) ? "Rain World" : processName);
        }
    }

    /// <summary>
    /// Copies one file into the snapshot and proves the copy against its source before the
    /// manifest records it.
    ///
    /// The source is measured before the copy and hashed after it, and the copy is hashed too.
    /// Hashing only the copy, which is what the manifest used to record, makes every later check
    /// compare the snapshot against itself: a file truncated by Steam Cloud mid-copy would be
    /// recorded at its truncated length with the hash of its truncated bytes, verify clean, and
    /// be accepted as the safety snapshot standing behind a restore.
    /// </summary>
    private static ManifestFileEntry CopyIntoSnapshot(ScopeEntry entry, string destination, IProgress<string>? progress)
    {
        var expectedLength = entry.Length;
        var expectedWrite = entry.LastWriteUtc;
        string problem = "it could not be read";

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            if (attempt == 2)
            {
                var refreshed = new FileInfo(entry.FullPath);
                if (!refreshed.Exists)
                {
                    break;
                }

                expectedLength = refreshed.Length;
                expectedWrite = refreshed.LastWriteTimeUtc;
            }

            progress?.Report(attempt == 1
                ? $"Copying {entry.RelativePath} ({FormatSize(expectedLength)})"
                : $"Copying {entry.RelativePath} again, it moved during the first copy");

            File.Copy(entry.FullPath, destination, overwrite: true);

            progress?.Report($"Checking {entry.RelativePath}");

            var source = new FileInfo(entry.FullPath);
            var copy = new FileInfo(destination);

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
                var sourceHash = Hashing.ComputeFileSha256(entry.FullPath);
                var copyHash = Hashing.ComputeFileSha256(destination);

                if (PathComparer.Equals(sourceHash, copyHash))
                {
                    return new ManifestFileEntry(entry.RelativePath, copy.Length, copyHash, copy.LastWriteTimeUtc);
                }

                problem = "the copy does not match the file it was copied from";
            }
        }

        throw new IOException(
            $"{entry.RelativePath} could not be copied: {problem}. The backup was abandoned rather than " +
            "recorded as sound. Close Steam, or wait for Steam Cloud to finish syncing, and try again.");
    }

    /// <summary>
    /// Reads sav, sav2, and sav3 from a folder, which is either the live folder or a snapshot.
    /// </summary>
    private static List<SlotMetadata> ReadSlots(string rootDirectory)
    {
        var slots = new List<SlotMetadata>();

        try
        {
            if (!Directory.Exists(rootDirectory))
            {
                return slots;
            }

            foreach (var fullPath in Directory.EnumerateFiles(rootDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(fullPath);
                if (SaveMetadataExtractor.SlotForFileName(name) is not { } slot)
                {
                    continue;
                }

                try
                {
                    slots.Add(SaveMetadataExtractor.Extract(fullPath, slot.Slot, slot.Realm));
                }
                catch (Exception ex)
                {
                    // The realm comes from the name rather than being left to default, so a
                    // Rain Meadow file that could not be read is still listed as an online one.
                    slots.Add(new SlotMetadata
                    {
                        Slot = slot.Slot,
                        FileName = name,
                        Realm = slot.Realm,
                        ParseError = ex.Message,
                    });
                }
            }
        }
        catch (Exception)
        {
            // Slot metadata is for display. A folder that cannot be listed shows no slots.
        }

        slots.Sort(static (a, b) => a.Slot.CompareTo(b.Slot));
        return slots;
    }

    /// <summary>
    /// Picks a snapshot folder name and claims it.
    ///
    /// Directory.CreateDirectory succeeds on a folder that already exists, so it cannot decide
    /// who owns a name: two operations starting in the same second would both be handed the same
    /// folder and write over each other's copies. Creating a file that must not exist yet is the
    /// step the filesystem makes atomic, so that is what settles the race.
    /// </summary>
    internal string CreateSnapshotDirectory()
    {
        // Local time, because the folder name is what the user reads in the list.
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);

        Directory.CreateDirectory(BackupRoot);

        for (var attempt = 1; attempt <= 1000; attempt++)
        {
            var name = attempt == 1 ? stamp : $"{stamp}_{attempt}";
            var path = Path.Combine(BackupRoot, name);

            // A folder that is already there belongs to an earlier snapshot, finished or not.
            if (Directory.Exists(path) || File.Exists(path))
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(path);
            }
            catch (IOException)
            {
                continue;
            }

            if (TryClaim(path))
            {
                return path;
            }
        }

        throw new IOException($"Could not create a backup folder under {BackupRoot}: too many folders share the name {stamp}.");
    }

    private static bool TryClaim(string directory)
    {
        try
        {
            using var claim = new FileStream(
                Path.Combine(directory, ClaimFileName),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void ReleaseClaim(string directory)
    {
        try
        {
            var path = Path.Combine(directory, ClaimFileName);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // The claim only matters while the folder is being filled. A leftover one is
            // reported by Verify and ignored.
        }
    }

    /// <summary>
    /// Holds the backup folder for the length of one operation, so a second window or a second
    /// process cannot interleave with a restore that is part way through the live save folder.
    /// Re-entrant, because a restore takes its safety snapshot through the same lock.
    /// </summary>
    private IDisposable AcquireOperationLock()
    {
        lock (_lockGate)
        {
            if (_operationDepth == 0)
            {
                Directory.CreateDirectory(BackupRoot);

                try
                {
                    _operationLock = new FileStream(
                        Path.Combine(BackupRoot, LockFileName),
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        bufferSize: 1,
                        FileOptions.DeleteOnClose);
                }
                catch (IOException ex)
                {
                    throw new BackupBusyException(BackupRoot, ex);
                }
            }

            _operationDepth++;
        }

        return new OperationLease(this);
    }

    private void ReleaseOperationLock()
    {
        lock (_lockGate)
        {
            if (_operationDepth == 0)
            {
                return;
            }

            _operationDepth--;
            if (_operationDepth > 0)
            {
                return;
            }

            try
            {
                _operationLock?.Dispose();
            }
            catch (Exception)
            {
            }

            _operationLock = null;
        }
    }

    private sealed class OperationLease : IDisposable
    {
        private readonly BackupService _service;
        private bool _released;

        public OperationLease(BackupService service) => _service = service;

        public void Dispose()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            _service.ReleaseOperationLock();
        }
    }

    private static void WriteManifest(string directory, BackupManifest manifest)
    {
        var path = Path.Combine(directory, BackupSnapshot.ManifestFileName);
        var temporary = path + ".tmp";
        var json = JsonSerializer.Serialize(manifest, BackupJson.Options);

        File.WriteAllText(temporary, json);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// Deletes the folders this restore's own deletions left empty, and nothing else.
    ///
    /// Only the parent chain of the files just removed is considered. Sweeping the scope folders
    /// for any empty directory instead takes away ones the restore never touched: Warp\Export
    /// ships empty, so a restore that changed nothing inside Warp would still remove it and the
    /// mod would find its export directory gone.
    ///
    /// The folder list is the one the snapshot's own rules covered, so restoring a version 1
    /// snapshot cannot reach into dressmyslugcat, RandomBuff or Warp, which those rules never
    /// covered and which the confirmation dialog therefore never listed.
    ///
    /// The walk stops at the scope folders themselves, so the mod that owns one does not find it
    /// gone, and every step checks for a reparse point: a user who moved dvrmentSaveStates onto
    /// another drive and left a junction behind would otherwise have folders deleted on the far
    /// side of it, outside the save folder and outside anything the safety snapshot holds.
    /// </summary>
    private void RemoveEmptiedScopeFolders(
        List<string> warnings,
        int snapshotScopeVersion,
        IReadOnlyList<string> removedRelativePaths)
    {
        if (removedRelativePaths.Count == 0)
        {
            return;
        }

        var roots = new List<string>();
        foreach (var relativeRoot in BackupScope.RecursiveFoldersAt(snapshotScopeVersion))
        {
            var rootPath = Path.Combine(SaveRoot, relativeRoot);
            if (Directory.Exists(rootPath) && !CanonicalPath.IsLink(rootPath))
            {
                roots.Add(TrimSeparators(Path.GetFullPath(rootPath)));
            }
        }

        if (roots.Count == 0)
        {
            return;
        }

        var candidates = new HashSet<string>(PathComparer);

        foreach (var relative in removedRelativePaths)
        {
            if (!TryResolveInside(SaveRoot, relative, out var fullPath))
            {
                continue;
            }

            var directory = Path.GetDirectoryName(fullPath);

            while (!string.IsNullOrEmpty(directory))
            {
                var trimmed = TrimSeparators(Path.GetFullPath(directory));

                // A scope folder is where the climb stops. It stays, and so does everything above
                // it, which includes the save folder itself.
                if (IsOneOf(roots, trimmed) || !IsUnderAnyOf(roots, trimmed))
                {
                    break;
                }

                candidates.Add(trimmed);
                directory = Path.GetDirectoryName(trimmed);
            }
        }

        // Deepest first, so a folder that only held empty folders also goes.
        var ordered = candidates.ToList();
        ordered.Sort(static (a, b) => b.Length.CompareTo(a.Length));

        foreach (var directory in ordered)
        {
            try
            {
                if (!Directory.Exists(directory) || Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    continue;
                }

                // Last guard before a delete: the resolved path has to still be inside the
                // save folder.
                if (CanonicalPath.IsLink(directory) || !CanonicalPath.IsInside(SaveRoot, directory))
                {
                    continue;
                }

                Directory.Delete(directory, recursive: false);
            }
            catch (Exception ex)
            {
                warnings.Add($"Could not remove the empty folder {Path.GetRelativePath(SaveRoot, directory)}: {ex.Message}");
            }
        }
    }

    private static bool IsOneOf(IReadOnlyList<string> roots, string candidate)
    {
        foreach (var root in roots)
        {
            if (PathComparer.Equals(root, candidate))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUnderAnyOf(IReadOnlyList<string> roots, string candidate)
    {
        foreach (var root in roots)
        {
            if (IsInside(root, candidate))
            {
                return true;
            }
        }

        return false;
    }

    private static void ClearReadOnly(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            var attributes = File.GetAttributes(path);

            // Attributes on a link belong to the link, not to what it points at. Nothing here
            // should be adjusting either.
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return;
            }

            if (attributes.HasFlag(FileAttributes.ReadOnly))
            {
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }
        }
        catch (Exception)
        {
            // If the attribute cannot be cleared the copy or delete below reports the real error.
        }
    }

    private static string NormaliseRelative(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return "";
        }

        var segments = relativePath
            .Trim()
            .Replace('/', '\\')
            .Split('\\', StringSplitOptions.RemoveEmptyEntries);

        return string.Join('\\', segments);
    }

    /// <summary>
    /// Turns a manifest path into a full path and proves it stays inside the given root. A
    /// manifest is a file on disk, so it is treated as untrusted input.
    /// </summary>
    private static bool TryResolveInside(string root, string relativePath, out string fullPath)
    {
        fullPath = "";

        var relative = NormaliseRelative(relativePath);
        if (relative.Length == 0 || Path.IsPathRooted(relative))
        {
            return false;
        }

        string candidate;
        try
        {
            candidate = Path.GetFullPath(Path.Combine(root, relative));
        }
        catch (Exception)
        {
            return false;
        }

        if (!IsInside(root, candidate))
        {
            return false;
        }

        // Windows drops trailing spaces and dots while resolving a path. If the resolved path does
        // not lead back to the relative path it came from, the entry does not name the file it
        // claims to, so it is refused rather than written.
        if (!PathComparer.Equals(NormaliseRelative(Path.GetRelativePath(root, candidate)), relative))
        {
            return false;
        }

        fullPath = candidate;
        return true;
    }

    private static bool IsInside(string root, string candidate)
    {
        var rootPath = TrimSeparators(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimSeparators(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// Names a set of files for one line of a report, capped so a folder with hundreds of entries
    /// does not turn a warning into a wall of text.
    /// </summary>
    private static string FormatPathList(IReadOnlyList<string> paths)
    {
        const int Limit = 8;

        if (paths.Count <= Limit)
        {
            return string.Join(", ", paths);
        }

        var remainder = paths.Count - Limit;
        return string.Join(", ", paths.Take(Limit))
            + string.Format(CultureInfo.InvariantCulture, " and {0} more", remainder);
    }

    private static string FormatSize(long bytes)
    {
        const double Kilobyte = 1024;
        const double Megabyte = Kilobyte * 1024;
        const double Gigabyte = Megabyte * 1024;

        if (bytes >= Gigabyte)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:0.0} GB", bytes / Gigabyte);
        }

        if (bytes >= Megabyte)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:0.0} MB", bytes / Megabyte);
        }

        if (bytes >= Kilobyte)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:0.0} KB", bytes / Kilobyte);
        }

        return bytes == 1 ? "1 byte" : $"{bytes} bytes";
    }
}
