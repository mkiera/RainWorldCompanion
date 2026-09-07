namespace RainWorldCompanion.Core.Saves;

public static class CampaignSpawnCatalog
{
    private static readonly Dictionary<string, string[]> Rooms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["White"] = ["SU_C04", "OE_SEXTRA"],
        ["Yellow"] = ["SU_C04", "OE_SEXTRA"],
        ["Red"] = ["LF_H01"],
        ["Gourmand"] = ["SH_GOR02", "OE_SEXTRA"],
        ["Artificer"] = ["GW_A24", "LC_FINAL"],
        ["Spear"] = ["GATE_OE_SU", "SI_A07"],
        ["Rivulet"] = ["DS_RIVSTART", "SL_AI"],
        ["Saint"] = ["SI_SAINTINTRO"],
        ["Inv"] = ["SH_E01"],
        ["Watcher"] = ["HI_W14", "WAUA_TOYS", "WORA_AI", "WRSA_WEAVER02"],
    };

    public static string? Find(string campaign, string room) => Rooms.TryGetValue(campaign, out var rooms)
        ? rooms.FirstOrDefault(r => string.Equals(r, room.Trim(), StringComparison.OrdinalIgnoreCase)) : null;
}
