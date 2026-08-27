// RainWorldCompanion.Core.System exists in this assembly, so a using written inside the namespace
// body would bind "System" to that namespace instead of the BCL root.
using System.Globalization;
using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Mods;

namespace RainWorldCompanion.Core.Library;

/// <param name="Summary">One line for a load that does not replace the slot wholesale. Empty for a
/// whole slot, where the answer is always the same sentence and the dialog says it.</param>
public sealed record LibraryLoadPlan(
    LibraryEntry Entry,
    SlotSide Target,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Warnings,
    string Summary = "")
{
    public bool CanLoad => Problems.Count == 0;

    /// <summary>How the mods recorded with this entry differ from the machine as it stands, or null
    /// when there was no way to look. Never lands in <see cref="Problems"/> and never stops a load.</summary>
    public ModListDiff? Mods { get; init; }

    /// <summary>The mod settings this entry can bring across, or null when it carries none. Nothing
    /// here is written unless it is asked for by mod id.</summary>
    public ModConfigOffer? Settings { get; init; }
}

/// <summary>
/// The mod settings a load can bring across, with what is needed to describe each one: the mods the
/// save was played with, what is in the save folder now, and what is installed here. A picker joins
/// them by <see cref="ModConfigFile.ModId"/>.
/// </summary>
/// <param name="Live">Null when the save folder could not be read, which is not the same answer as
/// a folder holding no settings.</param>
/// <param name="Current">Null when there is no way to look at what is installed.</param>
public sealed record ModConfigOffer(
    ModConfigSet Recorded,
    ModListSnapshot? RecordedMods,
    ModConfigSet? Live,
    CurrentMods? Current)
{
    /// <summary>What a player picks by: a mod, not a file. Devourment owns both its settings file
    /// and its whole preset folder, and ticking Devourment means both.</summary>
    public IReadOnlyList<ModConfigGroup> ByMod() => Recorded.ByMod();
}

/// <summary><see cref="LiveFolderModified"/> is false only when the slot file is provably the same
/// bytes it was before.</summary>
public sealed record LibraryLoadResult(
    bool Success,
    BackupSnapshot? SafetySnapshot,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    bool LiveFolderModified,
    long BytesCopied,
    LibraryLoadPlan? Plan)
{
    /// <summary>How many mod settings files landed beside the save. Outside <see cref="Success"/>,
    /// which is about the save.</summary>
    public int SettingsWritten { get; init; }

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

/// <summary>A refused import has a null entry and at least one error. One that went through with
/// reservations has an entry and warnings, which is how a save with a bad checksum still gets in.</summary>
public sealed record LibraryImportResult(
    LibraryEntry? Entry,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors)
{
    public bool Success => Entry is not null && Errors.Count == 0;
}
