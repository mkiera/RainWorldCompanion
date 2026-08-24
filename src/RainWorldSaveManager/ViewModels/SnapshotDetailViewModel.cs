// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists in the referenced assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Globalization;
using RainWorldSaveManager.Core.Saves.Models;
using RainWorldSaveManager.Services;

namespace RainWorldSaveManager.ViewModels;

/// <summary>
/// What the detail panel shows: either the save folder as it stands right now, or one backup,
/// laid out the same way so the two can be read against each other.
///
/// A backup is filled from the manifest that was written with it, so selecting one costs no disk
/// read. A manifest written by schema version 1 recorded far less per campaign, and those cards
/// render with dashes where the value was never stored rather than failing to render at all.
/// </summary>
public sealed class SnapshotDetailViewModel
{
    private SnapshotDetailViewModel(
        bool isLive,
        string title,
        string subtitle,
        string kindText,
        string sizeText,
        string fileCountText,
        string noteText,
        string emptyText,
        BackupItemViewModel? backup,
        IReadOnlyList<SlotViewModel> slots)
    {
        IsLive = isLive;
        Title = title;
        Subtitle = subtitle;
        KindText = kindText;
        SizeText = sizeText;
        FileCountText = fileCountText;
        NoteText = noteText;
        EmptyText = emptyText;
        Backup = backup;
        Slots = slots;
        CampaignCountText = BuildCampaignCount(slots);
    }

    /// <summary>True for the save folder on disk, false for a backup.</summary>
    public bool IsLive { get; }

    public string Title { get; }

    /// <summary>The save folder path, or the backup's folder name.</summary>
    public string Subtitle { get; }

    public string KindText { get; }

    public string SizeText { get; }

    public string FileCountText { get; }

    public string CampaignCountText { get; }

    public string NoteText { get; }

    public bool HasNote => NoteText.Length > 0;

    /// <summary>
    /// The list row this panel was built from, or null for the live save. The header binds the
    /// verify state through this so a Verify run updates the panel without rebuilding it.
    /// </summary>
    public BackupItemViewModel? Backup { get; }

    public bool HasBackup => Backup is not null;

    public IReadOnlyList<SlotViewModel> Slots { get; }

    public bool HasSlots => Slots.Count > 0;

    public bool HasNoSlots => Slots.Count == 0;

    /// <summary>The line shown in place of the slot sections when there are none.</summary>
    public string EmptyText { get; }

    /// <summary>The save folder as it stands on disk.</summary>
    public static SnapshotDetailViewModel ForLive(
        IReadOnlyList<SlotMetadata> slots,
        string savePath,
        long sizeBytes,
        int fileCount,
        ISlugcatIconProvider icons)
    {
        return new SnapshotDetailViewModel(
            isLive: true,
            title: "Live save",
            subtitle: savePath,
            kindText: "On disk now",
            sizeText: BackupItemViewModel.FormatSize(sizeBytes),
            fileCountText: FormatFileCount(fileCount, "save file"),
            noteText: "",
            emptyText: "No save files were found in the save folder.",
            backup: null,
            slots: BuildSlots(slots, icons));
    }

    /// <summary>One backup, read out of the manifest that was written with it.</summary>
    public static SnapshotDetailViewModel ForBackup(BackupItemViewModel item, ISlugcatIconProvider icons)
    {
        var source = item.Snapshot.Manifest?.Slots;
        var slots = source is null
            ? Array.Empty<SlotViewModel>()
            : BuildSlots(source, icons);

        var empty = item.Snapshot.Manifest is null
            ? "This snapshot has no manifest, so it recorded no campaign detail."
            : "This snapshot's manifest recorded no save files.";

        return new SnapshotDetailViewModel(
            isLive: false,
            title: item.LabelText,
            subtitle: item.CreatedText + "    " + item.Snapshot.Id,
            kindText: item.KindText,
            sizeText: item.SizeText,
            fileCountText: item.FileCountText,
            noteText: item.NoteText,
            emptyText: empty,
            backup: item,
            slots: slots);
    }

    /// <summary>
    /// Builds the slot sections and opens the first campaign. Everything else starts closed, so
    /// the panel reads as a list of slots with one worked example already open.
    /// </summary>
    private static IReadOnlyList<SlotViewModel> BuildSlots(
        IReadOnlyList<SlotMetadata> slots,
        ISlugcatIconProvider icons)
    {
        var built = slots
            .OrderBy(slot => slot.Slot == 0 ? int.MaxValue : slot.Slot)
            .ThenBy(slot => slot.FileName, StringComparer.OrdinalIgnoreCase)
            .Select(slot => new SlotViewModel(slot, icons))
            .ToList();

        foreach (var slot in built)
        {
            if (slot.Campaigns.Count > 0)
            {
                slot.Campaigns[0].IsExpanded = true;
                break;
            }
        }

        return built;
    }

    private static string BuildCampaignCount(IReadOnlyList<SlotViewModel> slots)
    {
        var campaigns = 0;
        foreach (var slot in slots)
        {
            campaigns += slot.Campaigns.Count;
        }

        return campaigns switch
        {
            0 => "no campaigns",
            1 => "1 campaign",
            _ => campaigns.ToString(CultureInfo.InvariantCulture) + " campaigns",
        };
    }

    private static string FormatFileCount(int count, string noun) =>
        count == 1
            ? "1 " + noun
            : count.ToString(CultureInfo.InvariantCulture) + " " + noun + "s";
}
