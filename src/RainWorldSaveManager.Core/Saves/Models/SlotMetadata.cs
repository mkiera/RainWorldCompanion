// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Globalization;
using System.Text;

namespace RainWorldSaveManager.Core.Saves.Models;

/// <summary>
/// What one save container file says about itself, extracted best effort. Every field other
/// than <see cref="ParseError"/> may be a partial result.
/// </summary>
public sealed class SlotMetadata
{
    private readonly IReadOnlyList<CampaignSummary> _campaigns = Array.Empty<CampaignSummary>();

    /// <summary>1..3 for sav, sav2, sav3. 0 for files with no UI slot.</summary>
    public int Slot { get; init; }

    public string FileName { get; init; } = "";

    /// <summary>
    /// True when the "save" value carried a digest prefix and it recomputed correctly, false
    /// when the digest was there and wrong, and null when there was no digest to check. The
    /// third state is a real one: several keys in this format store a raw unchecksummed payload,
    /// and the game loads them.
    /// </summary>
    public bool? ChecksumValid { get; init; }

    /// <summary>
    /// The campaigns in this file, never null. A manifest.json carrying an explicit null here
    /// would defeat a plain field initialiser, so the init accessor turns one into an empty list.
    /// </summary>
    public IReadOnlyList<CampaignSummary> Campaigns
    {
        get => _campaigns;
        init => _campaigns = value ?? Array.Empty<CampaignSummary>();
    }

    /// <summary>Non-null means extraction failed and the other fields are empty.</summary>
    public string? ParseError { get; init; }

    /// <summary>One line for the UI, for example "Slot 2: White cycle 17".</summary>
    public string Describe()
    {
        string label = Slot > 0
            ? "Slot " + Slot.ToString(CultureInfo.InvariantCulture)
            : (string.IsNullOrEmpty(FileName) ? "Slot 0" : FileName);

        if (ParseError is not null)
        {
            return label + ": unreadable (" + ParseError + ")";
        }

        if (Campaigns.Count == 0)
        {
            return label + ": empty";
        }

        var text = new StringBuilder(label);
        text.Append(": ");

        for (int i = 0; i < Campaigns.Count; i++)
        {
            if (i > 0)
            {
                text.Append(", ");
            }

            CampaignSummary campaign = Campaigns[i];
            text.Append(string.IsNullOrEmpty(campaign.SlugcatId)
                ? CampaignSummary.UnknownSlugcat
                : campaign.SlugcatId);

            if (campaign.CycleNum.HasValue)
            {
                text.Append(" cycle ");
                text.Append(campaign.CycleNum.Value.ToString(CultureInfo.InvariantCulture));
            }
        }

        // Only an explicit false is worth saying. That means the digest was there and did not
        // recompute, which is the state where the game discards the save. A null means the value
        // carried no digest, which is normal for several keys in this format and says nothing
        // about whether the save is sound.
        if (ChecksumValid == false)
        {
            text.Append(" (checksum bad)");
        }

        return text.ToString();
    }
}
