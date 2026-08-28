using System.Diagnostics.CodeAnalysis;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;

using RainWorldCompanion.Core.Mods;

namespace RainWorldCompanion.ViewModels;

/// <param name="WorkshopUrl">Empty for a local mod.</param>
public sealed record ModDiffRowViewModel(string Name, string DetailText, string ActionText, string WorkshopUrl)
{
    public bool HasWorkshopPage => WorkshopUrl.Length > 0;
}

/// <summary>
/// Nothing here writes: this says what moved, and holds the tick that says the difference is
/// understood. The Mods window is where a mod is actually turned on.
/// </summary>
public sealed partial class ModListDiffViewModel : ObservableObject
{
    /// <summary>
    /// The https address, which is what opens on a machine with no Steam. Views hand it to
    /// <see cref="Views.WorkshopLink"/>, which offers it to the Steam client first.
    /// </summary>
    public const string WorkshopUrlPrefix = "https://steamcommunity.com/sharedfiles/filedetails/?id=";

    private static readonly ModDiffRowViewModel[] NoRows = Array.Empty<ModDiffRowViewModel>();

    // Set by whoever builds the dialog. Null where there is nothing to open, which keeps the
    // button off rather than drawing it dead.
    public Func<ModListDiff?>? FixMods { get; init; }

    public bool CanFixMods => FixMods is not null && HasRows;

    public string FixModsText => (TurnedOff.Count, Extra.Count) switch
    {
        ( > 0, > 0) => $"Turn on {TurnedOff.Count}, turn off {Extra.Count}",
        ( > 0, 0) => $"Turn on {TurnedOff.Count}",
        (0, > 0) => $"Turn off {Extra.Count}",
        _ => "Open the Mods window",
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Settled))]
    private bool acknowledged;

    // True when the machine differs from the recording in a way somebody should say they know
    // about before a save is written over.
    public bool NeedsAcknowledgement { get; private set; }

    public bool Settled => !NeedsAcknowledgement || Acknowledged;

    public string AcknowledgeText => "Load it anyway. I know the mods do not match.";

    /// <param name="diff">Null when there was no way to look at all.</param>
    /// <param name="fromABackup">Changes only the wording for a snapshot that recorded nothing.</param>
    public ModListDiffViewModel(ModListDiff? diff, bool fromABackup = false)
    {
        _fromABackup = fromABackup;
        Reload(diff);
    }

    private readonly bool _fromABackup;

    // Called again after the Mods window has been through, so the dialog that opened it shows what
    // is true now rather than what was true when it opened.
    [MemberNotNull(
        nameof(HeadlineText), nameof(GroupNotes),
        nameof(Missing), nameof(TurnedOff), nameof(Changed), nameof(Extra),
        nameof(MissingHeader), nameof(TurnedOffHeader), nameof(ChangedHeader), nameof(ExtraHeader))]
    public void Reload(ModListDiff? diff)
    {
        if (diff is null)
        {
            ShowSection = false;
            NeedsAcknowledgement = false;
            HeadlineText = "";
            GroupNotes = Array.Empty<string>();
            Missing = TurnedOff = Changed = Extra = NoRows;
            MissingHeader = TurnedOffHeader = ChangedHeader = ExtraHeader = "";
            OnPropertyChanged(string.Empty);
            return;
        }

        ShowSection = true;
        NeedsAcknowledgement = diff.Compared && !diff.Matches;

        if (!NeedsAcknowledgement)
        {
            Acknowledged = false;
        }

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
                "The Mods window can turn it on.",
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
                "The Mods window can turn it off.",
                UrlFor(mod.WorkshopId)))
            .ToList();

        MissingHeader = $"Not installed now ({Missing.Count})";
        TurnedOffHeader = $"Turned off ({TurnedOff.Count})";
        ChangedHeader = $"At a different version ({Changed.Count})";
        ExtraHeader = $"On now, but not recorded ({Extra.Count})";

        HeadlineText = Headline(diff, _fromABackup);
        GroupNotes = Notes(diff);
        OnPropertyChanged(string.Empty);
    }

    /// <summary>False when nothing was compared and there is nothing worth drawing.</summary>
    public bool ShowSection { get; private set; }

    public string HeadlineText { get; private set; }

    public IReadOnlyList<string> GroupNotes { get; private set; }

    public bool HasNotes => GroupNotes.Count > 0;

    public IReadOnlyList<ModDiffRowViewModel> Missing { get; private set; }

    public IReadOnlyList<ModDiffRowViewModel> TurnedOff { get; private set; }

    public IReadOnlyList<ModDiffRowViewModel> Changed { get; private set; }

    public IReadOnlyList<ModDiffRowViewModel> Extra { get; private set; }

    public string MissingHeader { get; private set; }

    public string TurnedOffHeader { get; private set; }

    public string ChangedHeader { get; private set; }

    public string ExtraHeader { get; private set; }

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
