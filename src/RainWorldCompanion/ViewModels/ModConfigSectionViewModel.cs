using System.Globalization;

using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Mods;

namespace RainWorldCompanion.ViewModels;

public sealed record ModConfigRowSummary(string Name, string DetailText);

/// <summary>
/// Which mods' settings a save carries, for the panel rather than a dialog. Read only: the picker
/// that decides what is written is <see cref="ModConfigPickerViewModel"/>.
///
/// <para>An empty list means "no mod had settings", "the folder could not be read" or "this
/// predates settings being kept", and those three want three different sentences. Showing any of
/// them as "no settings" would be a claim about the save that nobody checked.</para>
/// </summary>
public sealed class ModConfigSectionViewModel
{
    private ModConfigSectionViewModel(
        string countText,
        IReadOnlyList<ModConfigRowSummary> rows,
        string emptyText)
    {
        CountText = countText;
        Rows = rows;
        EmptyText = emptyText;
    }

    /// <summary>"3 mods", or empty when nothing was read.</summary>
    public string CountText { get; }

    public IReadOnlyList<ModConfigRowSummary> Rows { get; }

    public bool HasRows => Rows.Count > 0;

    public bool HasCount => CountText.Length > 0;

    /// <summary>Why there are no rows, in a sentence. Empty when there are rows.</summary>
    public string EmptyText { get; }

    public bool HasEmptyText => EmptyText.Length > 0;

    /// <summary>What is in the save folder now.</summary>
    public static ModConfigSectionViewModel ForCurrent(ModConfigSet? configs)
        => Build(configs, "Mod settings in the save folder could not be read.", "");

    /// <param name="fromABackup">Only changes the wording of the nothing-recorded case.</param>
    public static ModConfigSectionViewModel ForRecorded(ModConfigSet? configs, bool fromABackup)
        => Build(
            configs,
            "The mod settings could not be read when this was saved.",
            fromABackup
                ? "This backup was taken before this app recorded mod settings."
                : "No mod settings were recorded when this save was stored.");

    /// <summary>
    /// A backup holds the settings files themselves and lists them in its manifest, so its section
    /// is derived from that list through the same rule the reader uses. A second index could only
    /// disagree with the first.
    /// </summary>
    public static ModConfigSectionViewModel ForBackup(IReadOnlyList<ManifestFileEntry>? files)
    {
        if (files is null)
        {
            return new ModConfigSectionViewModel(
                "",
                Array.Empty<ModConfigRowSummary>(),
                "This snapshot has no manifest, so it recorded no mod settings.");
        }

        var carried = new ModConfigSet { ReadTheFolder = true };

        foreach (var file in files)
        {
            var relative = file.RelativePath ?? "";
            if (ModConfigReader.Travels(relative))
            {
                carried.Files.Add(new ModConfigFile
                {
                    RelativePath = relative,
                    ModId = ModConfigReader.ModIdFor(relative),
                    SizeBytes = file.SizeBytes,
                });
            }
        }

        return Build(carried, "", "");
    }

    private static ModConfigSectionViewModel Build(ModConfigSet? configs, string unreadable, string nothingRecorded)
    {
        if (configs is null)
        {
            return new ModConfigSectionViewModel("", Array.Empty<ModConfigRowSummary>(), nothingRecorded);
        }

        if (!configs.ReadTheFolder)
        {
            return new ModConfigSectionViewModel(
                "",
                Array.Empty<ModConfigRowSummary>(),
                configs.Note ?? unreadable);
        }

        var groups = configs.ByMod();

        if (groups.Count == 0)
        {
            return new ModConfigSectionViewModel("No mod settings", Array.Empty<ModConfigRowSummary>(), "");
        }

        return new ModConfigSectionViewModel(Count(groups.Count), BuildRows(groups), "");
    }

    private static List<ModConfigRowSummary> BuildRows(IReadOnlyList<ModConfigGroup> groups)
        => groups
            .Select(group => new ModConfigRowSummary(
                group.ModId.Length > 0 ? group.ModId : "settings with no mod name",
                Detail(group)))
            .ToList();

    private static string Detail(ModConfigGroup group)
    {
        var files = group.Files.Count == 1
            ? "1 file"
            : group.Files.Count.ToString(CultureInfo.InvariantCulture) + " files";

        return files + "    " + SlotCopyService.FormatSize(group.TotalBytes);
    }

    private static string Count(int mods)
        => mods == 1 ? "1 mod" : mods.ToString(CultureInfo.InvariantCulture) + " mods";
}
