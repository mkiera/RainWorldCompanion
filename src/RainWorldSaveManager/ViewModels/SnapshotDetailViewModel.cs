// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists in the referenced assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Globalization;
using RainWorldSaveManager.Core.Saves;
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
///
/// Rain Meadow's online saves are kept out of the slot sections and put in their own foldout,
/// paired with the local save of the same slot number. That foldout and the meadow.json section
/// are both left out entirely when there is nothing of Rain Meadow's to show, so a player who
/// does not use the mod sees the panel exactly as it was before.
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
        IReadOnlyList<SlotMetadata> allSlots,
        MeadowProfile? meadow,
        SlotCopyGate? copyGate,
        ISlugcatIconProvider icons)
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

        var local = BuildSlots(allSlots.Where(slot => slot.Realm != SaveRealm.Online), icons);
        var online = BuildSlots(allSlots.Where(slot => slot.Realm == SaveRealm.Online), icons);

        Slots = local;
        SlotPairs = BuildPairs(local, online, copyGate);
        OnlineCountText = FormatFileCount(online.Count, "online save");

        Meadow = meadow is null ? null : new MeadowProfileViewModel(meadow);
        CampaignCountText = CampaignCount.Describe(CountCampaigns(local), CountCampaigns(online));

        OpenFirstCampaign(local.Count > 0 ? local : online);
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

    /// <summary>The local save files, one section each. Online files are in <see cref="SlotPairs"/>.</summary>
    public IReadOnlyList<SlotViewModel> Slots { get; }

    public bool HasSlots => Slots.Count > 0;

    public bool HasNoSlots => Slots.Count == 0;

    /// <summary>
    /// One row per slot number, local and online together. Empty when there is no online save
    /// anywhere in this snapshot, which is what hides the whole foldout.
    /// </summary>
    public IReadOnlyList<SlotPairViewModel> SlotPairs { get; }

    public bool HasOnlineSlots => SlotPairs.Count > 0;

    /// <summary>
    /// Whether the Rain Meadow foldout is open. It lives here rather than on the window so that
    /// each newly built detail starts collapsed however the last one was left, which is what
    /// "collapsed by default" has to mean for a panel the user selects into repeatedly. The view
    /// writes it back, and nothing else reads it, so it needs no change notification.
    /// </summary>
    public bool IsOnlineFoldoutExpanded { get; set; }

    /// <summary>"2 online saves", for the foldout header.</summary>
    public string OnlineCountText { get; }

    /// <summary>meadow.json, or null when the folder holds no such file.</summary>
    public MeadowProfileViewModel? Meadow { get; }

    public bool HasMeadow => Meadow is not null;

    /// <summary>The line shown in place of the slot sections when there are none.</summary>
    public string EmptyText { get; }

    /// <summary>The save folder as it stands on disk.</summary>
    public static SnapshotDetailViewModel ForLive(
        IReadOnlyList<SlotMetadata> slots,
        string savePath,
        long sizeBytes,
        int fileCount,
        MeadowProfile? meadow,
        SlotCopyGate? copyGate,
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
            allSlots: slots,
            meadow: meadow,
            copyGate: copyGate,
            icons: icons);
    }

    /// <summary>
    /// One backup, read out of the manifest that was written with it. No copy gate: a file inside
    /// a snapshot is put back by restoring the snapshot, not by writing it over a live slot.
    /// </summary>
    public static SnapshotDetailViewModel ForBackup(
        BackupItemViewModel item,
        MeadowProfile? meadow,
        ISlugcatIconProvider icons)
    {
        var source = item.Snapshot.Manifest?.Slots;

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
            allSlots: (IReadOnlyList<SlotMetadata>?)source ?? Array.Empty<SlotMetadata>(),
            meadow: meadow,
            copyGate: null,
            icons: icons);
    }

    /// <summary>
    /// Re-asks every copy button whether it is allowed to run. Called by the window when the game
    /// starts or stops, or when an operation begins or ends.
    /// </summary>
    public void RaiseCopyStates()
    {
        foreach (var pair in SlotPairs)
        {
            pair.RaiseCopyStates();
        }
    }

    private static IReadOnlyList<SlotViewModel> BuildSlots(
        IEnumerable<SlotMetadata> slots,
        ISlugcatIconProvider icons)
    {
        return slots
            .OrderBy(slot => slot.Slot == 0 ? int.MaxValue : slot.Slot)
            .ThenBy(slot => slot.FileName, StringComparer.OrdinalIgnoreCase)
            .Select(slot => new SlotViewModel(slot, icons))
            .ToList();
    }

    /// <summary>
    /// One row per slot number that has a file on either side, or nothing at all when the folder
    /// holds no online save. A row whose online side is missing is still worth drawing when some
    /// other slot has one, because copying a local save into an empty online slot is the case that
    /// row exists for.
    /// </summary>
    private static IReadOnlyList<SlotPairViewModel> BuildPairs(
        IReadOnlyList<SlotViewModel> local,
        IReadOnlyList<SlotViewModel> online,
        SlotCopyGate? copyGate)
    {
        if (online.Count == 0)
        {
            return Array.Empty<SlotPairViewModel>();
        }

        var pairs = new List<SlotPairViewModel>();

        for (var slot = 1; slot <= 3; slot++)
        {
            var localSlot = FindSlot(local, slot);
            var onlineSlot = FindSlot(online, slot);

            if (localSlot is null && onlineSlot is null)
            {
                continue;
            }

            pairs.Add(new SlotPairViewModel(slot, localSlot, onlineSlot, copyGate));
        }

        return pairs;
    }

    private static SlotViewModel? FindSlot(IReadOnlyList<SlotViewModel> slots, int number)
    {
        foreach (var slot in slots)
        {
            if (slot.SlotNumber == number)
            {
                return slot;
            }
        }

        return null;
    }

    /// <summary>
    /// Opens the first campaign, so the panel reads as a list of slots with one worked example
    /// already open. Everything else starts closed.
    /// </summary>
    private static void OpenFirstCampaign(IReadOnlyList<SlotViewModel> slots)
    {
        foreach (var slot in slots)
        {
            if (slot.Campaigns.Count > 0)
            {
                slot.Campaigns[0].IsExpanded = true;
                return;
            }
        }
    }

    private static int CountCampaigns(IEnumerable<SlotViewModel> slots)
    {
        var campaigns = 0;
        foreach (var slot in slots)
        {
            campaigns += slot.Campaigns.Count;
        }

        return campaigns;
    }

    private static string FormatFileCount(int count, string noun) =>
        count == 1
            ? "1 " + noun
            : count.ToString(CultureInfo.InvariantCulture) + " " + noun + "s";
}
