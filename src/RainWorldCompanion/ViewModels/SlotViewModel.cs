// Usings sit above the namespace: RainWorldCompanion.Core.System would otherwise shadow System.
using System.Globalization;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.Core.Saves.Models;
using RainWorldCompanion.Services;

namespace RainWorldCompanion.ViewModels;

/// <summary>The card and the panel build their own instances, so expanding one leaves the other.</summary>
public sealed class SlotViewModel
{
    private const int MaxRowPortraits = 6;

    /// <param name="fileNameOverride">
    /// A library save is parsed out of the copy kept under the library's own storage name, so its
    /// metadata says save.bin and only the entry's manifest knows the container it came from.
    /// </param>
    /// <param name="nameRealm">
    /// True where the realm toggle picks between two files that share a slot number, so the number
    /// alone does not say which one is on screen.
    /// </param>
    /// <param name="editable">True only for the live campaigns.</param>
    /// <param name="sourceDirectory">
    /// Empty when there is no file to go back to, which is what a backup with a manifest but no
    /// snapshot on disk looks like.
    /// </param>
    /// <param name="sourceLabel">For example "backup 2026-08-24_120000".</param>
    /// <param name="sourceFileOverride">
    /// The file inside <paramref name="sourceDirectory"/>, when it is not named after the slot.
    /// </param>
    public SlotViewModel(
        SlotMetadata slot,
        ISlugcatIconProvider icons,
        string? fileNameOverride = null,
        bool nameRealm = false,
        bool editable = false,
        string sourceDirectory = "",
        string sourceLabel = "",
        string sourceFileOverride = "",
        bool storable = false,
        SlotMetadata? live = null)
    {
        Metadata = slot;
        SlotNumber = slot.Slot;
        FileName = string.IsNullOrEmpty(fileNameOverride) ? slot.FileName : fileNameOverride;
        Realm = slot.Realm;

        // online_sav2 carries the same slot number as sav2.
        string prefix = nameRealm && slot.Realm == SaveRealm.Online ? "ONLINE SLOT " : "SLOT ";

        NumberText = slot.Slot > 0 ? slot.Slot.ToString(CultureInfo.InvariantCulture) : "?";
        HeaderText = slot.Slot > 0
            ? prefix + NumberText
            : (FileName.Length > 0 ? FileName.ToUpperInvariant() : "SLOT");

        // A slot number outside 1 to 3 has no file the writer will target, so those campaigns are
        // shown without an Edit button rather than with one that refuses.
        SaveSlotRef? editableSlot = editable && slot.Slot is >= SaveSlotRef.MinSlot and <= SaveSlotRef.MaxSlot
            ? new SaveSlotRef(slot.Realm, slot.Slot)
            : null;

        EditableSlot = editableSlot;

        CampaignSource? source = BuildSource(
            slot, editableSlot, sourceDirectory, sourceLabel, sourceFileOverride);

        Source = source;
        Storable = storable;

        // Matched by slugcat inside the slot this one would be written over. A campaign the live
        // slot does not hold has nothing to compare against, so its tiles stay unmarked.
        Campaigns = slot.Campaigns
            .Select(campaign => new CampaignViewModel(campaign, icons, source, LiveCampaign(live, campaign.SlugcatId)))
            .ToList();

        ComparedToLive = live is not null;
        _liveCampaignCount = live?.Campaigns.Count ?? 0;
        Portraits = BuildPortraits(slot, icons);

        HasParseError = slot.ParseError is not null;
        ParseErrorText = slot.ParseError ?? "";
        ChecksumBad = slot.ChecksumValid == false;
        SummaryText = BuildSummary(slot);
        CampaignCountText = BuildCampaignCount(slot);
    }

    public SlotMetadata Metadata { get; }

    /// <summary>Whether a live slot of the same number was found to compare against.</summary>
    public bool ComparedToLive { get; }

    /// <summary>
    /// Different if any campaign it shares with the live slot differs, or if the two do not hold
    /// the same set of campaigns at all. A campaign this slot has and the live one does not is a
    /// difference between the slots even though that campaign has nothing to compare against.
    /// </summary>
    public bool DiffersFromLive =>
        ComparedToLive
        && (Campaigns.Any(campaign => campaign.DiffersFromLive)
            || Campaigns.Any(campaign => !campaign.ComparedToLive)
            || _liveCampaignCount != Campaigns.Count);

    /// <summary>Empty unless there was a live slot to compare against.</summary>
    public string LiveComparisonText =>
        !ComparedToLive ? "" : DiffersFromLive ? "Differs from live" : "Same as live";

    public bool HasLiveComparisonText => LiveComparisonText.Length > 0;

    private readonly int _liveCampaignCount;

    /// <summary>Where this slot's bytes are, which a library save already holds a copy of.</summary>
    public CampaignSource? Source { get; }

    /// <summary>
    /// False for a library save, whose whole point is that the slot is already stored. The live
    /// folder and a backup are the two places a slot can be taken from.
    /// </summary>
    public bool Storable { get; }

