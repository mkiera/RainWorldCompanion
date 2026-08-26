// RainWorldCompanion.Core.System exists in this assembly, so a using written inside the namespace
// body would bind "System" to that namespace instead of the BCL root.
using System.Globalization;
using System.Text;

namespace RainWorldCompanion.Core.Saves.Models;

/// <summary>Extracted best effort: every field other than <see cref="ParseError"/> may be a partial
/// result.</summary>
public sealed class SlotMetadata
{
    private readonly IReadOnlyList<CampaignSummary> _campaigns = Array.Empty<CampaignSummary>();

    /// <summary>1..3 for sav, sav2, sav3, and the same 1..3 for online_sav, online_sav2, online_sav3.
    /// 0 for files with no UI slot. <see cref="Realm"/> is what separates the two sets.</summary>
    public int Slot { get; init; }

    public string FileName { get; init; } = "";

    /// <summary>Defaults to <see cref="SaveRealm.Local"/>, which is also what a manifest written
    /// before online slots were read deserialises to, since it carries no such key.</summary>
    public SaveRealm Realm { get; init; } = SaveRealm.Local;

    /// <summary>True when the digest recomputed, false when it was there and wrong, and null when
    /// there was no digest to check. The third state is real: several keys in this format store a
    /// raw unchecksummed payload, and the game loads them.</summary>
    public bool? ChecksumValid { get; init; }

    /// <summary>Never null. A manifest.json carrying an explicit null here would defeat a plain
    /// field initialiser, so the init accessor turns one into an empty list.</summary>
    public IReadOnlyList<CampaignSummary> Campaigns
    {
        get => _campaigns;
        init => _campaigns = value ?? Array.Empty<CampaignSummary>();
    }

    /// <summary>Non-null means extraction failed and the other fields are empty.</summary>
    public string? ParseError { get; init; }

    /// <summary>Records of every kind, or null when the count is not known, which is what a manifest
    /// written before this was recorded reads back as. <see cref="Campaigns"/> counts only the SAVE
    /// STATE records, and a Rain Meadow online_sav routinely holds none of those.</summary>
    public int? RecordCount { get; init; }

    /// <summary>"empty" is kept for a payload with no records at all. A file with records but no SAVE
    /// STATE among them is a Rain Meadow online save holding the explored map and the MISCPROG
    /// record, and calling that empty next to a button that overwrites it is wrong.</summary>
    public static string DescribeWithoutCampaigns(int? recordCount) =>
        recordCount > 0 ? "no campaigns, map and progression data only" : "empty";

    /// <summary>One line for the UI, for example "Slot 2: White cycle 17".</summary>
    public string Describe()
    {
        string prefix = Realm == SaveRealm.Online ? "Online slot " : "Slot ";
        string label = Slot > 0
            ? prefix + Slot.ToString(CultureInfo.InvariantCulture)
            : (string.IsNullOrEmpty(FileName) ? prefix + "0" : FileName);

        if (ParseError is not null)
        {
            return label + ": unreadable (" + ParseError + ")";
        }

        if (Campaigns.Count == 0)
        {
            return label + ": " + DescribeWithoutCampaigns(RecordCount);
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

        // Only an explicit false is worth saying: a null carried no digest, which is normal.
        if (ChecksumValid == false)
        {
            text.Append(" (checksum bad)");
        }

        return text.ToString();
    }
}
