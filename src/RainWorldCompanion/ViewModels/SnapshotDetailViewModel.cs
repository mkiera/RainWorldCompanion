// Usings sit above the namespace: RainWorldCompanion.Core.System would otherwise shadow System.
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.Core.Mods;
using RainWorldCompanion.Core.Saves.Models;
using RainWorldCompanion.Services;

namespace RainWorldCompanion.ViewModels;

/// <summary>
/// A backup is filled from the manifest written with it, so selecting one costs no disk read, and
/// a schema version 1 manifest renders with dashes rather than failing to render. A library save
/// is one file: no second half to pair against and no realm to switch.
/// </summary>
public sealed partial class SnapshotDetailViewModel : ObservableObject
{
    private readonly IReadOnlyList<SlotViewModel> _localSlots;
    private readonly IReadOnlyList<SlotViewModel> _onlineSlots;
    private readonly string _localEmptyText;
    private readonly string _onlineEmptyText;

    private SnapshotDetailViewModel(
        ModListSectionViewModel modsSection,
        ModConfigSectionViewModel configsSection,
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
        LibraryEntryViewModel? entry,
        IReadOnlyList<SlotMetadata> allSlots,
        MeadowProfile? meadow,
        ISlugcatIconProvider icons,
        string sourceDirectory = "",
        string sourceLabel = "",
        IReadOnlyList<SlotMetadata>? liveSlots = null)
    {
        Mods = modsSection;
        Configs = configsSection;
        IsLive = isLive;
        Title = title;
        Subtitle = subtitle;
        KindText = kindText;
        SizeText = sizeText;
        FileCountText = fileCountText;
        NoteText = noteText;
        Backup = backup;
        Entry = entry;

        _localEmptyText = localEmptyText;
        _onlineEmptyText = onlineEmptyText;

        // A library save is not split by realm: that would drop an online sourced save out of Slots
        // and leave the panel with nothing in it.
        _localSlots = entry is not null
            ? BuildSlots(
                allSlots,
                icons,
                entry.Entry.Manifest?.SourceFileName,
                sourceDirectory: entry.Entry.DirectoryPath,
                sourceLabel: "the library save \"" + entry.Name + "\"",
                sourceFileOverride: entry.Entry.ContentFileName,
                liveSlots: liveSlots)
            : BuildSlots(
                allSlots.Where(slot => slot.Realm != SaveRealm.Online),
                icons,
                nameRealm: true,
                editable: isLive,
                sourceDirectory: sourceDirectory,
                sourceLabel: sourceLabel,
                storable: true,
                liveSlots: liveSlots);
        _onlineSlots = entry is not null
            ? Array.Empty<SlotViewModel>()
            : BuildSlots(
                allSlots.Where(slot => slot.Realm == SaveRealm.Online),
                icons,
                nameRealm: true,
                editable: isLive,
                sourceDirectory: sourceDirectory,
                sourceLabel: sourceLabel,
                storable: true,
                liveSlots: liveSlots);

        SlotPairs = entry is not null ? Array.Empty<SlotPairViewModel>() : BuildPairs(_localSlots, _onlineSlots);
        OnlineCountText = FormatFileCount(_onlineSlots.Count, "online save");

        Meadow = meadow is null ? null : new MeadowProfileViewModel(meadow);
        CampaignCountText = CampaignCount.Describe(CountCampaigns(_localSlots), CountCampaigns(_onlineSlots));
    }

    public bool IsLive { get; }

    public string Title { get; }

    public string Subtitle { get; }

    public string KindText { get; }

    public string SizeText { get; }

    public string FileCountText { get; }

    public string CampaignCountText { get; }

    public string NoteText { get; }

    public bool HasNote => NoteText.Length > 0;

    /// <summary>The header binds verify state through this, so Verify updates without a rebuild.</summary>
    public BackupItemViewModel? Backup { get; }

    public bool HasBackup => Backup is not null;

    /// <summary>The library row this panel was built from, or null for anything else.</summary>
    public LibraryEntryViewModel? Entry { get; }

    public bool HasEntry => Entry is not null;

    /// <summary>The window carries this choice from one selection to the next.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLocal))]
    [NotifyPropertyChangedFor(nameof(Slots))]
    [NotifyPropertyChangedFor(nameof(HasSlots))]
    [NotifyPropertyChangedFor(nameof(HasNoSlots))]
    [NotifyPropertyChangedFor(nameof(ActiveEmptyText))]
    [NotifyPropertyChangedFor(nameof(HasNoOnlineSlots))]
    private bool showOnline;

