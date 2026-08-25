// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Globalization;

using RainWorldSaveManager.Core.Backups;
using RainWorldSaveManager.Core.Saves;
using RainWorldSaveManager.Core.Saves.Models;
using RainWorldSaveManager.Core.System;

namespace RainWorldSaveManager.Core.Editing;

/// <summary>
/// What writing an edit did, in the same shape as <see cref="SlotCopyResult"/> and
/// <see cref="Library.LibraryLoadResult"/> so a caller reports all three the same way.
/// </summary>
public sealed record SaveWriteResult(
    bool Success,
    BackupSnapshot? SafetySnapshot,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    bool LiveFolderModified,
    long BytesWritten,
    string TargetFileName)
{
    /// <summary>
    /// The line to lead a report with. The wording lives here rather than in the UI so "nothing was
    /// changed" can never be printed over a save file that was in fact written to.
    /// </summary>
    public string Headline()
    {
        if (Success)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "Saved the changes to {0} ({1}).",
                TargetFileName,
                SlotCopyService.FormatSize(BytesWritten));
        }

        if (!LiveFolderModified)
        {
            return "The changes were not saved, so nothing in the save folder was changed.";
        }

        string? safety = SafetySnapshot?.Id;
        return safety is null
            ? $"The save did not finish and {TargetFileName} may be part written."
            : $"The save did not finish and {TargetFileName} may be part written. Backup {safety} still holds the saves as they were, and restoring it puts them back.";
    }

    internal static SaveWriteResult Refused(string targetFileName, params string[] errors)
        => new(false, null, errors, Array.Empty<string>(), false, 0, targetFileName);
}

/// <summary>
/// Writes an edited save over the slot it came from.
///
/// The ladder that makes overwriting a save undoable is not repeated here. It already exists, a
/// slot copy and a library load both run it, and this runs the same one: the edited bytes are put
/// in a temp file and handed to <see cref="SlotCopyService.CopyOntoSlot"/> as the source. So the
/// safety snapshot, the proof that the snapshot holds the file about to be replaced, the operation
/// lock and the hashing of both sides afterwards all come along without a second implementation to
/// keep in step.
///
/// The safety snapshot is not optional. An edit is the one operation in this app that writes bytes
/// no file anywhere else holds, so the snapshot taken before it is the only copy of what was there.
/// </summary>
public sealed class SaveSlotWriter
{
    private readonly BackupService _backups;
    private readonly IGameProcessDetector _gameDetector;

    public SaveSlotWriter(BackupService backups, IGameProcessDetector gameDetector)
    {
        _backups = backups ?? throw new ArgumentNullException(nameof(backups));
        _gameDetector = gameDetector ?? throw new ArgumentNullException(nameof(gameDetector));
    }

    /// <summary>
    /// Writes a plan over its slot.
    ///
    /// Refusals come back in <see cref="SaveWriteResult.Errors"/>. A running game throws
    /// <see cref="GameRunningException"/>, the same way the copy and the restore do, so one handler
    /// covers it wherever it is met.
    /// </summary>
    public SaveWriteResult Write(
        SaveWritePlan plan,
        SaveSlotRef target,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(target);

        // The game holds the save files open while it runs and writes them on its own schedule.
        EnsureGameNotRunning();

        SlotSide side = _backups.SlotCopies.ReadSide(target, includeCampaigns: false);
        string targetName = side.FileName.Length == 0 ? "the target slot" : side.FileName;

        if (!target.IsRealSlot)
        {
            return SaveWriteResult.Refused(
                targetName,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Slot {0} is not a Rain World slot. The game has slots {1} to {2}.",
                    target.Slot,
                    SaveSlotRef.MinSlot,
                    SaveSlotRef.MaxSlot));
        }

        // The plan was built from one file's bytes. Writing it to a different slot would put one
        // campaign's edit into another slot's file, which is a different feature entirely.
        if (!SamePath(side.FullPath, plan.FilePath))
        {
            return SaveWriteResult.Refused(
                targetName,
                $"These changes were made to {Path.GetFileName(plan.FilePath)}, so they will not be written to {targetName}.");
        }

        if (!plan.CanWrite)
        {
            return SaveWriteResult.Refused(targetName, plan.Problems.ToArray());
        }

        if (plan.IsNoOp)
        {
            return SaveWriteResult.Refused(targetName, "Nothing was changed, so there is nothing to save.");
        }

