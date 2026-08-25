// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists in the referenced assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Globalization;
using RainWorldSaveManager.Core.Saves;
using RainWorldSaveManager.Core.Saves.Models;
using RainWorldSaveManager.Services;

namespace RainWorldSaveManager.ViewModels;

/// <summary>
/// What the detail panel shows: the save folder as it stands right now, one backup, or one library
/// save, laid out the same way so any two of them can be read against each other.
///
/// A backup is filled from the manifest that was written with it, so selecting one costs no disk
/// read. A manifest written by schema version 1 recorded far less per campaign, and those cards
/// render with dashes where the value was never stored rather than failing to render at all.
///
/// For the live folder and for a backup, Rain Meadow's online saves are kept out of the slot
/// sections and put in their own banded section, paired with the local save of the same slot
/// number. That section is left out when the mod is not on the machine, and left out for a library
/// save, which is one file with no second half to pair it against.
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
        LibraryEntryViewModel? entry,
        IReadOnlyList<SlotMetadata> allSlots,
        MeadowProfile? meadow,
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
        Entry = entry;

        // A library save is one file and it reads the same whichever slot it came from. Splitting it
        // by realm the way a folder is split would drop an online sourced save out of Slots and
        // leave the panel with nothing in it, and there is no second half here to pair it against.
        var local = entry is not null
            ? BuildSlots(allSlots, icons, entry.Entry.Manifest?.SourceFileName)
            : BuildSlots(allSlots.Where(slot => slot.Realm != SaveRealm.Online), icons);
        var online = entry is not null
            ? Array.Empty<SlotViewModel>()
            : BuildSlots(allSlots.Where(slot => slot.Realm == SaveRealm.Online), icons);

        Slots = local;
        SlotPairs = entry is not null ? Array.Empty<SlotPairViewModel>() : BuildPairs(local, online);
        OnlineCountText = FormatFileCount(online.Count, "online save");

        Meadow = meadow is null ? null : new MeadowProfileViewModel(meadow);
        CampaignCountText = CampaignCount.Describe(CountCampaigns(local), CountCampaigns(online));
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

    /// <summary>
    /// The library row this panel was built from, or null for anything else. The header binds the
    /// checked state through this the same way it binds a backup's.
    /// </summary>
    public LibraryEntryViewModel? Entry { get; }

    public bool HasEntry => Entry is not null;

    /// <summary>
    /// One section each. For the live folder and for a backup these are the local save files, and
    /// the online ones are in <see cref="SlotPairs"/>. A library save puts its one file here
    /// whichever realm it came from.
    /// </summary>
    public IReadOnlyList<SlotViewModel> Slots { get; }

    public bool HasSlots => Slots.Count > 0;

    public bool HasNoSlots => Slots.Count == 0;

    /// <summary>
    /// One row per slot number, local and online together. A slot with no online file still gets a
    /// row, because copying a local save into an empty online slot is what that row is for. Empty
    /// for a library save, which is one file.
    /// </summary>
    public IReadOnlyList<SlotPairViewModel> SlotPairs { get; }

    public bool HasOnlineSlots => SlotPairs.Any(pair => pair.Online.Exists);

    public bool HasNoOnlineSlots => SlotPairs.Count > 0 && !HasOnlineSlots;

    /// <summary>Whether Rain Meadow is on this machine.</summary>
    /// Set by the window after the detail is built, because only the window knows what is on the
    /// machine. The whole detail is replaced on every rebuild, so this needs no change notification.
    public bool MeadowInstalled { get; set; }

    /// <summary>
    /// Whether the whole Rain Meadow block is drawn.
    ///
    /// It follows the mod being on the machine, not the folder happening to hold an online save, so
    /// a player who has the mod but has not played online still gets the rows to copy a save across.
    /// A player without the mod sees nothing.
    ///
    /// A library save is left out whether or not the mod is here. It is one file, and the block
    /// exists to pair a slot's two halves against each other.
    /// </summary>
    public bool ShowMeadowSection => MeadowInstalled && !HasEntry;

    /// <summary>"v0.1.15.1", or empty when the version was not read.</summary>
    public string MeadowVersionText { get; set; } = "";

    /// <summary>"2 online saves", for the section band.</summary>
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
            entry: null,
            allSlots: slots,
            meadow: meadow,
            icons: icons);
    }

    /// <summary>
    /// One library save. Filled from the manifest written beside it, so selecting a row costs no
    /// disk read, and drawn as a single slot section because that is what an entry holds.
    /// </summary>
    public static SnapshotDetailViewModel ForLibraryEntry(LibraryEntryViewModel item, ISlugcatIconProvider icons)
    {
        var metadata = item.Entry.Manifest?.Metadata;

        var empty = item.Entry.Manifest is null
            ? "This save did not finish being stored, so it recorded no campaign detail."
            : "This save could not be read, so there is no campaign detail to show.";

        var subtitle = item.SourceText.Length > 0
            ? item.CreatedText + "    " + item.SourceText + "    " + item.Entry.Id
            : item.CreatedText + "    " + item.Entry.Id;

        return new SnapshotDetailViewModel(
            isLive: false,
            title: item.Name,
            subtitle: subtitle,
            kindText: "Library save",
            sizeText: item.SizeText,
            fileCountText: "1 save file",
            noteText: item.NoteText,
            emptyText: empty,
            backup: null,
            entry: item,
            allSlots: metadata is null ? Array.Empty<SlotMetadata>() : new[] { metadata },
            meadow: null,
            icons: icons);
    }

    /// <summary>One backup, read out of the manifest that was written with it.</summary>
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
            entry: null,
            allSlots: (IReadOnlyList<SlotMetadata>?)source ?? Array.Empty<SlotMetadata>(),
            meadow: meadow,
            icons: icons);
    }

    /// <param name="fileNameOverride">
    /// The container name to show instead of the one the metadata carries. Only a library save
    /// passes this, because it was parsed out of the copy kept under the library's storage name.
    /// </param>
    private static IReadOnlyList<SlotViewModel> BuildSlots(
        IEnumerable<SlotMetadata> slots,
        ISlugcatIconProvider icons,
        string? fileNameOverride = null)
    {
        return slots
            .OrderBy(slot => slot.Slot == 0 ? int.MaxValue : slot.Slot)
            .ThenBy(slot => slot.FileName, StringComparer.OrdinalIgnoreCase)
            .Select(slot => new SlotViewModel(slot, icons, fileNameOverride))
            .ToList();
    }

    /// <summary>
    /// One row per slot number, all three of them, whenever the Rain Meadow block is drawn at all.
    /// A row with nothing on either side still earns its place: it shows the player which online
    /// slots are still empty, which is what they need to know before copying a local save across.
    /// </summary>
    private static IReadOnlyList<SlotPairViewModel> BuildPairs(
        IReadOnlyList<SlotViewModel> local,
        IReadOnlyList<SlotViewModel> online)
    {
        var pairs = new List<SlotPairViewModel>(SaveSlotRef.MaxSlot);

        for (int slot = SaveSlotRef.MinSlot; slot <= SaveSlotRef.MaxSlot; slot++)
        {
            pairs.Add(new SlotPairViewModel(slot, FindSlot(local, slot), FindSlot(online, slot)));
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
