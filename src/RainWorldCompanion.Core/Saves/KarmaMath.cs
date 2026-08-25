// Usings sit above the namespace declaration on purpose. RainWorldCompanion.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Globalization;

namespace RainWorldCompanion.Core.Saves;

/// <summary>
/// Turns the raw KARMA and KARMACAP numbers on disk into the karma the game runs with and the
/// karma a player reads off the meter.
///
/// Two rules from the game drive all of this. DeathPersistentSaveData.FromString ends with an
/// unconditional Custom.IntClamp(karma, 0, karmaCap), so the stored number is not necessarily the
/// number the game plays with. And HUD.KarmaMeter indexes sprites smallKarma0 through smallKarma9
/// with the stored karma directly, so the stored number is a 0-based index and the meter shows one
/// more than it.
///
/// Every method takes and returns nullable ints so a record that never carried the field stays
/// null the whole way through instead of turning into a 0.
/// </summary>
public static class KarmaMath
{
    /// <summary>Shown by <see cref="FormatKarma"/> when the record carried no KARMA field.</summary>
    public const string UnknownKarmaText = "-";

    /// <summary>
    /// RWCustom.Custom.IntClamp, reproduced instruction for instruction:
    /// if (val &lt; inclMin) return inclMin; if (val &gt; inclMax) return inclMax; return val.
    ///
    /// Math.Clamp is not a substitute. It throws when inclMin is above inclMax, and this one
    /// returns a bound instead. Save data is not trusted to keep the two in order, and the game
    /// itself never throws here, so neither does this.
    /// </summary>
    public static int IntClamp(int val, int inclMin, int inclMax)
    {
        if (val < inclMin)
        {
            return inclMin;
        }

        if (val > inclMax)
        {
            return inclMax;
        }

        return val;
    }

    /// <summary>
    /// The karma the game ends up holding after it loads this record, which is the stored karma
    /// clamped to 0..cap by DeathPersistentSaveData.FromString.
    ///
    /// A null cap means no upper bound is known, so only the lower bound is applied. That keeps a
    /// stored -1, which the void sea ascension sequence writes, reading as karma level 1 rather
    /// than as a negative.
    /// </summary>
    public static int? EffectiveKarma(int? karma, int? karmaCap)
    {
        if (karma is not { } stored)
        {
            return null;
        }

        if (karmaCap is not { } cap)
        {
            return stored < 0 ? 0 : stored;
        }

        return IntClamp(stored, 0, cap);
    }

    /// <summary>
    /// The karma level a player sees on the meter. Storage is 0-based, so a stored 7 is the eighth
    /// level and reads as 8.
    ///
    /// int.MaxValue comes back as null. Nothing in the game writes it, but the reader takes any
    /// number that parses, and adding 1 to it in this unchecked context wraps to a large negative
    /// that would render as a karma level.
    /// </summary>
    public static int? DisplayKarma(int? karma, int? karmaCap)
        => EffectiveKarma(karma, karmaCap) is { } effective && effective < int.MaxValue
            ? effective + 1
            : null;

    /// <summary>
    /// The karma cap a player sees, one above the 0-based stored cap. int.MaxValue comes back as
    /// null, for the reason given on <see cref="DisplayKarma"/>.
    /// </summary>
    public static int? DisplayKarmaCap(int? karmaCap)
        => karmaCap is { } cap && cap < int.MaxValue ? cap + 1 : null;

    /// <summary>
    /// True when the stored karma is not the karma the game loads, which happens when the value on
    /// disk sits below 0 or above the cap.
    /// </summary>
    public static bool IsStoredOutOfRange(int? karma, int? karmaCap)
        => karma.HasValue && karma != EffectiveKarma(karma, karmaCap);

    /// <summary>
    /// Player-facing karma as "8 / 10", or just "8" when the cap is unknown, or
    /// <see cref="UnknownKarmaText"/> when the karma is unknown.
    /// </summary>
    public static string FormatKarma(int? karma, int? karmaCap)
    {
        if (DisplayKarma(karma, karmaCap) is not { } display)
        {
            return UnknownKarmaText;
        }

        string displayText = display.ToString(CultureInfo.InvariantCulture);

        if (DisplayKarmaCap(karmaCap) is not { } displayCap)
        {
            return displayText;
        }

        return displayText + " / " + displayCap.ToString(CultureInfo.InvariantCulture);
    }
}
