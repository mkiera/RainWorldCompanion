// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.

namespace RainWorldSaveManager.Core.Saves;

/// <summary>
/// Hunter's cycle limit, and the two things the game derives from it that a save does not store.
///
/// RedsIllness.RedsCycles returns 19, or 24 when the campaign has the extra cycles. Two rules hang
/// off that number. HUD.Map.CycleLabel.UpdateCycleText and Menu.SlugcatSelectMenu.SlugcatPageContinue
/// both show Hunter the remaining cycles, RedsCycles minus the stored cycle number, where every
/// other slugcat is shown the stored number itself. And SaveState.LoadGame clears the redsDeath
/// flag when the stored cycle number is below RedsCycles, so the flag on disk is not the flag the
/// game plays with.
/// </summary>
public static class RedsIllness
{
    /// <summary>The slugcat id a Hunter campaign stores in "SAV STATE NUMBER".</summary>
    public const string HunterSlugcatId = "Red";

    /// <summary>Cycles Hunter has without the extra ones.</summary>
    public const int Cycles = 19;

    /// <summary>Cycles the REDEXTRACYCLES flag adds.</summary>
    public const int ExtraCycles = 5;

    /// <summary>RedsIllness.RedsCycles, reproduced: 19, or 24 with the extra cycles.</summary>
    public static int RedsCycles(bool redExtraCycles)
        => redExtraCycles ? Cycles + ExtraCycles : Cycles;

    /// <summary>True when this campaign is Hunter's, which is the only one these rules apply to.</summary>
    public static bool IsHunter(string? slugcatId)
        => string.Equals(slugcatId?.Trim(), HunterSlugcatId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The cycle number the game puts on screen: cycles remaining for Hunter, the stored number
    /// for everyone else. A campaign with no stored cycle stays null.
    /// </summary>
    public static int? DisplayCycle(string? slugcatId, int? cycleNum, bool redExtraCycles)
    {
        if (cycleNum is not { } stored)
        {
            return null;
        }

        return IsHunter(slugcatId) ? RedsCycles(redExtraCycles) - stored : stored;
    }

    /// <summary>
    /// The redsDeath flag as the game holds it after loading, which SaveState.LoadGame clears
    /// while the campaign is still inside the cycle limit.
    ///
    /// A record with no cycle number, which is every campaign in a schema 1 backup, has nothing to
    /// apply the rule to, so the stored flag stands.
    /// </summary>
    public static bool EffectiveRedsDeath(bool storedFlag, int? cycleNum, bool redExtraCycles)
    {
        if (!storedFlag || cycleNum is not { } cycle)
        {
            return storedFlag;
        }

        return cycle >= RedsCycles(redExtraCycles);
    }
}
