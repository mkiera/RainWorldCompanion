// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists in the referenced assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Globalization;
using RainWorldSaveManager.Core.Saves;
using RainWorldSaveManager.Core.Saves.Models;
using RainWorldSaveManager.Services;

namespace RainWorldSaveManager.ViewModels;

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

    public SlotViewModel(SlotMetadata slot, ISlugcatIconProvider icons)
    {
        Metadata = slot;
        SlotNumber = slot.Slot;
        FileName = slot.FileName;

        NumberText = slot.Slot > 0 ? slot.Slot.ToString(CultureInfo.InvariantCulture) : "?";
        HeaderText = slot.Slot > 0
            ? "SLOT " + NumberText
            : (slot.FileName.Length > 0 ? slot.FileName.ToUpperInvariant() : "SLOT");

        Campaigns = slot.Campaigns.Select(campaign => new CampaignViewModel(campaign, icons)).ToList();
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

    public string NumberText { get; }

    public string HeaderText { get; }

    public string FileName { get; }

    /// <summary>One line for a compact row, for example "3 campaigns: Survivor, Monk, Hunter".</summary>
    public string SummaryText { get; }

    public string CampaignCountText { get; }

    /// <summary>Faces for the campaigns in this slot, capped so a busy slot still fits a row.</summary>
    public IReadOnlyList<PortraitViewModel> Portraits { get; }

    public IReadOnlyList<CampaignViewModel> Campaigns { get; }

    public bool HasCampaigns => Campaigns.Count > 0;

    public bool HasNoCampaigns => Campaigns.Count == 0;

    public bool HasParseError { get; }

    public string ParseErrorText { get; }

    /// <summary>
    /// True only when the file carried a digest and it did not recompute. A file with no digest
    /// is normal in this format and says nothing about whether the save is sound.
    /// </summary>
    public bool ChecksumBad { get; }

    /// <summary>The line shown when a slot holds nothing to expand.</summary>
    public string EmptyText => HasParseError
        ? "This save file could not be read: " + ParseErrorText
        : "This slot holds no campaigns.";

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
            return "empty";
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
