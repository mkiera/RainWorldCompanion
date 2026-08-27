using System.Globalization;

using RainWorldCompanion.Core.Mods;

namespace RainWorldCompanion.ViewModels;

/// <param name="WorkshopUrl">Empty for a local mod.</param>
public sealed record ModDiffRowViewModel(string Name, string DetailText, string ActionText, string WorkshopUrl)
{
    public bool HasWorkshopPage => WorkshopUrl.Length > 0;
}

/// <summary>
/// Nothing here blocks anything, and nothing here writes: this says what moved. The Mods window
/// is where a mod is turned on.
/// </summary>
public sealed class ModListDiffViewModel
{
    /// <summary>
    /// The https address rather than a steam:// one, because the page opens on any machine while
    /// the protocol link fails outright where Steam is not installed to handle it.
    /// </summary>
    public const string WorkshopUrlPrefix = "https://steamcommunity.com/sharedfiles/filedetails/?id=";

    private static readonly ModDiffRowViewModel[] NoRows = Array.Empty<ModDiffRowViewModel>();

    /// <param name="diff">Null when there was no way to look at all.</param>
    /// <param name="fromABackup">Changes only the wording for a snapshot that recorded nothing.</param>
    public ModListDiffViewModel(ModListDiff? diff, bool fromABackup = false)
    {
        if (diff is null)
        {
            HeadlineText = "";
            GroupNotes = Array.Empty<string>();
            Missing = TurnedOff = Changed = Extra = NoRows;
            MissingHeader = TurnedOffHeader = ChangedHeader = ExtraHeader = "";
            return;
        }

        ShowSection = true;

        Missing = diff.Missing
            .Select(mod => new ModDiffRowViewModel(
                NameOf(mod),
                "not installed now",
                mod.WorkshopId is { Length: > 0 }
                    ? "Get it from the Steam Workshop."
                    : "A local mod. Put it back in the game's mods folder by this name.",
                UrlFor(mod.WorkshopId)))
            .ToList();

        TurnedOff = diff.TurnedOff
            .Select(mod => new ModDiffRowViewModel(
                NameOf(mod),
                "installed, but turned off",
                "Turn it on in the game's Remix menu.",
                UrlFor(mod.WorkshopId)))
            .ToList();

        Changed = diff.Changed
            .Select(change => new ModDiffRowViewModel(
                change.Name.Length > 0 ? change.Name : change.Id,
                Describe(change),
                "",
                UrlFor(change.WorkshopId)))
            .ToList();

        Extra = diff.Extra
            .Select(mod => new ModDiffRowViewModel(
                NameOf(mod),
                "on now, and was not on when this was saved",
                "",
                UrlFor(mod.WorkshopId)))
            .ToList();

        MissingHeader = $"Not installed now ({Missing.Count})";
        TurnedOffHeader = $"Turned off ({TurnedOff.Count})";
        ChangedHeader = $"At a different version ({Changed.Count})";
        ExtraHeader = $"On now, but not recorded ({Extra.Count})";

        HeadlineText = Headline(diff, fromABackup);
        GroupNotes = Notes(diff);
    }

    /// <summary>False when nothing was compared and there is nothing worth drawing.</summary>
    public bool ShowSection { get; }

    public string HeadlineText { get; }

    public IReadOnlyList<string> GroupNotes { get; }

    public bool HasNotes => GroupNotes.Count > 0;

    public IReadOnlyList<ModDiffRowViewModel> Missing { get; }

    public IReadOnlyList<ModDiffRowViewModel> TurnedOff { get; }

    public IReadOnlyList<ModDiffRowViewModel> Changed { get; }

    public IReadOnlyList<ModDiffRowViewModel> Extra { get; }

    public string MissingHeader { get; }

    public string TurnedOffHeader { get; }

    public string ChangedHeader { get; }

    public string ExtraHeader { get; }

    public bool HasMissing => Missing.Count > 0;

    public bool HasTurnedOff => TurnedOff.Count > 0;

    public bool HasChanged => Changed.Count > 0;

    public bool HasExtra => Extra.Count > 0;

    public bool HasRows => HasMissing || HasTurnedOff || HasChanged || HasExtra;

    private static string Headline(ModListDiff diff, bool fromABackup)
    {
        if (diff.NothingWasRecorded)
        {
            return fromABackup
                ? "This backup was taken before this app recorded mod lists, so there is nothing to compare."
                : "No mod list was recorded when this save was stored, so there is nothing to compare.";
        }

        if (diff.RecordedCouldNotLook)
        {
            return "Which mods were on could not be read when this was saved, so there is nothing to compare.";
        }

        if (diff.CurrentCouldNotLook)
        {
            return "Which mods are on now could not be read, so this cannot be compared against them.";
        }

        if (!diff.HasAnyDifference())
        {
            return diff.CurrentCount == 0
                ? "No mods were on when this was saved, and none are on now."
                : Count(diff.CurrentCount) + " on now, matching what this was saved with.";
        }

        return "This was saved with a different set of mods than what is on now. It will still load, "
            + "but the game may change or drop what those mods added. Sort them out first, or load it anyway.";
    }

    private static List<string> Notes(ModListDiff diff)
    {
        var notes = new List<string>();

        if (diff.GameVersionDiffers)
        {
            notes.Add($"Saved under game {diff.RecordedGameVersion}. This machine has {diff.CurrentGameVersion}.");
        }

        notes.AddRange(diff.Notes);
        return notes;
    }

    private static string Describe(ModVersionChange change)
        => $"{change.Recorded} when this was saved, {change.Now} now";

    private static string NameOf(ModEntry mod)
        => mod.Name.Length > 0 ? mod.Name : mod.Id;

    private static string UrlFor(string? workshopId)
        => workshopId is { Length: > 0 } id && id.All(char.IsAsciiDigit)
            ? WorkshopUrlPrefix + id
            : "";

    private static string Count(int mods)
        => mods == 1 ? "1 mod" : mods.ToString(CultureInfo.InvariantCulture) + " mods";
}

internal static class ModListDiffExtensions
{
    /// <summary>Whether anything at all differs, the game version included.</summary>
    public static bool HasAnyDifference(this ModListDiff diff)
        => !diff.ListsMatch || diff.GameVersionDiffers;
}
