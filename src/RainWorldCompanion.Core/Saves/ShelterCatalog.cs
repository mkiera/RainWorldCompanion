// Usings sit above the namespace declaration on purpose. RainWorldCompanion.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.

namespace RainWorldCompanion.Core.Saves;

/// <summary>
/// The shelter rooms of the game and its expansions, which is what DENPOS and LASTVDENPOS hold.
///
/// Extracted from the world files of Rain World 1.10 with Downpour and The Watcher, where a
/// shelter is a room whose entry in world_&lt;region&gt;.txt ends in SHELTER. Reading the files
/// rather than assuming a naming rule matters: most shelters are named &lt;REGION&gt;_S&lt;number&gt;, but
/// LC_A05 is a shelter too, and a picker built on the pattern would not offer it.
///
/// The list suggests, it does not decide. A room from a mod this app has never heard of is still
/// a room the player may be sitting in, so nothing here rejects a name for being absent.
/// </summary>
public static class ShelterCatalog
{
    private static readonly string[] AllShelters =
    {
        "CC_S01", "CC_S03", "CC_S04", "CC_S05", "CL_LCS2", "CL_S01",
        "CL_S02", "CL_S03", "CL_S05", "CL_S08", "CL_S10", "CL_S11",
        "CL_S12", "CL_S13", "CL_S14", "CL_S15", "CL_S20", "CL_S21",
        "DM_LAB5", "DM_S01", "DM_S02", "DM_S03", "DM_S04", "DM_S05",
        "DM_S06", "DM_S10", "DM_S11", "DM_S13", "DM_S14", "DM_STOP",
        "DS_S01R", "DS_S02L", "DS_S03", "DS_S04", "GW_S01", "GW_S02",
        "GW_S03", "GW_S04", "GW_S05", "GW_S06", "GW_S07", "GW_S08",
        "HI_S01", "HI_S02", "HI_S03", "HI_S04", "HI_S05", "HI_S06",
        "HR_S01", "HR_S02", "HR_S03", "HR_S04", "HR_S05", "HR_S06",
        "HR_S10", "HR_S11", "HR_S12", "HR_S1R", "HR_SHR", "LC_A05",
        "LC_S01", "LC_S03", "LC_S04", "LC_S05", "LC_S06", "LC_SHELTER_ABOVE",
        "LC_SHELTERTRAIN1", "LC_SROOFS", "LF_S01", "LF_S02", "LF_S03", "LF_S04",
        "LF_S05", "LF_S06", "LF_S07", "LM_S02", "LM_S03", "LM_S04",
        "LM_S05", "LM_S06", "LM_S07", "LM_S09", "LM_S11", "LM_S13",
        "LM_S15", "MS_BITTERSHELTER", "MS_LAB5", "MS_S01", "MS_S03", "MS_S04",
        "MS_S05", "MS_S06", "MS_S07", "MS_S09", "MS_S10", "OE_EXSHELTER",
        "OE_MIDSHELTER", "OE_S01", "OE_S03", "OE_S04", "OE_S06", "OE_SFINAL",
        "RM_LCS1", "RM_LCS2", "RM_S01", "RM_S02", "RM_S03", "RM_S04",
        "RM_S05", "RM_SDEAD", "RM_SFINAL", "SB_S01", "SB_S02", "SB_S03",
        "SB_S04", "SB_S05", "SB_S06", "SB_S07", "SH_S01", "SH_S02",
        "SH_S03", "SH_S04", "SH_S05", "SH_S06", "SH_S07", "SH_S08",
        "SH_S09", "SH_S10", "SI_S03", "SI_S04", "SI_S05", "SL_S02",
        "SL_S03", "SL_S04", "SL_S05", "SL_S06", "SL_S07", "SL_S08",
        "SL_S09", "SL_S10", "SL_S11", "SS_S01", "SS_S02", "SS_S03",
        "SS_S04", "SS_S05", "SU_S01", "SU_S03", "SU_S04", "UG_S01R",
        "UG_S02L", "UG_S03", "UG_S04", "UW_S01", "UW_S02", "UW_S03",
        "UW_S04", "UW_S05", "UW_S06", "UW_S07", "VS_S01", "VS_S02",
        "VS_S03", "VS_S04", "VS_S05", "VS_S06", "VS_S07", "VS_S08",
        "VS_S09", "VS_S20", "WARA_S22", "WARA_S23", "WARA_S24", "WARB_S11",
        "WARB_S15", "WARB_S17", "WARB_S29", "WARB_S31", "WARC_S01", "WARC_S02",
        "WARC_S03", "WARC_S04", "WARC_S05", "WARC_S06", "WARC_S07", "WARD_S07",
        "WARD_S09", "WARD_S13", "WARD_S19", "WARD_S23", "WARE_S05", "WARE_S12",
        "WARE_S25", "WARE_S28", "WARE_S30", "WARF_S01", "WARF_S02", "WARF_S03",
        "WARF_S04", "WARF_S06", "WARF_S08", "WARF_S14", "WARF_S18", "WARF_S32",
        "WARG_S10", "WARG_S16", "WARG_S20", "WARG_S21", "WARG_S26", "WARG_S27",
        "WAUA_S01B", "WAUA_S02B", "WBLA_S01", "WBLA_S02", "WBLA_S03", "WDSR_S04",
        "WGWR_S01", "WGWR_S06", "WGWR_S08", "WHIR_S01", "WHIR_S02", "WHIR_S03",
        "WHIR_S04", "WHIR_S05", "WMPA_S01", "WMPA_S02", "WMPA_S03", "WMPA_S04",
        "WMPA_S05", "WORA_S01", "WORA_S02", "WORA_S03", "WORA_S04", "WORA_S05",
        "WORA_THRONES01", "WPGA_S01", "WPGA_S02", "WPGA_S03", "WPGA_S04", "WPTA_S01",
        "WPTA_S02", "WPTA_S03", "WRFA_S01", "WRFA_S02", "WRFA_S07", "WRFA_S08",
        "WRFB_S03", "WRFB_S04", "WRFB_S06", "WRRA_S01", "WRRA_S02", "WRRA_S03",
        "WRRA_S04", "WRRA_S05", "WRRA_S06", "WSKA_S07", "WSKA_S08", "WSKA_S09",
        "WSKA_S0N", "WSKB_S06", "WSKB_S0N", "WSKC_S01", "WSKC_S02", "WSKD_S03",
        "WSKD_S04", "WSKD_S05", "WSSR_S03", "WSUR_S04", "WSUR_S06", "WTDA_S02",
        "WTDA_S03", "WTDA_S04", "WTDB_S01", "WTDB_S02", "WTDB_S03", "WTDB_S04",
        "WVWA_S01", "WVWA_S02", "WVWA_S03", "WVWB_S01", "WVWB_S02",
    };