    /// <summary>
    /// Settable because both halves bind two way: a screen reader selecting a radio button sets its
    /// checked state directly and raises no click, so a command-driven toggle would do nothing.
    /// </summary>
    public bool ShowLocal
    {
        get => !ShowOnline;
        set => ShowOnline = !value;
    }

    /// <summary>
    /// A library save ignores the realm and puts its one file here, because a realm carried over
    /// from the previous selection would otherwise leave the panel showing nothing.
    /// </summary>
    public IReadOnlyList<SlotViewModel> Slots => ShowOnline && !HasEntry ? _onlineSlots : _localSlots;

    public bool HasSlots => Slots.Count > 0;

    public bool HasNoSlots => Slots.Count == 0;

    public string ActiveEmptyText => ShowOnline ? _onlineEmptyText : _localEmptyText;

    /// <summary>A slot with no online file still gets a row. Empty for a library save.</summary>
    public IReadOnlyList<SlotPairViewModel> SlotPairs { get; }

    public bool HasOnlineSlots => SlotPairs.Any(pair => pair.Online.Exists);

    /// <summary>
    /// Silent while the sections above are showing the online realm, because those already say it
    /// and the two lines land on one screen.
    /// </summary>
    public bool HasNoOnlineSlots => SlotPairs.Count > 0 && !HasOnlineSlots && !ShowOnline;

    /// <summary>
    /// Set by the window after the detail is built. The whole detail is replaced on every rebuild,
    /// so this needs no change notification.
    /// </summary>
    public bool MeadowInstalled { get; set; }

    /// <summary>
    /// Follows the mod being on the machine, not the folder happening to hold an online save, so a
    /// player who has the mod but has not played online still gets the rows to copy a save across.
    /// </summary>
    public bool ShowMeadowSection => MeadowInstalled && !HasEntry;

    public string MeadowVersionText { get; set; } = "";

    public string OnlineCountText { get; }

    /// <summary>Never null: "nothing was recorded" is itself a line worth drawing.</summary>
    public ModListSectionViewModel Mods { get; }

    /// <summary>Which mods' settings this carries, beside the list of which mods were on.</summary>
    public ModConfigSectionViewModel Configs { get; }

    /// <summary>meadow.json, or null when the folder holds no such file.</summary>
    public MeadowProfileViewModel? Meadow { get; }

    public bool HasMeadow => Meadow is not null;

    public static SnapshotDetailViewModel ForLive(
        IReadOnlyList<SlotMetadata> slots,
        string savePath,
        long sizeBytes,
        int fileCount,
        MeadowProfile? meadow,
        ISlugcatIconProvider icons,
        CurrentMods? mods = null,
        ModConfigSet? configs = null)
    {
        return new SnapshotDetailViewModel(
            modsSection: ModListSectionViewModel.ForCurrent(mods),
            configsSection: ModConfigSectionViewModel.ForCurrent(configs),
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
            entry: null,
            allSlots: slots,
            meadow: meadow,
            icons: icons,
            sourceDirectory: savePath,
            sourceLabel: "");
    }

    /// <summary>Filled from the manifest written beside it, so selecting a row costs no disk read.</summary>
    /// <param name="live">
    /// The settings in the save folder now, so a row can say whether taking it would change
    /// anything. Null leaves them unlabelled.
    /// </param>
    public static SnapshotDetailViewModel ForLibraryEntry(
        LibraryEntryViewModel item,
        ISlugcatIconProvider icons,
        ModConfigSet? live = null,
        IReadOnlyList<SlotMetadata>? liveSlots = null)
    {
        var metadata = item.Entry.Manifest?.Metadata;

        var empty = item.Entry.Manifest is null
            ? "This save did not finish being stored, so it recorded no campaign detail."
            : "This save could not be read, so there is no campaign detail to show.";

        var when = item.WasUpdated
            ? "updated " + item.ModifiedText
            : "stored " + item.ModifiedText;

        var subtitle = item.SourceText.Length > 0
            ? when + "    " + item.SourceText + "    " + item.Entry.Id
            : when + "    " + item.Entry.Id;

        return new SnapshotDetailViewModel(
            modsSection: ModListSectionViewModel.ForRecorded(item.Entry.Manifest?.Mods, fromABackup: false),
            configsSection: ModConfigSectionViewModel.ForRecorded(item.Entry.Manifest?.Configs, fromABackup: false, live),
            isLive: false,
            title: item.Name,
            subtitle: subtitle,
            kindText: "Library save",
            sizeText: item.SizeText,
            fileCountText: "1 save file",
            noteText: item.NoteText,
            // One file, so the realm never changes what this panel would say is missing.
            localEmptyText: empty,
            onlineEmptyText: empty,
            backup: null,
            entry: item,
            allSlots: metadata is null ? Array.Empty<SlotMetadata>() : new[] { metadata },
            meadow: null,
            icons: icons,
            liveSlots: liveSlots);
    }

