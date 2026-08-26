// RainWorldCompanion.Core.System exists in this assembly, so a using written inside the namespace
// body would bind "System" to that namespace instead of the BCL root.

namespace RainWorldCompanion.Core.Saves;

/// <summary>One karma gate: the name a save stores in UNLOCKEDGATES, and the regions it joins.</summary>
public sealed record GateInfo(string Name, string FromRegion, string ToRegion)
{
    /// <summary>The two regions by the names the game shows, for example "Outskirts to Industrial Complex".</summary>
    public string DisplayName
        => RegionCatalog.ForCode(FromRegion).DisplayName + " to " + RegionCatalog.ForCode(ToRegion).DisplayName;
}

/// <summary>
/// Extracted from the world files of Rain World 1.10 with Downpour and The Watcher, where a gate is
/// a room named GATE_&lt;from&gt;_&lt;to&gt;. The Watcher regions add none, since that campaign warps
/// rather than passing through gates. Like the other catalogs this suggests rather than decides.
/// </summary>
public static class GateCatalog
{
    private const string NamePrefix = "GATE_";

    private static readonly GateInfo[] KnownEntries =
    {
        new("GATE_CC_UW", "CC", "UW"),
        new("GATE_DM_SL", "DM", "SL"),
        new("GATE_DS_CC", "DS", "CC"),
        new("GATE_DS_GW", "DS", "GW"),
        new("GATE_DS_SB", "DS", "SB"),
        new("GATE_GW_SH", "GW", "SH"),
        new("GATE_GW_SL", "GW", "SL"),
        new("GATE_HI_CC", "HI", "CC"),
        new("GATE_HI_GW", "HI", "GW"),
        new("GATE_HI_SH", "HI", "SH"),
        new("GATE_HI_VS", "HI", "VS"),
        new("GATE_LF_SB", "LF", "SB"),
        new("GATE_LF_SU", "LF", "SU"),
        new("GATE_MS_SL", "MS", "SL"),
        new("GATE_OE_SU", "OE", "SU"),
        new("GATE_SB_OE", "SB", "OE"),
        new("GATE_SB_SL", "SB", "SL"),
        new("GATE_SB_VS", "SB", "VS"),
        new("GATE_SH_SL", "SH", "SL"),
        new("GATE_SH_UW", "SH", "UW"),
        new("GATE_SI_CC", "SI", "CC"),
        new("GATE_SI_LF", "SI", "LF"),
        new("GATE_SI_VS", "SI", "VS"),
        new("GATE_SL_CL", "SL", "CL"),
        new("GATE_SL_DM", "SL", "DM"),
        new("GATE_SL_MS", "SL", "MS"),
        new("GATE_SL_VS", "SL", "VS"),
        new("GATE_SS_UW", "SS", "UW"),
        new("GATE_SU_DS", "SU", "DS"),
        new("GATE_SU_HI", "SU", "HI"),
        new("GATE_UW_LC", "UW", "LC"),
        new("GATE_UW_SL", "UW", "SL"),
        new("GATE_UW_SS", "UW", "SS"),
    };

    private static readonly Dictionary<string, GateInfo> ByName =
        KnownEntries.ToDictionary(g => g.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Ordered by name.</summary>
    public static IReadOnlyList<GateInfo> Known => KnownEntries;

    public static bool IsKnown(string? name) => !string.IsNullOrWhiteSpace(name) && ByName.ContainsKey(name.Trim());

    /// <summary>Never returns null: an unknown name still has its two regions read off it when it is
    /// shaped like a gate name, so a modded gate reads as a gate rather than as nothing.</summary>
    public static GateInfo ForName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new GateInfo("", "", "");
        }

        string trimmed = name.Trim();
        if (ByName.TryGetValue(trimmed, out GateInfo? known))
        {
            return known;
        }

        if (trimmed.StartsWith(NamePrefix, StringComparison.OrdinalIgnoreCase))
        {
            string[] parts = trimmed[NamePrefix.Length..].Split('_');
            if (parts.Length == 2)
            {
                return new GateInfo(trimmed, parts[0], parts[1]);
            }
        }

        return new GateInfo(trimmed, "", "");
    }

    /// <summary>Matches by name or by either region name, so typing "Shoreline" finds the gates into
    /// and out of it. A blank query matches everything.</summary>
    public static IEnumerable<GateInfo> Search(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return KnownEntries;
        }

        string trimmed = query.Trim();

        return KnownEntries.Where(g =>
            g.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
            || g.DisplayName.Contains(trimmed, StringComparison.OrdinalIgnoreCase));
    }
}
