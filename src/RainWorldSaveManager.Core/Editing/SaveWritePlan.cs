// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using RainWorldSaveManager.Core.Backups;
using RainWorldSaveManager.Core.Saves;

namespace RainWorldSaveManager.Core.Editing;

/// <summary>
/// What a campaign splice said it would do to the record list.
///
/// A field edit changes characters inside one record and leaves the record list alone, so it is
/// checked by position. Moving a campaign changes the list itself, and there is no position left to
/// check against, so it is checked by what moved: take these records out of the old payload and
/// those out of the new one, and what remains on each side has to be the same records in the same
/// order.
/// </summary>
internal sealed record RecordSetChange(IReadOnlyList<string> Written, IReadOnlyList<string> Removed);

/// <summary>
/// An edited save, built and checked in memory, ready to be written or reported on.
///
/// Building one touches no files. That is the point: every way an edit can be wrong is found while
/// the only copy of it is in memory, so a plan that reaches the writer has already been proved to
/// decode back to what it was meant to say.
/// </summary>
/// <param name="ExpectedFileSha256">
/// What the file hashed to when the session opened. The writer refuses if the file on disk no
/// longer matches, because that means something else wrote to the slot while the edit was open.
/// </param>
public sealed record SaveWritePlan(
    string FilePath,
    string ExpectedFileSha256,
    byte[] NewBytes,
    string NewBytesSha256,
    long OldLength,
    long NewLength,
    IReadOnlyList<string> ChangeDescriptions,
    IReadOnlyList<string> Problems)
{
    /// <summary>True when nothing found a reason to refuse.</summary>
    public bool CanWrite => Problems.Count == 0 && NewBytes.Length > 0;

    /// <summary>True when the edits cancelled out and the file would be written exactly as it is.</summary>
    public bool IsNoOp => ChangeDescriptions.Count == 0;

    internal static SaveWritePlan Build(
        SaveEditSession session,
        ContainerText container,
        string originalPayload,
        string newPayload,
        IReadOnlySet<int> touchedRecords,
        IReadOnlyList<string> changes,
        RecordSetChange? spliced,
        SizePolicy policy)
    {
        var problems = new List<string>();

        // Records are addressed by position while only their contents change, and by what moved
        // once the list itself changes. A session that did both has no single answer to check
        // against, so it is refused rather than checked the weaker of the two ways. Nothing in the
        // app does both: moving a campaign runs in its own session.
        if (spliced is not null && touchedRecords.Count > 0)
        {
            return Refused(
                session,
                container,
                changes,
                "This save has both edited fields and a campaign moved in or out, and those are written one at a time.");
        }

        // Everything below reads the bytes that would be written, rather than the values they were
        // built from. A check that asks the model what it meant proves only that the model is
        // self-consistent; asking the bytes proves what the game will read.
        byte[] newBytes;
        try
        {
            newBytes = container.WithValue("save", SaveChecksum.Wrap(newPayload)).ToBytes(policy);
        }
        catch (SaveContainerException ex)
        {
            return Refused(session, container, changes, ex.Message);
        }

        ContainerText written;
        try
        {
            written = ContainerText.Load(newBytes);
        }
        catch (SaveContainerException ex)
        {
            return Refused(session, container, changes, $"The edited save did not read back as a save container ({ex.Message}).");
        }

        CheckEveryOtherEntryIsUntouched(container, written, problems);
        CheckTheGameWouldAcceptTheChecksum(written, newPayload, problems);

        if (spliced is null)
        {
            CheckOnlyTheEditedRecordsChanged(originalPayload, newPayload, touchedRecords, problems);
        }
        else
        {
            CheckOnlyTheSplicedRecordsMoved(originalPayload, newPayload, spliced, problems);
        }

        return new SaveWritePlan(
            session.FilePath,
            session.FileSha256,
            newBytes,
            Hashing.ComputeSha256(newBytes),
            container.OriginalLength,
            newBytes.Length,
            changes.ToArray(),
            problems);
    }

    /// <summary>
    /// Every entry but the edited one has to come back character for character. save__Backup is
    /// the one that matters most: it is the game's own previous revision, and leaving it alone is
    /// what gives a bad edit something to fall back to.
    /// </summary>
    private static void CheckEveryOtherEntryIsUntouched(ContainerText before, ContainerText after, List<string> problems)
    {
        if (!before.Keys.SequenceEqual(after.Keys, StringComparer.Ordinal))
        {
            problems.Add("The edited save does not hold the same entries as the file it came from.");
            return;
        }

        foreach (string key in before.Keys)
        {
            if (string.Equals(key, "save", StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.Equals(before.GetValueRaw(key), after.GetValueRaw(key), StringComparison.Ordinal))
            {
                problems.Add($"The edit changed the '{key}' entry, which it was not meant to touch.");
            }
        }
    }

    private static void CheckTheGameWouldAcceptTheChecksum(ContainerText written, string expectedPayload, List<string> problems)
    {
        if (!SaveChecksum.TryUnwrap(written.GetValue("save"), out string payload, out bool checksumValid))
        {
            problems.Add("The edited save came out with no checksum on it.");
            return;
        }

        if (!checksumValid)
        {
            problems.Add("The edited save came out with a checksum the game would reject.");
        }

        if (!string.Equals(payload, expectedPayload, StringComparison.Ordinal))
        {
            problems.Add("The edited save did not read back as the campaign data it was built from.");
        }
    }

    /// <summary>
    /// The payload has to keep every record it had, in order, and differ only inside the records
    /// the session was actually asked to change. A splice that ran off the end of a record would
    /// show up here as a neighbouring record that moved.
    /// </summary>
    private static void CheckOnlyTheEditedRecordsChanged(
        string originalPayload,
        string newPayload,
        IReadOnlySet<int> touchedRecords,
        List<string> problems)
    {
        List<SaveRecord> before = SavePayloadReader.SplitRecords(originalPayload).ToList();
        List<SaveRecord> after = SavePayloadReader.SplitRecords(newPayload).ToList();

        if (before.Count != after.Count)
        {
            problems.Add($"The edit changed how many records the save holds, from {before.Count} to {after.Count}.");
            return;
        }

        for (int i = 0; i < before.Count; i++)
        {
            if (!string.Equals(before[i].Header, after[i].Header, StringComparison.Ordinal))
            {
                problems.Add($"Record {i} changed from '{before[i].Header}' to '{after[i].Header}'.");
                continue;
            }

            if (touchedRecords.Contains(i))
            {
                continue;
            }

            if (!string.Equals(before[i].Body, after[i].Body, StringComparison.Ordinal))
            {
                string header = before[i].Header.Length == 0 ? "the leading record" : $"the '{before[i].Header}' record";
                problems.Add($"The edit changed {header}, which it was not meant to touch.");
            }
        }
    }

    /// <summary>
    /// A campaign that moved took some records with it and left the rest alone, and this proves the
    /// second half.
    ///
    /// Drop from the old payload exactly the records the splice said it removed, and from the new
    /// one exactly the records it said it wrote. What is left is everything the move was not about,
    /// on both sides, and those have to be the same records in the same order. A map record dropped
    /// by accident, a MISCPROG rewritten, or a record that slid past another all show up here.
    ///
    /// A record replaced where it lay is in both lists and so cancels out, which is why putting a
    /// campaign back where it came from passes with nothing left to compare but the whole payload.
    /// </summary>
    private static void CheckOnlyTheSplicedRecordsMoved(
        string originalPayload,
        string newPayload,
        RecordSetChange spliced,
        List<string> problems)
    {
        List<string> before = Without(SplitWholeRecords(originalPayload), spliced.Removed);
        List<string> after = Without(SplitWholeRecords(newPayload), spliced.Written);

        if (before.Count != after.Count)
        {
            problems.Add(
                $"The campaign move left {after.Count} other records where the save had {before.Count}.");
            return;
        }

        for (int i = 0; i < before.Count; i++)
        {
            if (string.Equals(before[i], after[i], StringComparison.Ordinal))
            {
                continue;
            }

            string header = HeaderOf(before[i]);
            problems.Add(header.Length == 0
                ? "The campaign move changed the leading record, which it was not meant to touch."
                : $"The campaign move changed the '{header}' record, which it was not meant to touch.");
        }
    }

    /// <summary>
    /// The payload split into whole records, headers and all, which is the unit a campaign moves in.
    /// </summary>
    private static List<string> SplitWholeRecords(string payload)
        => new(payload.Split(SavePayloadReader.RecordSeparator, StringSplitOptions.None));

    /// <summary>
    /// Takes out one occurrence of each listed record, in order. A record that is not there is not
    /// an error here: two splices in one session can put a record in and take the same one out
    /// again, and both halves are listed even though neither shows in the result.
    /// </summary>
    private static List<string> Without(List<string> records, IReadOnlyList<string> drop)
    {
        foreach (string record in drop)
        {
            int at = records.IndexOf(record);
            if (at >= 0)
            {
                records.RemoveAt(at);
            }
        }

        return records;
    }

    private static string HeaderOf(string record)
    {
        int split = record.IndexOf(SavePayloadReader.HeaderSeparator, StringComparison.Ordinal);
        return split < 0 ? record : record[..split];
    }

    private static SaveWritePlan Refused(
        SaveEditSession session,
        ContainerText container,
        IReadOnlyList<string> changes,
        string problem)
        => new(
            session.FilePath,
            session.FileSha256,
            Array.Empty<byte>(),
            "",
            container.OriginalLength,
            0,
            changes.ToArray(),
            new[] { problem });
}