    public static SnapshotDetailViewModel ForBackup(
        BackupItemViewModel item,
        MeadowProfile? meadow,
        ISlugcatIconProvider icons,
        ModConfigSet? live = null,
        IReadOnlyList<SlotMetadata>? liveSlots = null)
    {
        var source = item.Snapshot.Manifest?.Slots;

        // A snapshot with no manifest recorded nothing about either realm.
        var noManifest = "This snapshot has no manifest, so it recorded no campaign detail.";

        var empty = item.Snapshot.Manifest is null
            ? noManifest
            : "This snapshot's manifest recorded no local saves.";

        var onlineEmpty = item.Snapshot.Manifest is null
            ? noManifest
            : "This snapshot's manifest recorded no Rain Meadow online saves.";

        return new SnapshotDetailViewModel(
            modsSection: ModListSectionViewModel.ForRecorded(item.Snapshot.Manifest?.Mods, fromABackup: true),
            configsSection: ModConfigSectionViewModel.ForBackup(item.Snapshot.Manifest?.Files, live),
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
            entry: null,
            allSlots: (IReadOnlyList<SlotMetadata>?)source ?? Array.Empty<SlotMetadata>(),
            meadow: meadow,
            icons: icons,
            // The panel is filled from the manifest, but taking a campaign out needs the file, so
            // the folder the snapshot is in comes along too.
            sourceDirectory: item.Snapshot.DirectoryPath,
            sourceLabel: "backup " + item.Snapshot.Id);
    }

    /// <param name="fileNameOverride">
    /// Only a library save passes this, having been parsed out of the copy kept under the library's
    /// own storage name.
    /// </param>
    /// <param name="nameRealm">
    /// True wherever the realm toggle can swap these sections for the other realm's, which is
    /// everything except a library save.
    /// </param>
    /// <param name="editable">True only for the live save folder.</param>
    private static IReadOnlyList<SlotViewModel> BuildSlots(
        IEnumerable<SlotMetadata> slots,
        ISlugcatIconProvider icons,
        string? fileNameOverride = null,
        bool nameRealm = false,
        bool editable = false,
        string sourceDirectory = "",
        string sourceLabel = "",
        string sourceFileOverride = "",
        bool storable = false,
        IReadOnlyList<SlotMetadata>? liveSlots = null)
    {
        return slots
            .OrderBy(slot => slot.Slot == 0 ? int.MaxValue : slot.Slot)
            .ThenBy(slot => slot.FileName, StringComparer.OrdinalIgnoreCase)
            .Select(slot => new SlotViewModel(
                slot, icons, fileNameOverride, nameRealm, editable, sourceDirectory, sourceLabel,
                sourceFileOverride, storable, LiveSlot(liveSlots, slot)))
            .ToList();
    }

    /// <summary>
    /// The live slot these bytes would be written over, matched by realm and number. Slot zero is
    /// a file with no numbered slot, and two of those cannot be told apart by number, so they are
    /// matched by file name instead.
    /// </summary>
    private static SlotMetadata? LiveSlot(IReadOnlyList<SlotMetadata>? liveSlots, SlotMetadata slot)
    {
        if (liveSlots is null)
        {
            return null;
        }

        foreach (var live in liveSlots)
        {
            if (live.Realm != slot.Realm)
            {
                continue;
            }

            var matches = slot.Slot > 0
                ? live.Slot == slot.Slot
                : string.Equals(live.FileName, slot.FileName, StringComparison.OrdinalIgnoreCase);

            if (matches)
            {
                return live;
            }
        }

        return null;
    }

    /// <summary>A row with nothing on either side still shows which online slots are empty.</summary>
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