        if (!side.Exists)
        {
            return SaveWriteResult.Refused(targetName, $"{targetName} is no longer in the save folder, so the changes were not saved.");
        }

        ct.ThrowIfCancellationRequested();

        string tempPath = Path.Combine(
            Path.GetTempPath(),
            "rwsm-edit-" + Guid.NewGuid().ToString("n") + ".tmp");

        try
        {
            if (StageEdit(plan, tempPath, side, targetName, out SaveWriteResult? refusal))
            {
                return refusal!;
            }

            var job = new SlotWriteJob(
                side,
                tempPath,
                "the edited save",
                plan.NewBytesSha256,
                OperationNoun: "save",
                ProgressVerb: "Saving",
                SafetyLabel: $"Before editing {side.FileName}",
                SafetyNote: _ => BuildSafetyNote(side.FileName, plan),
                TargetExpectedSha256: plan.ExpectedFileSha256);

            SlotWriteOutcome outcome = _backups.SlotCopies.CopyOntoSlot(job, progress, ct);

            return new SaveWriteResult(
                outcome.Success && outcome.Errors.Count == 0,
                outcome.SafetySnapshot,
                outcome.Errors,
                outcome.Warnings,
                outcome.LiveFolderModified,
                outcome.BytesCopied,
                side.FileName);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    /// <summary>
    /// Puts the edited bytes on disk and reads them back before anything is overwritten.
    ///
    /// The plan already proved itself in memory. This proves the same things again about a real
    /// file read by the ordinary reader, which is the last check available that costs nothing: a
    /// disk that wrote the bytes wrongly, or a container that only decodes while it is a string in
    /// this process, is caught while the save folder is still untouched.
    /// </summary>
    private static bool StageEdit(
        SaveWritePlan plan,
        string tempPath,
        SlotSide side,
        string targetName,
        out SaveWriteResult? refusal)
    {
        try
        {
            File.WriteAllBytes(tempPath, plan.NewBytes);
        }
        catch (Exception ex)
        {
            refusal = SaveWriteResult.Refused(targetName, $"The edited save could not be prepared ({ex.Message}), so nothing was changed.");
            return true;
        }

        try
        {
            SaveContainer container = SaveContainer.Read(tempPath);
            if (container.StructureProblem is { } problem)
            {
                refusal = SaveWriteResult.Refused(targetName, $"The edited save did not read back correctly ({problem}), so nothing was changed.");
                return true;
            }
        }
        catch (Exception ex)
        {
            refusal = SaveWriteResult.Refused(targetName, $"The edited save did not read back as a save file ({ex.Message}), so nothing was changed.");
            return true;
        }

        SlotMetadata metadata = SaveMetadataExtractor.Extract(tempPath, side.Slot, side.Realm);

        if (metadata.ParseError is { } parseError)
        {
            refusal = SaveWriteResult.Refused(targetName, $"The edited save could not be read back ({parseError}), so nothing was changed.");
            return true;
        }

        if (metadata.ChecksumValid != true)
        {
            // This is the failure that wipes a slot, so it is checked against the file rather than
            // against the string the file was built from.
            refusal = SaveWriteResult.Refused(
                targetName,
                "The edited save came out with a checksum the game would reject, so nothing was changed.");
            return true;
        }

        refusal = null;
        return false;
    }

    /// <summary>
    /// The note recorded in the safety snapshot, listing what the edit was about to do. A snapshot
    /// found weeks later says which change it was taken before rather than only when.
    /// </summary>
    private static string BuildSafetyNote(string fileName, SaveWritePlan plan)
    {
        const int Listed = 8;

        string changes = string.Join("; ", plan.ChangeDescriptions.Take(Listed));

        if (plan.ChangeDescriptions.Count > Listed)
        {
            changes += string.Format(
                CultureInfo.InvariantCulture,
                " and {0} more changes",
                plan.ChangeDescriptions.Count - Listed);
        }

        return $"Automatic copy taken before {fileName} was edited: {changes}.";
    }

    private void EnsureGameNotRunning()
    {
        if (_gameDetector.IsGameRunning(out string? processName))
        {
            throw new GameRunningException(string.IsNullOrWhiteSpace(processName) ? "Rain World" : processName);
        }
    }

    private static bool SamePath(string left, string right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
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
            // A temp file left behind is litter in the system temp folder, not a failed save, and
            // reporting it would say the save went wrong when it did not.
        }
    }
}
