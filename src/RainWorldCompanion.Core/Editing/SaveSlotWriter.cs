// Usings sit above the namespace declaration on purpose. RainWorldCompanion.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Globalization;

using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.Core.Saves.Models;
using RainWorldCompanion.Core.System;

namespace RainWorldCompanion.Core.Editing;

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
/// What moving a campaign onto or off one slot would do, worked out without changing anything.
///
/// <see cref="Splice"/> says what the move is: whether it replaces a campaign the slot already had,
/// how much map comes with it, and anything the game will make of it that a person would not expect.
/// <see cref="Write"/> is the same plan an edit builds, so writing one runs the same ladder.
/// </summary>
public sealed record CampaignMovePlan(
    SaveWritePlan Write,
    CampaignSpliceReport Splice,
    SaveSlotRef Target,
    string TargetFileName,
    IReadOnlyList<string> Problems)
{
    public bool CanWrite => Problems.Count == 0 && Write.CanWrite;

    /// <summary>What is worth saying before this is written. None of it stops the write.</summary>
    public IReadOnlyList<string> Warnings => Splice.Warnings;

    /// <summary>One line saying what this would do to the slot.</summary>
    public string Describe()
    {
        string what = Splice.Outcome switch
        {
            CampaignSpliceOutcome.Replaced => $"Replaces the campaign in {TargetFileName}",
            CampaignSpliceOutcome.Added => $"Adds a campaign to {TargetFileName}",
            CampaignSpliceOutcome.Removed => $"Takes a campaign out of {TargetFileName}",
            _ => $"Changes nothing in {TargetFileName}",
        };

        var map = new List<string>();

        if (Splice.MapsCarried > 0)
        {
            map.Add(string.Format(
                CultureInfo.InvariantCulture,
                "{0} regions of map come with it",
                Splice.MapsCarried));
        }

        if (Splice.MapsRemoved > 0)
        {
            map.Add(string.Format(
                CultureInfo.InvariantCulture,
                "{0} the slot had are dropped",
                Splice.MapsRemoved));
        }

        return map.Count == 0 ? what + "." : what + ", and " + string.Join(", ", map) + ".";
    }

    internal static CampaignMovePlan Refused(
        string filePath,
        SaveSlotRef target,
        string targetFileName,
        params string[] problems)
        => new(
            SaveWritePlan.CannotBuild(filePath, problems),
            CampaignSpliceReport.Nothing,
            target,
            targetFileName,
            problems);
}

