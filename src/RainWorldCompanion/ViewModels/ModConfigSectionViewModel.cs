using System.ComponentModel;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;

using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Mods;

namespace RainWorldCompanion.ViewModels;

public sealed partial class ModConfigRowSummary : ObservableObject
{
    public ModConfigRowSummary(string name, string detailText, ModConfigMatch match, string modId = "", bool canSelect = false)
    {
        Name = name;
        DetailText = detailText;
        Match = match;
        ModId = modId;
        CanSelect = canSelect;
    }

    public string Name { get; }

    public string DetailText { get; }

    public ModConfigMatch Match { get; }

    public string ModId { get; }

    public bool CanSelect { get; }

    [ObservableProperty]
    private bool isSelected;

    /// <summary>Empty when there was nothing to compare against, which says nothing rather than
    /// claiming a difference.</summary>
    public string MatchText => ModConfigMatching.Describe(Match);

    public bool HasMatchText => MatchText.Length > 0;
}

/// <summary>
/// Which mods' settings a save carries, for the panel rather than a dialog. Read only: the picker
/// that decides what is written is <see cref="ModConfigPickerViewModel"/>.
///
/// <para>An empty list means "no mod had settings", "the folder could not be read" or "this
/// predates settings being kept", and those three want three different sentences. Showing any of
/// them as "no settings" would be a claim about the save that nobody checked.</para>
/// </summary>
public sealed class ModConfigSectionViewModel : ObservableObject
{
    private ModConfigSectionViewModel(
        string countText,
        IReadOnlyList<ModConfigRowSummary> rows,
        string emptyText,
        bool canManage)
    {
        CountText = countText;
        Rows = rows;
        EmptyText = emptyText;
        CanManage = canManage;

        foreach (ModConfigRowSummary row in rows)
        {
            row.PropertyChanged += OnRowChanged;
        }
    }

    /// <summary>"3 mods", or empty when nothing was read.</summary>
    public string CountText { get; }

    public IReadOnlyList<ModConfigRowSummary> Rows { get; }

    public bool HasRows => Rows.Count > 0;

    public bool HasCount => CountText.Length > 0;

    /// <summary>Why there are no rows, in a sentence. Empty when there are rows.</summary>
    public string EmptyText { get; }

    public bool HasEmptyText => EmptyText.Length > 0;

    // Only the live folder's own settings can be exported, imported over, or deleted.
    public bool CanManage { get; }

    public bool CanSelectAny => Rows.Any(row => row.CanSelect);

    public bool HasSelection => Rows.Any(row => row.IsSelected);

    public int SelectedCount => Rows.Count(row => row.IsSelected);

    public IReadOnlyList<string> SelectedModIds =>
        Rows.Where(row => row.IsSelected).Select(row => row.ModId).ToList();

    public string SelectionText => SelectedCount switch
    {
        0 => "Tick the mods to export or delete.",
        1 => "1 mod ticked",
        _ => SelectedCount.ToString(CultureInfo.InvariantCulture) + " mods ticked",
    };

    public void SelectAll(bool selected)
    {
        foreach (ModConfigRowSummary row in Rows)
        {
            if (row.CanSelect)
            {
                row.IsSelected = selected;
            }
        }
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ModConfigRowSummary.IsSelected))
        {
            return;
        }

        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedModIds));
        OnPropertyChanged(nameof(SelectionText));
    }

    /// <summary>What is in the save folder now.</summary>
    public static ModConfigSectionViewModel ForCurrent(ModConfigSet? configs, bool manageable = false)
        => Build(configs, "Mod settings in the save folder could not be read.", "", null, manageable);

    /// <param name="fromABackup">Only changes the wording of the nothing-recorded case.</param>
    /// <param name="live">
    /// What is in the save folder now, so a row can say whether it differs from it. Null leaves
    /// every row unlabelled, which is what the panel showing the live folder itself wants: nothing
    /// there is being compared to anything.
    /// </param>
    public static ModConfigSectionViewModel ForRecorded(
        ModConfigSet? configs, bool fromABackup, ModConfigSet? live = null)
        => Build(
            configs,
            "The mod settings could not be read when this was saved.",
            fromABackup
                ? "This backup was taken before this app recorded mod settings."
                : "No mod settings were recorded when this save was stored.",
            live,
            manageable: false);

    /// <summary>
    /// A backup holds the settings files themselves and lists them in its manifest, so its section
    /// is derived from that list through the same rule the reader uses. A second index could only
    /// disagree with the first.
    /// </summary>
    public static ModConfigSectionViewModel ForBackup(
        IReadOnlyList<ManifestFileEntry>? files, ModConfigSet? live = null)
    {
        if (files is null)
        {
            return new ModConfigSectionViewModel(
                "",
                Array.Empty<ModConfigRowSummary>(),
                "This snapshot has no manifest, so it recorded no mod settings.",
                canManage: false);
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
                    Sha256 = file.Sha256 ?? "",
                });
            }
        }

        return Build(carried, "", "", live, manageable: false);
    }

    private static ModConfigSectionViewModel Build(
        ModConfigSet? configs, string unreadable, string nothingRecorded, ModConfigSet? live, bool manageable)
    {
        if (configs is null)
        {
            return new ModConfigSectionViewModel("", Array.Empty<ModConfigRowSummary>(), nothingRecorded, manageable);
        }

        if (!configs.ReadTheFolder)
        {
            return new ModConfigSectionViewModel(
                "",
                Array.Empty<ModConfigRowSummary>(),
                configs.Note ?? unreadable,
                manageable);
        }

        var groups = configs.ByMod();

        if (groups.Count == 0)
        {
            return new ModConfigSectionViewModel("No mod settings", Array.Empty<ModConfigRowSummary>(), "", manageable);
        }

        return new ModConfigSectionViewModel(Count(groups.Count), BuildRows(groups, live, manageable), "", manageable);
    }

    private static List<ModConfigRowSummary> BuildRows(
        IReadOnlyList<ModConfigGroup> groups, ModConfigSet? live, bool manageable)
        => groups
            .Select(group => new ModConfigRowSummary(
                group.ModId.Length > 0 ? group.ModId : "settings with no mod name",
                Detail(group),
                ModConfigMatching.For(group, live),
                group.ModId,
                canSelect: manageable && group.ModId.Length > 0))
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
