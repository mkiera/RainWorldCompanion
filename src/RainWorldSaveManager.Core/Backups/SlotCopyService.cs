// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Globalization;
using RainWorldSaveManager.Core.Saves;
using RainWorldSaveManager.Core.Saves.Models;
using RainWorldSaveManager.Core.System;

namespace RainWorldSaveManager.Core.Backups;

/// <summary>
/// One end of a copy, read off disk without changing anything.
///
/// <see cref="Metadata"/> is null when the file is not there. It is present but carries a
/// ParseError when the file is there and could not be read, which is a state the dialog shows
/// rather than a reason to refuse: a byte for byte copy of an unreadable file is exactly what a
/// user moving a damaged save off a slot is asking for.
/// </summary>
public sealed record SlotSide(
    SaveRealm Realm,
    int Slot,
    string FileName,
    string FullPath,
    bool Exists,
    long SizeBytes,
    DateTime? LastWriteUtc,
    SlotMetadata? Metadata)
{
    /// <summary>One line for the UI, for example "online_sav2 (96.0 KB): White cycle 4".</summary>
    public string Describe()
    {
        if (!Exists)
        {
            return FileName + ": not there";
        }

        string size = SlotCopyService.FormatSize(SizeBytes);

        if (Metadata is not { } metadata)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0} ({1})", FileName, size);
        }

        if (metadata.ParseError is { } parseError)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0} ({1}): unreadable ({2})", FileName, size, parseError);
        }

        if (metadata.Campaigns.Count == 0)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} ({1}): {2}",
                FileName,
                size,
                SlotMetadata.DescribeWithoutCampaigns(metadata.RecordCount));
        }

        // Describe() leads with "Slot n", which is already the row the user clicked. The file name
        // is the thing that separates the two sides of this dialog, so it leads instead.
        string body = metadata.Describe();
        int colon = body.IndexOf(':');
        if (colon >= 0)
        {
            body = body[(colon + 1)..].Trim();
        }

        return string.Format(CultureInfo.InvariantCulture, "{0} ({1}): {2}", FileName, size, body);
    }
}

/// <summary>
/// What a slot copy would do, worked out without changing anything.
///
/// <see cref="Problems"/> holds the reasons the copy is refused, and an empty list is what
/// <see cref="CanCopy"/> means. <see cref="Warnings"/> holds things the user should see before
/// agreeing, such as an empty source about to land on a slot that holds a campaign.
/// </summary>
public sealed record SlotCopyPlan(
    SlotSide Source,
    SlotSide Target,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Warnings)
{
    public bool CanCopy => Problems.Count == 0;
}

/// <summary>
/// The outcome of a slot copy, in the same shape as <see cref="RestoreResult"/> so a caller can
/// report both the same way. <see cref="LiveFolderModified"/> is again the field to read before
/// wording a failure: it is false only when the target file is provably the same bytes it was
/// before the operation started.
/// </summary>
public sealed record SlotCopyResult(
    bool Success,
    BackupSnapshot? SafetySnapshot,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    bool LiveFolderModified,
    long BytesCopied,
    SlotCopyPlan? Plan)
{
    /// <summary>
    /// The line to lead a report with. The wording lives here rather than in the UI so that
    /// "nothing was changed" can never be printed over a save file that was in fact written to,
    /// and so the sentence naming the safety snapshot is the same sentence in every caller.
    /// </summary>
    public string Headline()
    {
        string target = Plan?.Target.FileName ?? "the target slot";

        if (Success)
        {
            string source = Plan?.Source.FileName ?? "the source slot";
            return string.Format(
                CultureInfo.InvariantCulture,
                "Copied {0} onto {1} ({2}).",
                source,
                target,
                SlotCopyService.FormatSize(BytesCopied));
        }

        if (!LiveFolderModified)
        {
            return "The copy did not happen, so nothing in the save folder was changed.";
        }

        string? safety = SafetySnapshot?.Id;
        return safety is null
            ? $"The copy did not finish and {target} may be part written."
            : $"The copy did not finish and {target} may be part written. Backup {safety} still holds the saves as they were, and restoring it puts them back.";
    }
}

