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

    /// <summary>
    /// 1..3 for sav, sav2, sav3, and the same 1..3 for online_sav, online_sav2, online_sav3.
    /// 0 for files with no UI slot. <see cref="Realm"/> is what separates the two sets, because
    /// the game picks both from one Options.saveSlot and they share the numbering.
    /// </summary>
    public int Slot { get; init; }

    public string FileName { get; init; } = "";

    /// <summary>
    /// Whether this came from a local container or a Rain Meadow online one. Defaults to
    /// <see cref="SaveRealm.Local"/>, which is also what a manifest written before online
    /// slots were read deserialises to, since it carries no such key.
    /// </summary>
    public SaveRealm Realm { get; init; } = SaveRealm.Local;

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

    /// <summary>
    /// How many records the save payload holds, of every kind, or null when the count is not
    /// known. A manifest written before this was recorded carries no value, and reads back as null.
    ///
    /// <see cref="Campaigns"/> counts only the SAVE STATE records, and a Rain Meadow online_sav
    /// routinely holds none of those while holding the explored map and the MISCPROG record. That
    /// file is 12 KB of real progress, so an empty campaign list on its own does not make a
    /// container empty. This count is what separates the two, through
    /// <see cref="DescribeWithoutCampaigns"/>.
    /// </summary>
    public int? RecordCount { get; init; }

    /// <summary>
    /// What to call a container that holds no campaign. A method rather than a property so it
    /// stays out of the manifest json, and shared so the three places that word this say the same
    /// thing.
    ///
    /// "empty" is kept for a payload with no records at all, which is what an untouched slot looks
    /// like: online_sav3 on a fresh install is the digest and nothing after it. A file with records
    /// but no SAVE STATE among them is a Rain Meadow online save holding the explored map and the
    /// MISCPROG record, and calling that empty next to a button that overwrites it is wrong.
    /// A null count comes from a manifest written before the count was recorded, and falls back to
    /// the old wording rather than guessing.
    /// </summary>
    public static string DescribeWithoutCampaigns(int? recordCount) =>
        recordCount > 0 ? "no campaigns, map and progression data only" : "empty";

    /// <summary>
    /// One line for the UI, for example "Slot 2: White cycle 17", or "Online slot 2: White cycle 4"
    /// for a Rain Meadow container.
    /// </summary>
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
