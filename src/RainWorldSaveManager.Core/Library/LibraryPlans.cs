// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Globalization;
using RainWorldSaveManager.Core.Backups;

namespace RainWorldSaveManager.Core.Library;

/// <summary>
/// What loading one entry onto one slot would do, worked out without changing anything.
///
/// Kept separate from <see cref="SlotCopyPlan"/> rather than reusing it. That plan's source is a
/// live slot and every line it builds says so, and a dialog that described a library entry as
/// "local slot 1" would be describing something that is not there.
/// </summary>
/// <param name="Summary">
/// One line saying what this would do to the slot, for a load that does not replace it wholesale.
/// Empty for a whole slot, where the answer is always the same sentence and the dialog says it.
/// </param>
public sealed record LibraryLoadPlan(
    LibraryEntry Entry,
    SlotSide Target,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Warnings,
    string Summary = "")
{
    public bool CanLoad => Problems.Count == 0;
}

/// <summary>
/// The outcome of a load, in the same shape as <see cref="SlotCopyResult"/> so a caller reports
/// both the same way. <see cref="LiveFolderModified"/> is again the field to read before wording a
/// failure: it is false only when the slot file is provably the bytes it was before.
/// </summary>
public sealed record LibraryLoadResult(
    bool Success,
    BackupSnapshot? SafetySnapshot,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    bool LiveFolderModified,
    long BytesCopied,
    LibraryLoadPlan? Plan)
{
    /// <summary>
    /// The line to lead a report with. The wording lives here rather than in the UI so "nothing was
    /// changed" can never be printed over a save file that was in fact written to.
    /// </summary>
    public string Headline()
    {
        var target = Plan?.Target.FileName ?? "the target slot";

        if (Success)
        {
            var name = Plan?.Entry.Name ?? "the library save";
            return string.Format(
                CultureInfo.InvariantCulture,
                "Loaded \"{0}\" into {1} ({2}).",
                name,
                target,
                SlotCopyService.FormatSize(BytesCopied));
        }

        if (!LiveFolderModified)
        {
            return "The save was not loaded, so nothing in the save folder was changed.";
        }

        var safety = SafetySnapshot?.Id;
        return safety is null
            ? $"The load did not finish and {target} may be part written."
            : $"The load did not finish and {target} may be part written. Backup {safety} still holds the saves as they were, and restoring it puts them back.";
    }
}

/// <summary>
/// What an import produced. A refused import has a null entry and at least one error; an import
/// that went through with reservations has an entry and warnings, which is how a save with a
/// checksum the game will reject still gets into the library.
/// </summary>
public sealed record LibraryImportResult(
    LibraryEntry? Entry,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors)
{
    public bool Success => Entry is not null && Errors.Count == 0;
}
