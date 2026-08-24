// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Globalization;

namespace RainWorldSaveManager.Core.Saves.Models;

/// <summary>
/// One campaign read out of a SAVE STATE record: which slugcat, how far the run has got,
/// and how many Devourment mod states the same record carries.
/// </summary>
public sealed class CampaignSummary
{
    /// <summary>Value of the "SAV STATE NUMBER" field, for example White, Rivulet, Saint.</summary>
    public string SlugcatId { get; init; } = "";

    public int? CycleNum { get; init; }

    public int? Food { get; init; }

    /// <summary>Value of DENPOS, for example SU_S04.</summary>
    public string? DenPos { get; init; }

    public string? Seed { get; init; }

    /// <summary>Number of DEVOURMENTSTATE fields in the record.</summary>
    public int DevourmentStateCount { get; init; }

    /// <summary>True when the record carries a bare HASTHEGLOW flag.</summary>
    public bool HasGlow { get; init; }

    /// <summary>Short single line for the UI, for example "White  cycle 17  food 3".</summary>
    public string Describe()
    {
        string name = string.IsNullOrEmpty(SlugcatId) ? UnknownSlugcat : SlugcatId;
        var parts = new List<string>(3) { name };

        if (CycleNum.HasValue)
        {
            parts.Add("cycle " + CycleNum.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (Food.HasValue)
        {
            parts.Add("food " + Food.Value.ToString(CultureInfo.InvariantCulture));
        }

        return string.Join("  ", parts);
    }

    /// <summary>Stand-in when a SAVE STATE record has no slugcat field.</summary>
    public const string UnknownSlugcat = "(unknown)";
}
