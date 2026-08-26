using System.Globalization;

using RainWorldCompanion.Core.Mods;

namespace RainWorldCompanion.ViewModels;

public sealed record ModRowViewModel(string Name, string VersionText, string OriginText);

/// <summary>
/// An empty list means "no mods were on", "the game folder is not set", "this predates mod lists"
/// or "the options file could not be read", and those four want four different sentences. Showing
/// any of them as "no mods" would be a claim about the user's install that nobody checked.
/// </summary>
public sealed class ModListSectionViewModel
{
    private ModListSectionViewModel(
        string countText,
        string gameVersionText,
        IReadOnlyList<ModRowViewModel> rows,
        string emptyText)
    {
        CountText = countText;
        GameVersionText = gameVersionText;
        Rows = rows;
        EmptyText = emptyText;
    }

    /// <summary>"14 mods on", or empty when the list was never read.</summary>
    public string CountText { get; }

    public string GameVersionText { get; }

    public IReadOnlyList<ModRowViewModel> Rows { get; }

    public bool HasRows => Rows.Count > 0;

    public bool HasGameVersion => GameVersionText.Length > 0;

    public bool HasCount => CountText.Length > 0;

    /// <summary>Why there are no rows, in a sentence. Empty when there are rows.</summary>
    public string EmptyText { get; }

    public bool HasEmptyText => EmptyText.Length > 0;

    public static ModListSectionViewModel ForCurrent(CurrentMods? mods)
    {
        if (mods is null)
        {
            return new ModListSectionViewModel("", "", Array.Empty<ModRowViewModel>(), "");
        }

        ModListSnapshot enabled = mods.Enabled;
        string version = VersionText(enabled.GameVersion);

        if (!enabled.ReadTheEnabledList)
        {
            return new ModListSectionViewModel(
                "",
                version,
                Array.Empty<ModRowViewModel>(),
                enabled.Note ?? "Which mods are on could not be read.");
        }

        if (enabled.Mods.Count == 0)
        {
            return new ModListSectionViewModel("No mods on", version, Array.Empty<ModRowViewModel>(), "");
        }

        return new ModListSectionViewModel(
            Count(enabled.Mods.Count) + " on",
            version,
            BuildRows(enabled),
            "");
    }

    /// <summary>
    /// Always returns a section, because a snapshot saying it recorded nothing is itself a line.
    /// </summary>
    /// <param name="fromABackup">Only changes the wording of the nothing-recorded case.</param>
    public static ModListSectionViewModel ForRecorded(ModListSnapshot? mods, bool fromABackup)
    {
        if (mods is null)
        {
            return new ModListSectionViewModel(
                "",
                "",
                Array.Empty<ModRowViewModel>(),
                fromABackup
                    ? "This backup was taken before this app recorded mod lists."
                    : "No mod list was recorded when this save was stored.");
        }

        string version = VersionText(mods.GameVersion);

        if (!mods.ReadTheEnabledList)
        {
            return new ModListSectionViewModel(
                "",
                version,
                Array.Empty<ModRowViewModel>(),
                mods.Note ?? "Which mods were on could not be read when this was saved.");
        }

        if (mods.Mods.Count == 0)
        {
            return new ModListSectionViewModel("No mods were on", version, Array.Empty<ModRowViewModel>(), "");
        }

        return new ModListSectionViewModel(
            Count(mods.Mods.Count) + " on",
            version,
            BuildRows(mods),
            "");
    }

    private static List<ModRowViewModel> BuildRows(ModListSnapshot mods)
        => mods.Mods
            .Select(mod => new ModRowViewModel(
                mod.Name.Length > 0 ? mod.Name : mod.Id,
                mod.Version ?? "",
                Origin(mod, mods)))
            .ToList();

    private static string Origin(ModEntry mod, ModListSnapshot mods)
    {
        if (mod.WorkshopId is { Length: > 0 } id)
        {
            return "workshop " + id;
        }

        if (mod.Origin == ModEntry.InstallOrigin)
        {
            return "local mod";
        }

        // Only worth saying when the install was actually looked at.
        return mods.CheckedTheInstall ? "not installed" : "";
    }

    private static string VersionText(string? gameVersion)
        => gameVersion is { Length: > 0 } version ? "game " + version : "";

    private static string Count(int mods)
        => mods == 1 ? "1 mod" : mods.ToString(CultureInfo.InvariantCulture) + " mods";
}