    /// <summary>
    /// A slot with no records at all would store a file the game reads as empty, which is a copy
    /// nobody wants under a name they had to type.
    /// </summary>
    public bool CanStoreToLibrary =>
        Storable && Source is { CanBeTaken: true } && (Campaigns.Count > 0 || Metadata.RecordCount > 0);

    /// <summary>1, 2 or 3. Zero for a save file with no numbered slot.</summary>
    public int SlotNumber { get; }

    public SaveRealm Realm { get; }

    public string NumberText { get; }

    public string HeaderText { get; }

    public string FileName { get; }

    public string SummaryText { get; }

    public string CampaignCountText { get; }

    public IReadOnlyList<PortraitViewModel> Portraits { get; }

    public IReadOnlyList<CampaignViewModel> Campaigns { get; }

    /// <summary>Null unless this is the live folder and a slot number the game itself has.</summary>
    public SaveSlotRef? EditableSlot { get; }

    /// <summary>
    /// Campaigns are not the test. A slot whose campaign was wiped still holds the map it explored
    /// and its progression record, which is what deleting all of it is for.
    /// </summary>
    public bool CanDelete => EditableSlot is not null && (Campaigns.Count > 0 || Metadata.RecordCount > 0);

    public bool HasCampaigns => Campaigns.Count > 0;

    public bool HasNoCampaigns => Campaigns.Count == 0;

    public bool HasParseError { get; }

    public string ParseErrorText { get; }

    /// <summary>
    /// True only when the file carried a digest and it did not recompute. A file with no digest is
    /// normal in this format.
    /// </summary>
    public bool ChecksumBad { get; }

    /// <summary>
    /// A Rain Meadow save routinely holds the explored map and the progression record with no
    /// campaign among them, so the two empty cases are worded apart.
    /// </summary>
    public string EmptyText
    {
        get
        {
            if (HasParseError)
            {
                return "This save file could not be read: " + ParseErrorText;
            }

            return Metadata.RecordCount > 0
                ? "No campaign is saved here. The file still holds the map you have explored and the progression record."
                : "This save file is empty.";
        }
    }

    /// <summary>
    /// A backup's panel is filled from its manifest, so the campaigns can be described without the
    /// snapshot folder being there. Taking one out needs the file: no folder, no source, no buttons.
    /// </summary>
    private static CampaignSummary? LiveCampaign(SlotMetadata? live, string slugcatId)
    {
        if (live is null)
        {
            return null;
        }

        foreach (var campaign in live.Campaigns)
        {
            if (string.Equals(campaign.SlugcatId, slugcatId, StringComparison.OrdinalIgnoreCase))
            {
                return campaign;
            }
        }

        return null;
    }

    private static CampaignSource? BuildSource(
        SlotMetadata slot,
        SaveSlotRef? editableSlot,
        string sourceDirectory,
        string sourceLabel,
        string sourceFileOverride)
    {
        string fileName = sourceFileOverride.Length > 0 ? sourceFileOverride : slot.FileName;

        if (sourceDirectory.Length == 0 || fileName.Length == 0)
        {
            return null;
        }

        string path;
        try
        {
            path = System.IO.Path.Combine(sourceDirectory, fileName);
        }
        catch (ArgumentException)
        {
            return null;
        }

        return new CampaignSource(
            path,
            sourceLabel.Length > 0 ? sourceLabel : fileName,
            editableSlot,
            slot.Realm,
            slot.Slot,
            fileName);
    }

    private static IReadOnlyList<PortraitViewModel> BuildPortraits(SlotMetadata slot, ISlugcatIconProvider icons)
    {
        var portraits = new List<PortraitViewModel>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var campaign in slot.Campaigns)
        {
            if (!seen.Add(campaign.SlugcatId))
            {
                continue;
            }

            var info = SlugcatCatalog.ForId(campaign.SlugcatId);
            var cycle = campaign.CycleNum.HasValue
                ? "  cycle " + campaign.CycleNum.Value.ToString(CultureInfo.InvariantCulture)
                : "";

            portraits.Add(new PortraitViewModel(info, icons.GetIcon(campaign.SlugcatId), info.DisplayName + cycle));

            if (portraits.Count == MaxRowPortraits)
            {
                break;
            }
        }

        return portraits;
    }

    private static string BuildSummary(SlotMetadata slot)
    {
        if (slot.ParseError is not null)
        {
            return "unreadable";
        }

        if (slot.Campaigns.Count == 0)
        {
            // This line sits beside a button that overwrites the file.
            return SlotMetadata.DescribeWithoutCampaigns(slot.RecordCount);
        }

        var names = slot.Campaigns
            .Select(campaign => SlugcatCatalog.ForId(campaign.SlugcatId).DisplayName)
            .ToList();

        var shown = string.Join(", ", names.Take(4));
        return names.Count > 4 ? shown + " and " + (names.Count - 4) + " more" : shown;
    }

    private static string BuildCampaignCount(SlotMetadata slot)
    {
        if (slot.ParseError is not null)
        {
            return "unreadable";
        }

        return slot.Campaigns.Count switch
        {
            0 => "no campaigns",
            1 => "1 campaign",
            _ => slot.Campaigns.Count.ToString(CultureInfo.InvariantCulture) + " campaigns",
        };
    }
}
