using System.Globalization;
using System.Text.Json;
using RainWorldCompanion.Core.Editing;
using RainWorldCompanion.Core.Mods;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.Core.Saves.Models;
using RainWorldCompanion.Core.Settings;
using RainWorldCompanion.Core.System;

namespace RainWorldCompanion.Core.Backups;

/// <summary>
/// Copies save files byte for byte and never decodes them: the UTF-8 BOM and the trailing NUL
/// padding are part of what the game reads back. Every integrity check compares two independent
/// things, never a copy against itself, so a snapshot cannot certify its own damage.
/// </summary>
public sealed class BackupService
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    private const string LockFileName = ".operation-lock";

    internal const string ClaimFileName = ".creating";

    /// <summary>How often the restore loop re-checks for the game, counted in files. Checking every
    /// file means a full process enumeration per file.</summary>
    private const int GameCheckInterval = 16;

    private readonly IGameProcessDetector _gameDetector;
    private readonly string _appVersion;
    private readonly Func<CurrentMods>? _modListSource;
    private readonly object _lockGate = new();

    private FileStream? _operationLock;
    private int _operationDepth;
    private SlotCopyService? _slotCopies;
    private SaveSlotWriter? _slotWriter;

    public BackupService(
        string saveRoot,
        string backupRoot,
        IGameProcessDetector gameDetector,
        string appVersion,
        Func<CurrentMods>? modListSource = null)
        : this(saveRoot, backupRoot, gameDetector, appVersion, scope: null, modListSource)
    {
    }

    internal BackupService(
        string saveRoot,
        string backupRoot,
        IGameProcessDetector gameDetector,
        string appVersion,
        BackupScope? scope,
        Func<CurrentMods>? modListSource = null)
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
        _modListSource = modListSource;

        SaveRoot = Path.GetFullPath(saveRoot);
        BackupRoot = Path.GetFullPath(backupRoot);

        var problem = SettingsValidation.Validate(SaveRoot, BackupRoot);
        if (problem is not null)
        {
            throw new ArgumentException(problem, nameof(backupRoot));
        }

        Scope = scope ?? new BackupScope(SaveRoot);
    }

    public string SaveRoot { get; }

    public string BackupRoot { get; }

    internal Func<CurrentMods>? ModListSource => _modListSource;

    /// <summary>The mods that are on, or null when there is no source or reading it failed.</summary>
    internal ModListSnapshot? TryReadMods()
    {
        try
        {
            return _modListSource?.Invoke().Enabled;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>How a recorded list stands against this machine, or null when there is no way to
    /// look. Null and "nothing was recorded" are different answers.</summary>
    internal ModListDiff? TryDiffMods(ModListSnapshot? recorded)
    {
        try
        {
            return _modListSource is null ? null : ModListDiff.Compare(recorded, _modListSource());
        }
        catch (Exception)
        {
            return null;
        }
    }

    public BackupScope Scope { get; }

    /// <summary>
    /// The mod settings a snapshot holds, or null when it holds none. A snapshot is a faithful copy
    /// of the save folder, so its ModConfigs sits at the same path it did there and the same rule
    /// decides which of it travels.
    /// </summary>
    public Library.ModConfigOffer? SettingsFor(BackupSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.Manifest is not { } manifest)
        {
            return null;
        }

        var recorded = new ModConfigSet { ReadTheFolder = true };

        foreach (var file in manifest.Files)
        {
            var relative = file.RelativePath ?? "";
            if (ModConfigReader.Travels(relative))
            {
                recorded.Files.Add(new ModConfigFile
                {
                    RelativePath = relative,
                    ModId = ModConfigReader.ModIdFor(relative),
                    SizeBytes = file.SizeBytes,
                    Sha256 = file.Sha256,
                });
            }
        }

        if (recorded.Files.Count == 0)
        {
            return null;
        }

        var scan = ModConfigReader.Read(SaveRoot);
        var live = new ModConfigSet
        {
            ReadTheFolder = scan.ReadTheFolder,
            Note = scan.Note,
            Files = scan.Files
                .Select(file => new ModConfigFile { RelativePath = file.RelativePath, ModId = file.ModId })
                .ToList(),
        };

        return new Library.ModConfigOffer(recorded, manifest.Mods, live, TryReadCurrentMods())
        {
            MachineSpecific = MachineSpecificIn(snapshot, recorded),
        };
    }

    /// <summary>The settings files to write out of a snapshot, for the mods that were asked for.</summary>
    public IReadOnlyList<ExtraFileWrite> SettingsToWrite(
        BackupSnapshot snapshot,
        IReadOnlyCollection<string> adoptSettingsFor)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(adoptSettingsFor);

        if (adoptSettingsFor.Count == 0 || snapshot.Manifest is not { } manifest)
        {
            return Array.Empty<ExtraFileWrite>();
        }

        var wanted = new HashSet<string>(adoptSettingsFor, StringComparer.OrdinalIgnoreCase);
        var extras = new List<ExtraFileWrite>();

        foreach (var file in manifest.Files)
        {
            var relative = file.RelativePath ?? "";

            if (!ModConfigReader.Travels(relative)
                || !wanted.Contains(ModConfigReader.ModIdFor(relative))
                || !TryResolveInside(snapshot.DirectoryPath, relative, out var source))
            {
                continue;
            }

            extras.Add(new ExtraFileWrite(relative, source, file.Sha256, relative));
        }

        return extras;
    }

    private IReadOnlyDictionary<string, IReadOnlyList<string>> MachineSpecificIn(
        BackupSnapshot snapshot,
        ModConfigSet recorded)
    {
        var found = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in recorded.Files)
        {
            if (!TryResolveInside(snapshot.DirectoryPath, file.RelativePath, out var path))
            {
                continue;
            }

            var keys = ModConfigNotes.MachineSpecificKeys(path);
            if (keys.Count > 0)
            {
                found[file.RelativePath] = keys;
            }
        }

        return found;
    }

    private CurrentMods? TryReadCurrentMods()
    {
        try
        {
            return _modListSource?.Invoke();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Copies every in-scope file into a new snapshot folder. The manifest is written last, so a
    /// folder without one is a snapshot that did not finish, and a failure leaves the folder there.
    /// </summary>
    /// <param name="mods">
    /// The list to record instead of the one on the machine right now. A safety copy passes the
    /// list from before the operation began: turning mods on to match a save happens between the
    /// dialog opening and the write, and a snapshot that recorded the new list would describe the
    /// state it exists to undo rather than the one it came from.
    /// </param>
    public BackupSnapshot CreateBackup(
        string? label,
        string? note,
        BackupKind kind = BackupKind.Manual,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        ModListSnapshot? mods = null)
    {
        EnsureGameNotRunning();
        ct.ThrowIfCancellationRequested();

        using var lease = AcquireOperationLock();

        var scan = Scope.Scan();
        var directory = CreateSnapshotDirectory();

        var manifest = new BackupManifest
        {
            SchemaVersion = BackupManifest.CurrentSchemaVersion,

            // The scope that produced scan, not today's rules: this records what decided the contents.
            ScopeVersion = Scope.Version,
            AppVersion = _appVersion,
            CreatedUtc = DateTime.UtcNow,
            Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            Kind = kind,
            Mods = mods ?? TryReadMods(),
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

    /// <summary>Every snapshot folder under the backup root, newest first. A folder with a missing
    /// or broken manifest is still listed.</summary>
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
    /// What restoring this snapshot would do. Timestamps are ignored: sameness means the bytes hash
    /// the same. The deletion list is judged by the scope rules the snapshot was taken under.
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
            // belongs in neither "added" nor "overwritten".
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
            Mods = TryDiffMods(manifest.Mods),
        };
    }

    /// <summary>Whether restoring a snapshot taken under <paramref name="snapshotScopeVersion"/> may
    /// delete a live file it does not hold. Asking both versions separates "the user deleted it"
    /// from "the rules of the day did not cover it".</summary>
    private bool IsDeletableByRestore(string relativePath, int snapshotScopeVersion) =>
        Scope.IsInScope(relativePath) && Scope.IsInScope(relativePath, snapshotScopeVersion);

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
    /// Puts a snapshot back over the live save folder. The order is fixed: refuse while the game
    /// runs, refuse an unfinished snapshot, verify, take a safety copy and abort if it fails,
    /// overwrite, remove in-scope files the snapshot does not hold, re-hash what landed. The
    /// deletion step runs only when every file in the manifest went back.
    /// </summary>
    /// <param name="modsBefore">The mods that were on before the operation began. See
    /// <see cref="CreateBackup"/>.</param>
    public RestoreResult RestoreBackup(
        BackupSnapshot snapshot,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        ModListSnapshot? modsBefore = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        EnsureGameNotRunning();

        var errors = new List<string>();
        var warnings = new List<string>();

        if (snapshot.Manifest is not { } manifest)
        {
            errors.Add($"Backup {snapshot.Id} did not finish ({snapshot.Problem ?? "manifest.json is missing"}), so nothing was changed.");
            return new RestoreResult(false, null, errors, warnings, false);
        }

        using var lease = AcquireOperationLock();

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

        BackupSnapshot safety;
        try
        {
            progress?.Report("Saving a copy of the current saves first");
            var safetyLabel = $"Before restoring {snapshot.Id}";
            var safetyNote = string.IsNullOrWhiteSpace(manifest.Label)
                ? $"Automatic copy taken before restoring backup {snapshot.Id}."
                : $"Automatic copy taken before restoring backup {snapshot.Id} (\"{manifest.Label}\").";

            safety = CreateBackup(safetyLabel, safetyNote, BackupKind.PreRestoreSafety, progress, ct, modsBefore);
        }
        catch (GameRunningException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
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

        // The safety copy took several seconds, so the player may have started the game during it.
        // Nothing has been overwritten yet, so this can still refuse outright.
        EnsureGameNotRunning();

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

            // The game can be launched mid-loop, and would write its own state over what is restored.
            if (index % GameCheckInterval == 0 && _gameDetector.IsGameRunning(out var processName))
            {
                stopped = $"Rain World ({processName ?? "the game"}) started while the restore was running";
                break;
            }

            var file = manifest.Files[index];
            var relative = NormaliseRelative(file.RelativePath);

            // An exclusion added since the snapshot was taken is a note, not a failure: treating it
            // as one would fail the restore and skip the deletion step below, turning a return to
            // one moment into a merge.
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

            // TryResolveInside is textual, and text cannot see a junction. A copy onto a link writes
            // straight through it, over a file the safety snapshot never copied.
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

        // A file skipped because today's rules exclude it counts as landed, or one such entry stops
        // the deletion step from running at all.
        var everyFileLanded = stopped is null
            && errors.Count == 0
            && restored.Count + notRestored.Count == manifest.Files.Count;

        try
        {
            // Only reached when the whole snapshot went back: deleting today's files on behalf of a
            // restore that put nothing back is pure loss.
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
                        // In scope only under rules newer than this snapshot, so its absence from
                        // the manifest says nothing.
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

    /// <summary>Refuses anything that is not a direct child of the backup root.</summary>
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

    /// <summary>A slot that cannot be parsed comes back with its ParseError set rather than throwing.</summary>
    public IReadOnlyList<SlotMetadata> ReadLiveSlots() => ReadSlots(SaveRoot);

    public SlotCopyService SlotCopies => _slotCopies ??= new SlotCopyService(this, _gameDetector);

    public SaveSlotWriter SlotWriter => _slotWriter ??= new SaveSlotWriter(this, _gameDetector);

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

    /// <summary>The source is hashed as well as the copy. Hashing only the copy makes every later
    /// check compare the snapshot against itself, so a file truncated by Steam Cloud mid-copy would
    /// be recorded at its truncated length and verify clean.</summary>
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
                    // The realm comes from the name, so an unreadable Rain Meadow file is still online.
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
        }

        slots.Sort(static (a, b) => a.Slot.CompareTo(b.Slot));
        return slots;
    }

    internal string CreateSnapshotDirectory() =>
        TimestampedFolders.Create(BackupRoot, ClaimFileName, "backup folder");

    private static void ReleaseClaim(string directory) =>
        TimestampedFolders.ReleaseClaim(directory, ClaimFileName);

    /// <summary>Holds the backup folder for the length of one operation. Re-entrant, because a
    /// restore takes its safety snapshot through the same lock.</summary>
    internal IDisposable AcquireOperationLock()
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
    /// Deletes the folders this restore's own deletions left empty, and nothing else. Only the
    /// parent chain of the files just removed is considered: Warp\Export ships empty, so sweeping
    /// for any empty directory would remove it from a restore that never touched Warp.
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

                // A scope folder is where the climb stops. It stays, and so does everything above it.
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

            // Attributes on a link belong to the link, not to what it points at.
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

    /// <summary>A manifest is a file on disk, so its paths are treated as untrusted input.</summary>
    internal static bool TryResolveInside(string root, string relativePath, out string fullPath)
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

        // Windows drops trailing spaces and dots while resolving a path, so a resolved path that
        // does not lead back to the relative path it came from does not name the file it claims to.
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