    private static readonly HashSet<string> Lookup = new(AllShelters, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string[]> ByRegion = AllShelters
        .GroupBy(RegionPrefix, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Every shelter this app knows, ordered by region and then by name.</summary>
    public static IReadOnlyList<string> All => AllShelters;

    public static bool IsKnown(string? roomName)
        => !string.IsNullOrWhiteSpace(roomName) && Lookup.Contains(roomName.Trim());

    /// <summary>The shelters of one region, or an empty list for a region with none.</summary>
    public static IReadOnlyList<string> ForRegion(string? regionCode)
    {
        if (string.IsNullOrWhiteSpace(regionCode))
        {
            return Array.Empty<string>();
        }

        return ByRegion.TryGetValue(regionCode.Trim(), out string[]? rooms) ? rooms : Array.Empty<string>();
    }

    /// <summary>
    /// The region a room name belongs to, taken from the part before the first underscore. This
    /// works on names the catalog has never seen, which is the point: a modded room still shows
    /// which region it is in.
    /// </summary>
    public static string? RegionOf(string? roomName)
    {
        if (string.IsNullOrWhiteSpace(roomName))
        {
            return null;
        }

        string trimmed = roomName.Trim();
        int underscore = trimmed.IndexOf('_');
        return underscore <= 0 ? null : trimmed[..underscore].ToUpperInvariant();
    }

    /// <summary>
    /// Shelters matching the query by room name, region code or region name, so typing
    /// "Outskirts" finds SU_S01. A blank query matches everything.
    /// </summary>
    public static IEnumerable<string> Search(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return AllShelters;
        }

        string trimmed = query.Trim();

        return AllShelters.Where(room =>
            room.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
            || RegionCatalog.ForCode(RegionPrefix(room)).DisplayName.Contains(trimmed, StringComparison.OrdinalIgnoreCase));
    }

    private static string RegionPrefix(string roomName)
    {
        int underscore = roomName.IndexOf('_');
        return underscore <= 0 ? roomName : roomName[..underscore];
    }
}