/// <summary>
/// What emptying one slot of its campaigns would do, worked out without changing anything.
///
/// The game's own WipeAll leaves a slot holding nothing but a reset MISCPROG. This app stops short
/// of that: it takes the campaigns out and leaves MISCPROG as it found it, because rebuilding that
/// record would drop every field of it this app does not model, which on a modded save is most of
/// them. <see cref="Describe"/> says what is actually about to happen rather than what the game
/// would have done.
/// </summary>
/// <param name="Campaigns">The campaigns about to go, by the name a person reads.</param>
/// <param name="MapsRemoved">How many map records go with them, which is none unless asked for.</param>
/// <param name="TakingTheMap">Whether the map discovery was asked to go too.</param>
public sealed record SlotDeletePlan(
    SaveWritePlan Write,
    SaveSlotRef Target,
    string TargetFileName,
    IReadOnlyList<string> Campaigns,
    int MapsRemoved,
    SlotDeleteDepth Depth,
    int OtherRecordsRemoved,
    bool ClearingTheGamesOwnCopy,
    IReadOnlyList<string> Problems)
{
    public bool CanWrite => Problems.Count == 0 && Write.CanWrite;

    /// <summary>True when the slot is being left as empty as one never played.</summary>
    public bool IsTotal => Depth == SlotDeleteDepth.Everything;

    /// <summary>One line saying what this takes out of the slot.</summary>
    public string Describe()
    {
        if (IsTotal)
        {
            return Campaigns.Count == 0
                ? $"Empties {TargetFileName} out entirely, leaving it as it was before it was ever played."
                : string.Format(
                    CultureInfo.InvariantCulture,
                    "Empties {0} out entirely: {1}, the map, the progression record and everything else in it.",
                    TargetFileName,
                    Campaigns.Count == 1 ? Campaigns[0] : Campaigns.Count + " campaigns");
        }

        string what = Campaigns.Count switch
        {
            0 => $"Takes nothing out of {TargetFileName}",
            1 => $"Takes {Campaigns[0]} out of {TargetFileName}",
            _ => string.Format(
                CultureInfo.InvariantCulture,
                "Takes all {0} campaigns out of {1}",
                Campaigns.Count,
                TargetFileName),
        };

        if (MapsRemoved > 0)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}, and the {1} regions of map it holds.",
                what,
                MapsRemoved);
        }

        return Depth == SlotDeleteDepth.CampaignsAndMap
            ? what + "."
            : what + ", and leaves the map they explored behind.";
    }

    /// <summary>
    /// What the slot still holds afterwards. Worth saying out loud, because a slot cleared of its
    /// campaigns is not an untouched one and the game will not treat it as new.
    /// </summary>
    public string WhatStays
    {
        get
        {
            if (IsTotal)
            {
                return ClearingTheGamesOwnCopy
                    ? $"Nothing stays. The copy of the old save the game keeps inside {TargetFileName} goes as well."
                    : "Nothing stays.";
            }

            return Depth == SlotDeleteDepth.CampaignsAndMap
                ? $"{TargetFileName} keeps its progression record, so the game still counts what it has seen."
                : $"{TargetFileName} keeps the map and its progression record, so the game still counts what it has seen.";
        }
    }

    internal static SlotDeletePlan Refused(
        string filePath,
        SaveSlotRef target,
        string targetFileName,
        params string[] problems)
        => new(
            SaveWritePlan.CannotBuild(filePath, problems),
            target,
            targetFileName,
            Array.Empty<string>(),
            0,
            SlotDeleteDepth.Campaigns,
            0,
            false,
            problems);
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

    /// <summary>Empties a slot, through the same ladder an edit runs.</summary>
    public SaveWriteResult Write(
        SlotDeletePlan plan,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return plan.Problems.Count > 0
            ? SaveWriteResult.Refused(plan.TargetFileName, plan.Problems.ToArray())
            : Write(plan.Write, plan.Target, progress, ct);
    }

    /// <summary>Writes a campaign move over its slot, through the same ladder an edit runs.</summary>
    public SaveWriteResult Write(
        CampaignMovePlan plan,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return plan.Problems.Count > 0
            ? SaveWriteResult.Refused(plan.TargetFileName, plan.Problems.ToArray())
            : Write(plan.Write, plan.Target, progress, ct);
    }

    /// <summary>Reads one campaign out of a slot, to store it or to move it somewhere else.</summary>
    public CampaignSlice? ReadCampaign(SaveSlotRef source, string slugcatId)
    {
        ArgumentNullException.ThrowIfNull(source);

        SlotSide side = _backups.SlotCopies.ReadSide(source, includeCampaigns: false);
        return SaveEditSession.Open(side.FullPath).TakeCampaign(slugcatId);
    }

    /// <summary>
    /// What putting a campaign into a slot would do. Nothing is written and nothing is locked, so
    /// the answer is what the slot holds now rather than what it will hold when the write runs.
    /// </summary>
    public CampaignMovePlan PlanPutCampaign(SaveSlotRef target, CampaignSlice slice)
    {
        ArgumentNullException.ThrowIfNull(slice);

        return Plan(target, session => session.PutCampaignIn(slice));
    }

    /// <summary>
    /// What taking a campaign out of a slot would do.
    /// </summary>
    /// <param name="includeMaps">
    /// Whether the slugcat's map discovery goes with it. The game's own WipeSaveState leaves it
    /// behind, so deleting a campaign in place should too. Moving one to another slot should take
    /// it, or the map stays in a slot that no longer has the campaign.
    /// </param>
    public CampaignMovePlan PlanTakeCampaign(SaveSlotRef target, string slugcatId, bool includeMaps)
        => Plan(target, session => session.TakeCampaignOut(slugcatId, includeMaps));

    /// <summary>
    /// What wiping a slot would do: every campaign in it taken out, worked out without changing
    /// anything.
    /// </summary>
    /// <param name="includeMaps">
    /// Whether each slugcat's map discovery goes with its campaign. The game's own wipe drops the
    /// map, so true is the closer match; false leaves the slot remembering everywhere it has been.
    /// </param>
    public SlotDeletePlan PlanDeleteSlot(SaveSlotRef target, SlotDeleteDepth depth)
    {
        SlotEdit open = OpenSlot(target);

        if (open.Refusal is { } refusal)
        {
            return SlotDeletePlan.Refused(open.Side.FullPath, target, open.Name, refusal);
        }

        SaveEditSession session = open.Session!;
        SlotDeleteReport gone = session.DeleteCampaigns(depth);

        if (!session.IsDirty)
        {
            return SlotDeletePlan.Refused(
                open.Side.FullPath,
                target,
                open.Name,
                $"{open.Name} is already as empty as it can be, so there is nothing in it to delete.");
        }

        return new SlotDeletePlan(
            session.BuildWritePlan(),
            target,
            open.Name,
            gone.Campaigns,
            gone.MapsRemoved,
            depth,
            gone.OtherRecordsRemoved,
            gone.ClearedTheGamesOwnCopy,
            Array.Empty<string>());
    }

    private CampaignMovePlan Plan(SaveSlotRef target, Func<SaveEditSession, CampaignSpliceReport> move)
    {
        SlotEdit open = OpenSlot(target);

        if (open.Refusal is { } refusal)
        {
            return CampaignMovePlan.Refused(open.Side.FullPath, target, open.Name, refusal);
        }

        SaveEditSession session = open.Session!;
        CampaignSpliceReport splice = move(session);

        if (!session.IsDirty)
        {
            return CampaignMovePlan.Refused(
                open.Side.FullPath,
                target,
                open.Name,
                $"{open.Name} already holds exactly this, so there is nothing to write.");
        }

        return new CampaignMovePlan(
            session.BuildWritePlan(),
            splice,
            target,
            open.Name,
            Array.Empty<string>());
    }

    /// <summary>
    /// A slot opened for a change, or the reason it was not.
    ///
    /// Every plan that rewrites a slot asks the same four questions first, and asking them once
    /// keeps a new kind of change from quietly skipping one of them.
    /// </summary>
    private SlotEdit OpenSlot(SaveSlotRef target)
    {
        ArgumentNullException.ThrowIfNull(target);

        SlotSide side = _backups.SlotCopies.ReadSide(target, includeCampaigns: false);
        string targetName = side.FileName.Length == 0 ? "the target slot" : side.FileName;

        if (!target.IsRealSlot)
        {
            return new SlotEdit(side, targetName, null, string.Format(
                CultureInfo.InvariantCulture,
                "Slot {0} is not a Rain World slot. The game has slots {1} to {2}.",
                target.Slot,
                SaveSlotRef.MinSlot,
                SaveSlotRef.MaxSlot));
        }

        if (!side.Exists)
        {
            return new SlotEdit(
                side, targetName, null, $"{targetName} is not in the save folder, so there is nothing to change in it.");
        }

        // A plan read while the game is running describes a slot the game is about to rewrite. The
        // write refuses on its own, but saying so here keeps it out of the dialog that asks.
        if (_gameDetector.IsGameRunning(out string? processName))
        {
            return new SlotEdit(
                side,
                targetName,
                null,
                $"Rain World is running (process \"{processName ?? "Rain World"}\"). Close the game first.");
        }

        try
        {
            return new SlotEdit(side, targetName, SaveEditSession.Open(side.FullPath), null);
        }
        catch (SaveContainerException ex)
        {
            return new SlotEdit(side, targetName, null, ex.Message);
        }
    }

    /// <summary>One slot open for a change, or the reason it is not.</summary>
    private sealed record SlotEdit(SlotSide Side, string Name, SaveEditSession? Session, string? Refusal);

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