/// <summary>
/// One write onto one slot, described in the caller's own words.
///
/// <paramref name="SourceExpectedSha256"/> is the digest the source is held to under the lock, or
/// null for a source that has none. A live slot has none, because nothing recorded it. A library
/// save has one, and holding it to that digest is what stops a save damaged since it was stored
/// from reaching the save folder.
/// </summary>
/// <param name="SafetyNote">
/// Called with whether the target file already exists, so the note recorded in the safety snapshot
/// can say which of the two things happened.
/// </param>
internal sealed record SlotWriteJob(
    SlotSide Target,
    string SourcePath,
    string SourceLabel,
    string? SourceExpectedSha256,
    string OperationNoun,
    string ProgressVerb,
    string SafetyLabel,
    Func<bool, string> SafetyNote);

/// <summary>
/// What the shared ladder did. <see cref="LiveFolderModified"/> is false only when the target file
/// is provably the bytes it was before, which is what lets a caller say nothing was changed without
/// having to work that out itself.
/// </summary>
internal sealed record SlotWriteOutcome(
    bool Success,
    BackupSnapshot? SafetySnapshot,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    bool LiveFolderModified,
    long BytesCopied)
{
    /// <summary>The shape for every path that stops before the target file is written.</summary>
    internal static SlotWriteOutcome Refused(
        BackupSnapshot? safety,
        IReadOnlyList<string> errors,
        IReadOnlyList<string> warnings) =>
        new(false, safety, errors, warnings, false, 0);
}

/// <summary>
/// Copies one whole save slot file onto another, byte for byte.
///
/// Nothing here parses or rewrites a save. The operation is File.Copy and nothing else, so the
/// UTF-8 BOM, the trailing NUL padding and the MD5 digest inside the payload all arrive unchanged
/// and the game reads the result as the file it came from. That is the whole reason this is safe
/// to offer today, and it is why moving a single campaign between slots is not part of it: that
/// rewrites the payload and recomputes the digest, which is the step that destroys a save when it
/// is wrong.
///
/// The order of the checks is the same order a restore follows and for the same reasons: throw
/// while the game runs, refuse anything the app does not manage, take a safety copy of the current
/// saves and abort if that copy fails, re-check for the game, then overwrite, then prove the
/// result against its source.
/// </summary>
public sealed class SlotCopyService
{
    private static readonly StringComparer HashComparer = StringComparer.OrdinalIgnoreCase;

    private readonly BackupService _backups;
    private readonly IGameProcessDetector _gameDetector;

    public SlotCopyService(BackupService backups, IGameProcessDetector gameDetector)
    {
        _backups = backups ?? throw new ArgumentNullException(nameof(backups));
        _gameDetector = gameDetector ?? throw new ArgumentNullException(nameof(gameDetector));
    }

    public string SaveRoot => _backups.SaveRoot;

    /// <summary>
    /// Every slot of one realm, slot 1 first, whether or not the file exists. Reading the campaign
    /// detail parses the file, which on a full sav is several megabytes, so a caller that only
    /// needs the file names and sizes can turn it off.
    /// </summary>
    public IReadOnlyList<SlotSide> ReadSlots(SaveRealm realm, bool includeCampaigns = true)
    {
        var sides = new List<SlotSide>(SaveSlotRef.MaxSlot);

        for (int slot = SaveSlotRef.MinSlot; slot <= SaveSlotRef.MaxSlot; slot++)
        {
            sides.Add(ReadSide(new SaveSlotRef(realm, slot), includeCampaigns));
        }

        return sides;
    }

