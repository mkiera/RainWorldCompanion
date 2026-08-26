// RainWorldCompanion.Core.System exists in this assembly, so a using written inside the namespace
// body would bind "System" to that namespace instead of the BCL root.
using System.Globalization;

namespace RainWorldCompanion.Core.Saves;

/// <summary>
/// Two rules from the game drive all of this. DeathPersistentSaveData.FromString ends with an
/// unconditional Custom.IntClamp(karma, 0, karmaCap), so the stored number is not necessarily the
/// one the game plays with. And HUD.KarmaMeter indexes sprites smallKarma0 through smallKarma9 with
/// the stored karma directly, so it is a 0-based index and the meter shows one more than it.
/// </summary>
public static class KarmaMath
{
    /// <summary>Shown by <see cref="FormatKarma"/> when the record carried no KARMA field.</summary>
    public const string UnknownKarmaText = "-";

    /// <summary>RWCustom.Custom.IntClamp, reproduced. Math.Clamp is not a substitute: it throws when
    /// inclMin is above inclMax, and this returns a bound instead, because save data is not trusted
    /// to keep the two in order.</summary>
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

    /// <summary>A null cap means no upper bound is known, so only the lower bound is applied. That
    /// keeps a stored -1, which the void sea ascension sequence writes, reading as karma level 1.</summary>
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

    /// <summary>Storage is 0-based, so a stored 7 reads as 8. int.MaxValue comes back as null,
    /// because adding 1 to it in this unchecked context wraps to a large negative.</summary>
    public static int? DisplayKarma(int? karma, int? karmaCap)
        => EffectiveKarma(karma, karmaCap) is { } effective && effective < int.MaxValue
            ? effective + 1
            : null;

    /// <summary>One above the 0-based stored cap. int.MaxValue comes back as null, for the reason
    /// given on <see cref="DisplayKarma"/>.</summary>
    public static int? DisplayKarmaCap(int? karmaCap)
        => karmaCap is { } cap && cap < int.MaxValue ? cap + 1 : null;

    /// <summary>True when the value on disk sits below 0 or above the cap.</summary>
    public static bool IsStoredOutOfRange(int? karma, int? karmaCap)
        => karma.HasValue && karma != EffectiveKarma(karma, karmaCap);

    /// <summary>"8 / 10", or "8" when the cap is unknown, or <see cref="UnknownKarmaText"/> when the
    /// karma is unknown.</summary>
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
