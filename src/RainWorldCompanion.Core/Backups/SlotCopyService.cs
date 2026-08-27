// RainWorldCompanion.Core.System exists in this assembly, so a using written inside the namespace
// body would bind "System" to that namespace instead of the BCL root.
using System.Globalization;
using RainWorldCompanion.Core.Mods;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.Core.Saves.Models;
using RainWorldCompanion.Core.System;

namespace RainWorldCompanion.Core.Backups;

/// <summary><see cref="Metadata"/> is null when the file is not there, and present with a ParseError
/// set when the file is there and could not be read.</summary>
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

        // SlotMetadata.Describe() leads with "Slot n". Here the file name leads instead.
        string body = metadata.Describe();
        int colon = body.IndexOf(':');
        if (colon >= 0)
        {
            body = body[(colon + 1)..].Trim();
        }

        return string.Format(CultureInfo.InvariantCulture, "{0} ({1}): {2}", FileName, size, body);
    }
}

public sealed record SlotCopyPlan(
    SlotSide Source,
    SlotSide Target,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Warnings)
{
    public bool CanCopy => Problems.Count == 0;
}

/// <summary><see cref="LiveFolderModified"/> is false only when the target file is provably the same
/// bytes it was before the operation started.</summary>
public sealed record SlotCopyResult(
    bool Success,
    BackupSnapshot? SafetySnapshot,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    bool LiveFolderModified,
    long BytesCopied,
    SlotCopyPlan? Plan)
{
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

/// <summary><paramref name="SourceExpectedSha256"/> is the digest the source is held to under the
/// lock. Null means the source has none: a live slot has nothing recorded, a library save does.</summary>
/// <param name="SafetyNote">Called with whether the target file already exists.</param>
/// <param name="TargetExpectedSha256">The digest the target is held to under the lock, or null to
/// write over whatever is there. An edit sets it, because writing it over a slot the game has since
/// played would put back an edited copy of an older save.</param>
/// <param name="Extras">Files written after the slot has landed and been proved, or null for none.
/// The job still has exactly one target slot, and these are named as what they are.</param>
internal sealed record SlotWriteJob(
    SlotSide Target,
    string SourcePath,
    string SourceLabel,
    string? SourceExpectedSha256,
    string OperationNoun,
    string ProgressVerb,
    string SafetyLabel,
    Func<bool, string> SafetyNote,
    string? TargetExpectedSha256 = null,
    IReadOnlyList<ExtraFileWrite>? Extras = null,
    ModListSnapshot? SafetyMods = null);

/// <summary>
/// One more file to write into the save folder beside the slot, named the way a manifest names it:
/// relative to the save folder, so the scope can be asked about it and a destination resolved from
/// it with nothing in between.
///
/// <para>Public so a caller outside Core can name one. That grants no privilege: the writer holds
/// every one of these to the scope, the safety snapshot, the link check and the recorded hash
/// before it writes anything.</para>
/// </summary>
/// <param name="Label">What a warning calls this file when it could not be written.</param>
public sealed record ExtraFileWrite(
    string RelativePath,
    string SourcePath,
    string? ExpectedSha256,
    string Label);

internal sealed record SlotWriteOutcome(
    bool Success,
    BackupSnapshot? SafetySnapshot,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    bool LiveFolderModified,
    long BytesCopied)
{
    /// <summary>How many of <see cref="SlotWriteJob.Extras"/> landed. Deliberately outside
    /// <see cref="Success"/>, which is about the slot.</summary>
    internal int ExtrasWritten { get; init; }

    internal static SlotWriteOutcome Refused(
        BackupSnapshot? safety,
        IReadOnlyList<string> errors,
        IReadOnlyList<string> warnings) =>
        new(false, safety, errors, warnings, false, 0);
}

/// <summary>
/// Copies one whole save slot file onto another, byte for byte. Nothing here parses or rewrites a
/// save: the operation is File.Copy and nothing else, so the UTF-8 BOM, the trailing NUL padding
/// and the MD5 digest inside the payload all arrive unchanged.
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

    /// <summary>Every slot of one realm, slot 1 first, whether or not the file exists. Reading the
    /// campaign detail parses the file, which on a full sav is several megabytes.</summary>
    public IReadOnlyList<SlotSide> ReadSlots(SaveRealm realm, bool includeCampaigns = true)
    {
        var sides = new List<SlotSide>(SaveSlotRef.MaxSlot);

        for (int slot = SaveSlotRef.MinSlot; slot <= SaveSlotRef.MaxSlot; slot++)
        {
            sides.Add(ReadSide(new SaveSlotRef(realm, slot), includeCampaigns));
        }

        return sides;
    }

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

    /// <summary>Changes nothing on disk.</summary>
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

        // The target has to be in the backup scope: the safety snapshot taken before the copy is
        // what makes overwriting it undoable.
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
            // A source with records but no SAVE STATE is not empty: it is a Rain Meadow online save
            // holding the explored map and the progression record.
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

    /// <summary>Refusals come back in <see cref="SlotCopyResult.Errors"/>. Two things leave by the
    /// other door: a running game throws <see cref="GameRunningException"/>, and cancellation throws
    /// and is only accepted before the safety snapshot finishes.</summary>
    public SlotCopyResult CopySlot(
        SaveSlotRef from,
        SaveSlotRef to,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var errors = new List<string>();

        EnsureGameNotRunning();

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
    /// Writes one file over one slot. Shared rather than copied: a slot copy runs it with a live
    /// file as its source and a library load with a stored save. The order is the point. Take the
    /// bytes about to be replaced, take a safety snapshot and prove it holds them, take the lock,
    /// re-check what the snapshot took long enough to invalidate, and only then write.
    /// </summary>
    internal SlotWriteOutcome CopyOntoSlot(SlotWriteJob job, IProgress<string>? progress, CancellationToken ct)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        string sourcePath = job.SourcePath;
        string targetPath = job.Target.FullPath;
        string targetName = job.Target.FileName;

        // The bytes about to be replaced, so a failed write can be told from one that never started.
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

        BackupSnapshot safety;
        try
        {
            progress?.Report("Saving a copy of the current saves first");
            safety = _backups.CreateBackup(
                job.SafetyLabel,
                job.SafetyNote(targetExistedBefore),
                BackupKind.PreRestoreSafety,
                progress,
                ct,
                job.SafetyMods);
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
            errors.Add($"The safety copy of the current saves failed ({ex.Message}), so the {job.OperationNoun} was abandoned and nothing was changed.");
            return SlotWriteOutcome.Refused(null, errors, warnings);
        }

        if (!safety.IsComplete)
        {
            errors.Add($"The safety copy {safety.Id} did not finish ({safety.Problem}), so the {job.OperationNoun} was abandoned and nothing was changed.");
            return SlotWriteOutcome.Refused(safety, errors, warnings);
        }

        // The safety snapshot has to hold the file about to be replaced, or this write has nothing
        // to undo it. The same rule is applied again under the lock, against what is on disk then.
        if (targetExistedBefore && !SnapshotHolds(safety, targetName))
        {
            errors.Add($"The safety copy {safety.Id} does not hold {targetName}, so overwriting it could not be undone and nothing was changed.");
            return SlotWriteOutcome.Refused(safety, errors, warnings);
        }

        // Taken after the safety snapshot, because that snapshot goes through the same lock.
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
            // The safety snapshot took several seconds, so the player may have started the game
            // during it. Nothing has been overwritten yet, so this can still refuse outright.
            EnsureGameNotRunning();

            // That window is long enough for Steam Cloud to bring down a target that was not there
            // when this started, which the snapshot was already enumerated without. Ask again, now,
            // inside the lock.
            bool targetExistsNow;
            try
            {
                targetExistsNow = File.Exists(targetPath);
            }
            catch (Exception)
            {
                // A target that cannot be tested for is not one to overwrite on an earlier sample.
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

            // A job built from the target's own bytes is only valid while the target still holds
            // them.
            if (job.TargetExpectedSha256 is { Length: > 0 } expectedTarget)
            {
                string? targetHashNow = null;
                try
                {
                    targetHashNow = targetExistsNow ? Hashing.ComputeFileSha256(targetPath) : null;
                }
                catch (Exception ex)
                {
                    errors.Add($"{targetName} could not be read before the {job.OperationNoun} ({ex.Message}), so nothing was changed.");
                    return SlotWriteOutcome.Refused(safety, errors, warnings);
                }

                if (targetHashNow is null || !HashComparer.Equals(targetHashNow, expectedTarget))
                {
                    errors.Add(
                        $"{targetName} is not the file this {job.OperationNoun} was built from. It has been written to since, " +
                        "so nothing was changed. Reopen the save and make the change again.");
                    return SlotWriteOutcome.Refused(safety, errors, warnings);
                }
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

            // Held to its recorded digest here, under the lock, immediately before the write: this
            // stops a library save damaged since it was stored from reaching a live slot.
            if (job.SourceExpectedSha256 is { Length: > 0 } expected && !HashComparer.Equals(sourceHash, expected))
            {
                errors.Add($"{job.SourceLabel} does not match the checksum recorded for it, so nothing was changed.");
                return SlotWriteOutcome.Refused(safety, errors, warnings);
            }

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

            // Both sides are hashed again: hashing only the target against a digest taken before the
            // write would pass a source that Steam Cloud rewrote underneath the copy.
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

            // Only once the save itself is right. There is no sense adopting settings for a save
            // that did not land, and once it has, a settings file that will not write is a warning
            // rather than a reason to put everything back.
            int extrasWritten = errors.Count == 0 && job.Extras is { Count: > 0 } extras
                ? WriteExtras(extras, safety, warnings, progress)
                : 0;

            bool success = errors.Count == 0;
            string finished = char.ToUpperInvariant(job.OperationNoun[0]) + job.OperationNoun[1..] + " finished";
            progress?.Report(success ? finished : finished + " with problems");

            return new SlotWriteOutcome(success, safety, errors, warnings, true, bytesCopied)
            {
                ExtrasWritten = extrasWritten,
            };
        }
    }

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

    private static SlotCopyResult Refused(
        SlotCopyPlan plan,
        BackupSnapshot? safety,
        List<string> errors,
        List<string> warnings) =>
        new(false, safety, errors, warnings, false, 0, plan);

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
            // A file that cannot even be measured is reported as present and unreadable.
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
    /// Writes the files a job carries beside its slot, and answers with how many landed. Each
    /// failure is a warning naming the file and the reason, never an error: the save has already
    /// landed by this point, and reporting a settings file as a failure would say it did not.
    /// </summary>
    private int WriteExtras(
        IReadOnlyList<ExtraFileWrite> extras,
        BackupSnapshot safety,
        List<string> warnings,
        IProgress<string>? progress)
    {
        int written = 0;

        foreach (ExtraFileWrite extra in extras)
        {
            string? problem = WriteExtra(extra, safety, progress);

            if (problem is null)
            {
                written++;
            }
            else
            {
                warnings.Add($"{extra.Label} was not written: {problem}.");
            }
        }

        return written;
    }

    /// <summary>Why the file was not written, or null when it was.</summary>
    private string? WriteExtra(ExtraFileWrite extra, BackupSnapshot safety, IProgress<string>? progress)
    {
        string relative = extra.RelativePath ?? "";

        // Being in scope under today's rules is what makes this undoable: the safety snapshot was
        // taken under those same rules, so restoring it puts the file back.
        if (!_backups.Scope.IsInScope(relative))
        {
            return "it is not one of the files this app manages";
        }

        if (!BackupService.TryResolveInside(SaveRoot, relative, out string destination))
        {
            return "its name does not resolve to a file inside the save folder";
        }

        // Resolving is textual, and text cannot see a junction. A copy onto a link writes through
        // it, over a file the safety snapshot never took.
        if (CanonicalPath.LeadsThroughLink(SaveRoot, destination))
        {
            return "it is a link, so writing to it would land outside the save folder";
        }

        bool exists;
        try
        {
            exists = File.Exists(destination);
        }
        catch (Exception)
        {
            exists = true;
        }

        // A file already there has to be in the safety snapshot, or overwriting it could not be
        // undone. One that is not there needs no entry: restoring the snapshot deletes it.
        if (exists && !SnapshotHolds(safety, relative))
        {
            return $"the safety copy {safety.Id} does not hold it, so overwriting it could not be undone";
        }

        try
        {
            var info = new FileInfo(extra.SourcePath);
            if (!info.Exists)
            {
                return "it is not there any more";
            }

            string sourceHash = Hashing.ComputeFileSha256(extra.SourcePath);
            if (extra.ExpectedSha256 is { Length: > 0 } expected && !HashComparer.Equals(sourceHash, expected))
            {
                return "it does not match the checksum recorded for it";
            }

            string? parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            progress?.Report($"Writing {extra.Label}");

            ClearReadOnly(destination);
            File.Copy(extra.SourcePath, destination, overwrite: true);

            if (!HashComparer.Equals(Hashing.ComputeFileSha256(destination), sourceHash))
            {
                return "the copy does not match what it was copied from";
            }
        }
        catch (Exception ex)
        {
            return ex.Message;
        }

        return null;
    }

    /// <summary>Whether the snapshot's manifest lists a file, by its whole path below the save
    /// folder.</summary>
    private static bool SnapshotHolds(BackupSnapshot snapshot, string relativePath)
    {
        if (snapshot.Manifest is not { } manifest)
        {
            return false;
        }

        string wanted = Flatten(relativePath);

        foreach (ManifestFileEntry file in manifest.Files)
        {
            if (string.Equals(Flatten(file.RelativePath ?? ""), wanted, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string Flatten(string relativePath) => relativePath.Replace('/', '\\').Trim('\\');

    /// <summary>File.Copy can truncate the destination and then fail, so "the copy threw" is not
    /// the same answer as "the save folder was not changed".</summary>
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
}