    /// <summary>
    /// Whether any online save file exists at all. The UI hides the online section when this is
    /// false, so it has to be answerable without parsing anything.
    /// </summary>
    public bool HasAnyOnlineSlot()
    {
        for (int slot = SaveSlotRef.MinSlot; slot <= SaveSlotRef.MaxSlot; slot++)
        {
            if (File.Exists(Path.Combine(SaveRoot, new SaveSlotRef(SaveRealm.Online, slot).FileName)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// What a copy would do: which file gets overwritten, how big it is now and what campaigns it
    /// holds, the same for the source, and every reason the copy would be refused. Changes
    /// nothing on disk.
    /// </summary>
    public SlotCopyPlan PlanCopy(SaveSlotRef from, SaveSlotRef to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        var problems = new List<string>();
        var warnings = new List<string>();

        SlotSide source = ReadSide(from, includeCampaigns: true);
        SlotSide target = ReadSide(to, includeCampaigns: true);

        if (!from.IsRealSlot)
        {
            problems.Add(NotASlot(from));
        }

        if (!to.IsRealSlot)
        {
            problems.Add(NotASlot(to));
        }

        if (problems.Count > 0)
        {
            return new SlotCopyPlan(source, target, problems, warnings);
        }

        if (_gameDetector.IsGameRunning(out string? processName))
        {
            problems.Add($"Rain World is running (process \"{processName ?? "Rain World"}\"). Close the game before copying a save slot.");
        }

        if (!source.Exists)
        {
            problems.Add($"{source.FileName} is not in the save folder, so there is nothing to copy from.");
        }
        else if (CanonicalPath.IsLink(source.FullPath))
        {
            problems.Add($"{source.FileName} is a link, and this app copies only real files inside the save folder.");
        }

        // The target has to be a file the backup scope covers, because the safety snapshot taken
        // before the copy is what makes overwriting it undoable. A target outside the scope would
        // be written over with no copy of it anywhere.
        if (!_backups.Scope.IsInScope(target.FileName))
        {
            problems.Add($"{target.FileName} is not one of the files this app manages, so it will not be written to.");
        }
        else if (CanonicalPath.LeadsThroughLink(SaveRoot, target.FullPath))
        {
            problems.Add($"{target.FileName} is a link, so writing to it would land outside the save folder.");
        }

        if (source.Exists && SamePath(source.FullPath, target.FullPath))
        {
            problems.Add($"{source.FileName} and {target.FileName} are the same file, so there is nothing to copy.");
        }

        if (source.Metadata?.ParseError is { } sourceError)
        {
            warnings.Add($"{source.FileName} cannot be read by this app ({sourceError}). It will still be copied exactly as it is.");
        }

        if (target.Exists && target.Metadata?.ParseError is { } targetError)
        {
            warnings.Add($"{target.FileName} cannot be read by this app ({targetError}), so what is about to be replaced cannot be described.");
        }

        int sourceCampaigns = source.Metadata?.Campaigns.Count ?? 0;
        int targetCampaigns = target.Metadata?.Campaigns.Count ?? 0;

        if (source.Exists && sourceCampaigns == 0 && targetCampaigns > 0)
        {
            // A source with records but no SAVE STATE is not an empty file. It is a Rain Meadow
            // online save holding the explored map and the progression record, and the game will
            // still show the slot as having no campaign in it.
            warnings.Add(source.Metadata?.RecordCount > 0
                ? $"{source.FileName} holds no campaign, only map and progression data, so this leaves {target.FileName} with no campaign in it."
                : $"{source.FileName} holds no campaign, so this replaces {target.FileName} with an empty slot.");
        }

        if (source.Metadata?.ChecksumValid == false)
        {
            warnings.Add($"{source.FileName} has a checksum the game will reject, and copying it does not repair that.");
        }

        return new SlotCopyPlan(source, target, problems, warnings);
    }

    /// <summary>
    /// Copies the source slot file onto the target slot file.
    ///
    /// Refusals come back in <see cref="SlotCopyResult.Errors"/> rather than as exceptions, so a
    /// caller reports an unreadable source the same way it reports a failed hash. Two things
    /// leave by the other door. A running game throws <see cref="GameRunningException"/>, which
    /// is what CreateBackup and RestoreBackup do, so one handler covers it wherever it is met.
    /// Cancellation throws too, and is only accepted before the safety snapshot finishes, which
    /// is the last moment when abandoning the job costs nothing.
    /// </summary>
    public SlotCopyResult CopySlot(
        SaveSlotRef from,
        SaveSlotRef to,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var errors = new List<string>();

        // (a) The game holds both files open while it runs, and it writes them on its own schedule.
        // This throws rather than reporting a problem, the same as CreateBackup and RestoreBackup,
        // so one caller handles a running game one way wherever it is met.
        EnsureGameNotRunning();

        // (b) Everything else that can be answered without touching anything.
        SlotCopyPlan plan = PlanCopy(from, to);
        var warnings = new List<string>(plan.Warnings);

        if (!plan.CanCopy)
        {
            errors.AddRange(plan.Problems);
            return Refused(plan, null, errors, warnings);
        }

        ct.ThrowIfCancellationRequested();

        var job = new SlotWriteJob(
            plan.Target,
            plan.Source.FullPath,
            plan.Source.FileName,

            // Null because the source is a live file with no recorded digest to hold it to. The
            // ladder hashes it under the lock and again after the write instead.
            SourceExpectedSha256: null,
            OperationNoun: "copy",
            ProgressVerb: "Copying",
            SafetyLabel: $"Before copying {plan.Source.FileName} onto {plan.Target.FileName}",
            SafetyNote: targetExists => targetExists
                ? $"Automatic copy taken before {plan.Source.FileName} was copied over {plan.Target.FileName} ({plan.Target.Describe()})."
                : $"Automatic copy taken before {plan.Source.FileName} was copied to {plan.Target.FileName}, which did not exist yet.");

        SlotWriteOutcome outcome = CopyOntoSlot(job, progress, ct);

        errors.AddRange(outcome.Errors);
        warnings.AddRange(outcome.Warnings);

        return new SlotCopyResult(
            outcome.Success && errors.Count == 0,
            outcome.SafetySnapshot,
            errors,
            warnings,
            outcome.LiveFolderModified,
            outcome.BytesCopied,
            plan);
    }

    /// <summary>
    /// Writes one file over one slot, with everything that makes overwriting a save undoable.
    ///
    /// This is the ladder, and it is shared rather than copied. A slot copy runs it with a live
    /// file as its source and a library load runs it with a stored save, so a change to the order
    /// of these steps changes both and neither can quietly fall behind the other.
    ///
    /// The order is the point. Take the bytes about to be replaced, take a safety snapshot and
    /// prove it holds them, take the lock, re-check the things the snapshot took long enough to
    /// invalidate, and only then write. Everything before the write returns with the save folder
    /// untouched.
    /// </summary>
    internal SlotWriteOutcome CopyOntoSlot(SlotWriteJob job, IProgress<string>? progress, CancellationToken ct)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        string sourcePath = job.SourcePath;
        string targetPath = job.Target.FullPath;
        string targetName = job.Target.FileName;

        // (c) The bytes that are about to be replaced, so a failed write can be told apart from a
        // write that never started.
        string? targetHashBefore = null;
        bool targetExistedBefore = File.Exists(targetPath);
        if (targetExistedBefore)
        {
            try
            {
                targetHashBefore = Hashing.ComputeFileSha256(targetPath);
            }
            catch (Exception ex)
            {
                errors.Add($"{targetName} could not be read before the {job.OperationNoun} ({ex.Message}), so nothing was changed.");
                return SlotWriteOutcome.Refused(null, errors, warnings);
            }
        }

        // (d) Safety copy of the current live saves. No safety copy, no write.
        BackupSnapshot safety;
        try
        {
            progress?.Report("Saving a copy of the current saves first");
            safety = _backups.CreateBackup(job.SafetyLabel, job.SafetyNote(targetExistedBefore), BackupKind.PreRestoreSafety, progress, ct);
        }
        catch (GameRunningException)
        {
            // The player started the game while the safety copy was being taken. Nothing live has
            // been touched, so this leaves by the same door it came in by.
            throw;
        }
        catch (OperationCanceledException)
        {
            // Nothing live has been touched yet, so cancelling here is clean.
            throw;
        }
        catch (Exception ex)
        {
            errors.Add($"The safety copy of the current saves failed ({ex.Message}), so the {job.OperationNoun} was abandoned and nothing was changed.");
            return SlotWriteOutcome.Refused(null, errors, warnings);
        }

        if (!safety.IsComplete)
        {
            errors.Add($"The safety copy {safety.Id} did not finish ({safety.Problem}), so the {job.OperationNoun} was abandoned and nothing was changed.");
            return SlotWriteOutcome.Refused(safety, errors, warnings);
        }

        // (e) The safety snapshot has to actually hold the file that is about to be replaced.
        // A snapshot that finished without it, because the file was skipped as a link or went
        // away mid scan, leaves this write with nothing to undo it. This is the early out for a
        // target that was already there; the same rule is applied again under the lock, against
        // what is on disk by then.
        if (targetExistedBefore && !SnapshotHolds(safety, targetName))
        {
            errors.Add($"The safety copy {safety.Id} does not hold {targetName}, so overwriting it could not be undone and nothing was changed.");
            return SlotWriteOutcome.Refused(safety, errors, warnings);
        }

        // (f) Hold the backup folder for the rest of the operation, so a restore in another window
        // cannot interleave with this write. Taken after the safety snapshot because that snapshot
        // goes through the same lock.
        IDisposable operationLock;
        try
        {
            operationLock = _backups.AcquireOperationLock();
        }
        catch (BackupBusyException ex)
        {
            errors.Add(ex.Message);
            return SlotWriteOutcome.Refused(safety, errors, warnings);
        }
        catch (Exception ex)
        {
            errors.Add($"The backup folder could not be held for the length of the {job.OperationNoun} ({ex.Message}), so nothing was changed.");
            return SlotWriteOutcome.Refused(safety, errors, warnings);
        }

        using (operationLock)
        {
            // (g) The safety snapshot took several seconds of hashing and copying, and the player
            // may have started the game during it. Nothing has been overwritten yet, so this can
            // still refuse outright.
            EnsureGameNotRunning();

            // (g2) The same window is long enough for Steam Cloud to bring a target file down that
            // was not there when this started. The check at (e) asked about a sample taken before
            // the snapshot, so it says nothing about a file that appeared during it: the snapshot
            // was already enumerated and does not hold it, and overwriting it would leave its bytes
            // nowhere. Ask the disk again, now, inside the lock.
            bool targetExistsNow;
            try
            {
                targetExistsNow = File.Exists(targetPath);
            }
            catch (Exception)
            {
                // A target that cannot even be tested for is not one to overwrite on the strength
                // of an earlier sample.
                targetExistsNow = true;
            }

            if (targetExistsNow && !SnapshotHolds(safety, targetName))
            {
                errors.Add(
                    $"{targetName} appeared while the safety copy {safety.Id} was being taken, so that copy " +
                    "does not hold it and overwriting it could not be undone. Nothing was changed. Wait for Steam " +
                    "Cloud to finish syncing and try again.");
                return SlotWriteOutcome.Refused(safety, errors, warnings);
            }

            long sourceLength;
            string sourceHash;
            try
            {
                var sourceInfo = new FileInfo(sourcePath);
                if (!sourceInfo.Exists)
                {
                    errors.Add($"{job.SourceLabel} went away before it could be copied, so nothing was changed.");
                    return SlotWriteOutcome.Refused(safety, errors, warnings);
                }

                sourceLength = sourceInfo.Length;
                sourceHash = Hashing.ComputeFileSha256(sourcePath);
            }
            catch (Exception ex)
            {
                errors.Add($"{job.SourceLabel} could not be read ({ex.Message}), so nothing was changed.");
                return SlotWriteOutcome.Refused(safety, errors, warnings);
            }

            // A source with a digest recorded against it is held to that digest here, under the
            // lock, immediately before the write. This is what stops a library save that was
            // damaged since it was stored from being written over a live slot.
            if (job.SourceExpectedSha256 is { Length: > 0 } expected && !HashComparer.Equals(sourceHash, expected))
            {
                errors.Add($"{job.SourceLabel} does not match the checksum recorded for it, so nothing was changed.");
                return SlotWriteOutcome.Refused(safety, errors, warnings);
            }

            // (h) The write itself. One File.Copy, no decode, no rewrite, no recomputed checksum.
            progress?.Report($"{job.ProgressVerb} {job.SourceLabel} onto {targetName} ({FormatSize(sourceLength)})");

            try
            {
                ClearReadOnly(targetPath);
                File.Copy(sourcePath, targetPath, overwrite: true);
            }
            catch (Exception ex)
            {
                errors.Add($"{targetName} could not be written: {ex.Message}");
                return new SlotWriteOutcome(
                    false,
                    safety,
                    errors,
                    warnings,
                    TargetChanged(targetPath, targetExistedBefore, targetHashBefore),
                    0);
            }

            // (i) Prove the target is the source. Both sides are hashed again: hashing only the
            // target against a digest taken before the write would pass a source that Steam Cloud
            // rewrote underneath the copy.
            progress?.Report($"Checking {targetName}");

            long bytesCopied = 0;
            try
            {
                var targetInfo = new FileInfo(targetPath);
                if (!targetInfo.Exists)
                {
                    errors.Add($"{targetName} is missing after the {job.OperationNoun}.");
                    return new SlotWriteOutcome(false, safety, errors, warnings, true, 0);
                }

                bytesCopied = targetInfo.Length;

                string targetHash = Hashing.ComputeFileSha256(targetPath);
                string sourceHashAfter = Hashing.ComputeFileSha256(sourcePath);

                if (!HashComparer.Equals(sourceHashAfter, sourceHash))
                {
                    errors.Add($"{job.SourceLabel} changed while it was being copied, so {targetName} cannot be shown to match it. Close Steam, or wait for Steam Cloud to finish syncing, and try again.");
                }
                else if (!HashComparer.Equals(targetHash, sourceHash))
                {
                    errors.Add($"{targetName} does not match {job.SourceLabel} after the {job.OperationNoun}.");
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{targetName} could not be checked after the {job.OperationNoun}: {ex.Message}");
            }

            bool success = errors.Count == 0;
            string finished = char.ToUpperInvariant(job.OperationNoun[0]) + job.OperationNoun[1..] + " finished";
            progress?.Report(success ? finished : finished + " with problems");

            return new SlotWriteOutcome(success, safety, errors, warnings, true, bytesCopied);
        }
    }

    /// <summary>
    /// Formats a byte count the way the backup progress messages do. Public because the copy
    /// confirmation has to print the same size the result headline will print afterwards, and two
    /// formatters is how one operation ends up saying "12 KB" and then "12.0 KB".
    /// </summary>
    public static string FormatSize(long bytes)
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

    /// <summary>
    /// The result shape for every path that stops before the target file is written. The false in
    /// the fifth position is LiveFolderModified, and it is what lets a caller say that nothing was
    /// changed without having to work that out itself.
    /// </summary>
    private static SlotCopyResult Refused(
        SlotCopyPlan plan,
        BackupSnapshot? safety,
        List<string> errors,
        List<string> warnings) =>
        new(false, safety, errors, warnings, false, 0, plan);

    /// <summary>
    /// Describes one slot without changing anything. Public because a library load needs the same
    /// fail-soft reading of its target that a copy needs, and two readers of the same thing is how
    /// two parts of an app come to disagree about whether a file is there.
    /// </summary>
    public SlotSide ReadSide(SaveSlotRef slot, bool includeCampaigns = true)
    {
        string fileName = slot.FileName;
        string fullPath = fileName.Length == 0 ? "" : Path.Combine(SaveRoot, fileName);

        if (fileName.Length == 0)
        {
            return new SlotSide(slot.Realm, slot.Slot, fileName, fullPath, false, 0, null, null);
        }

        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists)
            {
                return new SlotSide(slot.Realm, slot.Slot, fileName, fullPath, false, 0, null, null);
            }

            SlotMetadata? metadata = includeCampaigns
                ? SaveMetadataExtractor.Extract(fullPath, slot.Slot, slot.Realm)
                : null;

            return new SlotSide(slot.Realm, slot.Slot, fileName, fullPath, true, info.Length, info.LastWriteTimeUtc, metadata);
        }
        catch (Exception ex)
        {
            // Describing a slot never throws. A file that cannot even be measured is reported as
            // present and unreadable, which is what the dialog needs to say about it.
            return new SlotSide(
                slot.Realm,
                slot.Slot,
                fileName,
                fullPath,
                true,
                0,
                null,
                new SlotMetadata
                {
                    Slot = slot.Slot,
                    FileName = fileName,
                    Realm = slot.Realm,
                    ParseError = ex.Message,
                });
        }
    }

    /// <summary>
    /// Refuses a slot number the game does not have. Named files are the only thing this class
    /// writes to, so a number outside the range has no file to name and stops here.
    /// </summary>
    private static string NotASlot(SaveSlotRef slot) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "Slot {0} is not a Rain World slot. The game has slots {1} to {2}.",
            slot.Slot,
            SaveSlotRef.MinSlot,
            SaveSlotRef.MaxSlot);

    private void EnsureGameNotRunning()
    {
        if (_gameDetector.IsGameRunning(out string? processName))
        {
            throw new GameRunningException(string.IsNullOrWhiteSpace(processName) ? "Rain World" : processName);
        }
    }

    /// <summary>
    /// Whether the snapshot's manifest lists a root-level file by name.
    /// </summary>
    private static bool SnapshotHolds(BackupSnapshot snapshot, string fileName)
    {
        if (snapshot.Manifest is not { } manifest)
        {
            return false;
        }

        foreach (ManifestFileEntry file in manifest.Files)
        {
            string relative = (file.RelativePath ?? "").Replace('/', '\\').Trim('\\');
            if (string.Equals(relative, fileName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the target file differs from the bytes it held before the copy was attempted.
    /// File.Copy can truncate the destination and then fail, so "the copy threw" is not the same
    /// answer as "the save folder was not changed".
    /// </summary>
    private static bool TargetChanged(string targetPath, bool existedBefore, string? hashBefore)
    {
        try
        {
            bool existsNow = File.Exists(targetPath);

            if (!existedBefore)
            {
                return existsNow;
            }

            if (!existsNow)
            {
                return true;
            }

            return hashBefore is null || !HashComparer.Equals(Hashing.ComputeFileSha256(targetPath), hashBefore);
        }
        catch (Exception)
        {
            // A target that cannot be read cannot be shown to be untouched.
            return true;
        }
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(CanonicalPath.Resolve(left), CanonicalPath.Resolve(right), StringComparison.OrdinalIgnoreCase);

    private static void ClearReadOnly(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            FileAttributes attributes = File.GetAttributes(path);

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
            // If the attribute cannot be cleared the copy below reports the real error.
        }
    }
}
