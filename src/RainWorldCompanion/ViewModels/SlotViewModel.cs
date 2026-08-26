// Usings sit above the namespace declaration on purpose. RainWorldCompanion.Core.System
// exists in the referenced assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Globalization;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.Core.Saves.Models;
using RainWorldCompanion.Services;

namespace RainWorldCompanion.ViewModels;

/// <summary>
/// One save file: slot 1, 2 or 3, and the campaigns inside it.
///
/// The same view model backs both the compact rows in the live save card and the full sections
/// in the detail panel, so a slot reads the same in both places. The two uses build their own
/// instances because expanding a campaign in the detail panel must not change the summary card.
/// </summary>
public sealed class SlotViewModel
{
    private const int MaxRowPortraits = 6;

    /// <param name="fileNameOverride">
    /// What to call the file this came from, when the metadata's own name is not it. A library save
    /// is parsed out of the copy kept under the library's own storage name, so its metadata says
    /// save.bin and only the entry's manifest knows the container it was taken from.
    /// </param>
    /// <param name="nameRealm">
    /// Whether the header says which realm this is. True for a panel whose sections are one realm
    /// at a time, where the realm toggle picks between two files that share a slot number and the
    /// number alone does not say which one is on screen. False for a library save, which is a single
    /// section that names the file it came from beside the title.
    /// </param>
    /// <param name="editable">
    /// True when these campaigns are the live ones and so can be edited. A backup and a library
    /// save are copies taken at a moment, and editing one in place would leave it no longer a copy
    /// of anything.
    /// </param>
    /// <param name="sourceDirectory">
    /// The folder holding the file these campaigns were read out of, so one of them can be taken
    /// back out and sent to a slot. Empty when there is no such file to go back to, which is what a
    /// backup with a manifest but no snapshot on disk looks like.
    /// </param>
    /// <param name="sourceLabel">
    /// What to call that file in a sentence, for example "backup 2026-08-24_120000". Empty for the
    /// live folder, where the file name says it.
    /// </param>
    /// <param name="sourceFileOverride">
    /// The file inside <paramref name="sourceDirectory"/>, when it is not named after the slot. A
    /// library save keeps a whole slot under the library's own storage name.
    /// </param>
    public SlotViewModel(
        SlotMetadata slot,
        ISlugcatIconProvider icons,
        string? fileNameOverride = null,
        bool nameRealm = false,
        bool editable = false,
        string sourceDirectory = "",
        string sourceLabel = "",
        string sourceFileOverride = "")
    {
        Metadata = slot;
        SlotNumber = slot.Slot;
        FileName = string.IsNullOrEmpty(fileNameOverride) ? slot.FileName : fileNameOverride;
        Realm = slot.Realm;

        // Rain Meadow's hook on Options.GetSaveFileName_SavOrExp gives online_sav2 the same slot
        // number as sav2, so the number alone does not say which of the two a header is naming.
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

        Campaigns = slot.Campaigns
            .Select(campaign => new CampaignViewModel(campaign, icons, source))
            .ToList();
        Portraits = BuildPortraits(slot, icons);

        HasParseError = slot.ParseError is not null;
        ParseErrorText = slot.ParseError ?? "";
        ChecksumBad = slot.ChecksumValid == false;
        SummaryText = BuildSummary(slot);
        CampaignCountText = BuildCampaignCount(slot);
    }

    public SlotMetadata Metadata { get; }

    /// <summary>1, 2 or 3. Zero for a save file with no numbered slot.</summary>
    public int SlotNumber { get; }

    /// <summary>Whether this came from a local save file or a Rain Meadow online one.</summary>
    public SaveRealm Realm { get; }

    public string NumberText { get; }

    public string HeaderText { get; }

    public string FileName { get; }

    /// <summary>One line for a compact row, for example "3 campaigns: Survivor, Monk, Hunter".</summary>
    public string SummaryText { get; }

    public string CampaignCountText { get; }

    /// <summary>Faces for the campaigns in this slot, capped so a busy slot still fits a row.</summary>
    public IReadOnlyList<PortraitViewModel> Portraits { get; }

    public IReadOnlyList<CampaignViewModel> Campaigns { get; }

    /// <summary>
    /// The slot these campaigns can be written to, or null when they cannot be. Only the live save
    /// folder passes one, and only for a slot number the game itself has.
    /// </summary>
    public SaveSlotRef? EditableSlot { get; }

    /// <summary>
    /// True when this slot can be emptied of its campaigns.
    ///
    /// A slot with nothing in it is left off rather than offered and refused: the plan would say
    /// there is nothing to empty, and a button that only ever reports that is one to not draw.
    /// </summary>
    public bool CanEmpty => EditableSlot is not null && Campaigns.Count > 0;

    public bool HasCampaigns => Campaigns.Count > 0;

    public bool HasNoCampaigns => Campaigns.Count == 0;

    public bool HasParseError { get; }

    public string ParseErrorText { get; }

    /// <summary>
    /// True only when the file carried a digest and it did not recompute. A file with no digest
    /// is normal in this format and says nothing about whether the save is sound.
    /// </summary>
    public bool ChecksumBad { get; }

    /// <summary>
    /// The line shown when a save holds no campaign to expand.
    ///
    /// A Rain Meadow save routinely holds the explored map and the progression record with no
    /// campaign among them. Saying only that it holds no campaign reports 12 KB of real progress as
    /// nothing, so the two cases are worded apart.
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
    /// Where these campaigns can be read back out of, or null when there is nowhere to read from.
    ///
    /// A backup's panel is filled from the manifest written beside it rather than from the files, so
    /// the campaigns can be described without the snapshot folder being there at all. Taking one out
    /// needs the file, and this is where the two part company: no folder, no source, no buttons.
    /// </summary>
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
            // A file name out of a manifest is whatever was written there. One that will not join to
            // a path is one this app cannot go back to, which is the same answer as having no folder.
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
            // A Rain Meadow online_sav with no campaign in it still holds the explored map and the
            // progression record, and this line sits beside a button that overwrites the file.
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
