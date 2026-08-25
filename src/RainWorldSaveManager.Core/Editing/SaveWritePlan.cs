// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using RainWorldSaveManager.Core.Backups;
using RainWorldSaveManager.Core.Saves;

namespace RainWorldSaveManager.Core.Editing;

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
        SizePolicy policy)
    {
        var problems = new List<string>();

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
        CheckOnlyTheEditedRecordsChanged(originalPayload, newPayload, touchedRecords, problems);

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
