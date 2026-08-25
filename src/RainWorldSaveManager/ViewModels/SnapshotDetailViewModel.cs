// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists in the referenced assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
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
/// Local saves and Rain Meadow online saves are both built into full slot sections, and
/// <see cref="ShowOnline"/> settles which set is on screen. The toggle that changes it is drawn
/// only when the mod is on the machine, so a player without it sees the local sections and nothing
/// else, exactly as before. The paired rows further down show both realms beside each other
/// whichever way the toggle is set.
/// </summary>
public sealed partial class SnapshotDetailViewModel : ObservableObject
{
    private readonly IReadOnlyList<SlotViewModel> _localSlots;
    private readonly IReadOnlyList<SlotViewModel> _onlineSlots;
    private readonly string _localEmptyText;
    private readonly string _onlineEmptyText;

    private SnapshotDetailViewModel(
        bool isLive,
        string title,
        string subtitle,
        string kindText,
        string sizeText,
        string fileCountText,
        string noteText,
        string localEmptyText,
        string onlineEmptyText,
        BackupItemViewModel? backup,
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
        Backup = backup;

        _localEmptyText = localEmptyText;
        _onlineEmptyText = onlineEmptyText;

        _localSlots = BuildSlots(allSlots.Where(slot => slot.Realm != SaveRealm.Online), icons);
        _onlineSlots = BuildSlots(allSlots.Where(slot => slot.Realm == SaveRealm.Online), icons);

        SlotPairs = BuildPairs(_localSlots, _onlineSlots);
        OnlineCountText = FormatFileCount(_onlineSlots.Count, "online save");

        Meadow = meadow is null ? null : new MeadowProfileViewModel(meadow);
        CampaignCountText = CampaignCount.Describe(CountCampaigns(_localSlots), CountCampaigns(_onlineSlots));

        // Both lists, because the toggle can be on either one and switching realms should land on a
        // list of slots with a worked example already open rather than on a wall of closed cards.
        OpenFirstCampaign(_localSlots);
        OpenFirstCampaign(_onlineSlots);
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
    /// Which realm the slot sections are showing. The toggle above them sets it, and the window
    /// carries the choice from one selection to the next, so reading the same slot across several
    /// backups does not drop back to the local saves every time.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLocal))]
    [NotifyPropertyChangedFor(nameof(Slots))]
    [NotifyPropertyChangedFor(nameof(HasSlots))]
    [NotifyPropertyChangedFor(nameof(HasNoSlots))]
    [NotifyPropertyChangedFor(nameof(ActiveEmptyText))]
    [NotifyPropertyChangedFor(nameof(HasNoOnlineSlots))]
    private bool showOnline;

    /// <summary>
    /// The other half of the toggle. Settable because both halves bind two way: a screen reader
    /// selecting a radio button sets its checked state directly and raises no click, so a toggle
    /// driven by a command instead would do nothing at all for that user.
    /// </summary>
    public bool ShowLocal
    {
        get => !ShowOnline;
        set => ShowOnline = !value;
    }

    /// <summary>
    /// The save files on screen, one section each: the local ones, or Rain Meadow's online ones.
    /// Both sets are built up front, so the toggle costs no parsing.
    /// </summary>
    public IReadOnlyList<SlotViewModel> Slots => ShowOnline ? _onlineSlots : _localSlots;

    public bool HasSlots => Slots.Count > 0;

    public bool HasNoSlots => Slots.Count == 0;

    /// <summary>The line shown in place of the slot sections when the chosen realm has none.</summary>
    public string ActiveEmptyText => ShowOnline ? _onlineEmptyText : _localEmptyText;

    /// <summary>
    /// One row per slot number, local and online together. A slot with no online file still gets a
    /// row, because an empty online slot is what a player about to copy a save across is looking at.
    /// </summary>
    public IReadOnlyList<SlotPairViewModel> SlotPairs { get; }

    public bool HasOnlineSlots => SlotPairs.Any(pair => pair.Online.Exists);

    /// <summary>
    /// Whether the pair rows should say there is nothing online yet. Silent while the sections
    /// above are showing the online realm, because those are already saying it and the two lines
    /// land on one screen.
    /// </summary>
    public bool HasNoOnlineSlots => SlotPairs.Count > 0 && !HasOnlineSlots && !ShowOnline;

    /// <summary>
    /// Whether the Rain Meadow block and the realm toggle are drawn. Both follow the mod being on
    /// the machine, not the folder happening to hold an online save, so a player who has the mod
    /// but has not played online still gets the rows to copy a save across. A player without the
    /// mod sees neither.
    /// </summary>
    /// Set by the window after the detail is built, because only the window knows what is on the
    /// machine. The whole detail is replaced on every rebuild, so this needs no change notification.
    public bool ShowMeadowSection { get; set; }

    /// <summary>"v0.1.15.1", or empty when the version was not read.</summary>
    public string MeadowVersionText { get; set; } = "";

    /// <summary>"2 online saves", for the section band.</summary>
    public string OnlineCountText { get; }

    /// <summary>meadow.json, or null when the folder holds no such file.</summary>
    public MeadowProfileViewModel? Meadow { get; }

    public bool HasMeadow => Meadow is not null;

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
            localEmptyText: "No save files were found in the save folder.",
            onlineEmptyText: "No online saves in this folder yet. Copy Slot in the top bar puts a local save into one.",
            backup: null,
            allSlots: slots,
            meadow: meadow,
            icons: icons);
    }

    /// <summary>One backup, read out of the manifest that was written with it.</summary>
    public static SnapshotDetailViewModel ForBackup(
        BackupItemViewModel item,
        MeadowProfile? meadow,
        ISlugcatIconProvider icons)
    {
        var source = item.Snapshot.Manifest?.Slots;

        // A snapshot with no manifest recorded nothing about either realm, so both sides say the
        // same thing. With a manifest, the realm the toggle is on is the one that came up short.
        var noManifest = "This snapshot has no manifest, so it recorded no campaign detail.";

        var empty = item.Snapshot.Manifest is null
            ? noManifest
            : "This snapshot's manifest recorded no local saves.";

        var onlineEmpty = item.Snapshot.Manifest is null
            ? noManifest
            : "This snapshot's manifest recorded no Rain Meadow online saves.";

        return new SnapshotDetailViewModel(
            isLive: false,
            title: item.LabelText,
            subtitle: item.CreatedText + "    " + item.Snapshot.Id,
            kindText: item.KindText,
            sizeText: item.SizeText,
            fileCountText: item.FileCountText,
            noteText: item.NoteText,
            localEmptyText: empty,
            onlineEmptyText: onlineEmpty,
            backup: item,
            allSlots: (IReadOnlyList<SlotMetadata>?)source ?? Array.Empty<SlotMetadata>(),
            meadow: meadow,
            icons: icons);
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
