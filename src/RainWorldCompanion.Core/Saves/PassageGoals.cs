// RainWorldCompanion.Core.System exists in this assembly, so a using written inside the namespace
// body would bind "System" to that namespace instead of the BCL root.
using System.Globalization;

namespace RainWorldCompanion.Core.Saves;

/// <summary>Which of the game's five WinState.EndgameTracker subclasses wrote a passage's stored
/// progress. The subclass decides what the text means, and the passage name decides the subclass.</summary>
public enum PassageTracker
{
    /// <summary>A passage this app has no requirement for, including anything a mod added.</summary>
    Unknown,

    /// <summary>IntegerTracker: one number counted up to a fixed maximum.</summary>
    Count,

    /// <summary>ListTracker: dot separated item ids, needing a fixed number of them.</summary>
    Items,

    /// <summary>BoolArrayTracker: one dot terminated 0 or 1 per region or echo, all needed.</summary>
    Flags,

    /// <summary>GourFeastTracker: one dot terminated count per food type, each needing to be above 0.</summary>
    Feast,

    /// <summary>FloatTracker: a fraction that has to reach 1.</summary>
    Fraction,
}

/// <param name="Done">The stored number for <see cref="PassageTracker.Count"/>, the number of items
/// or set flags otherwise. Null when the text does not parse as the tracker's shape.</param>
/// <param name="Needed">The requirement, or null when this app does not know the passage.</param>
/// <param name="Fulfilled">Null rather than false when <see cref="Needed"/> is unknown, so a passage
/// from a mod is not reported as unearned on no evidence.</param>
/// <param name="Text">For example "5 / 5". Empty when the entry carried no tracker, and the raw
/// stored text when this app cannot interpret it.</param>
public sealed record PassageGoal(
    PassageTracker Tracker,
    int? Done,
    int? Needed,
    bool? Fulfilled,
    string Text);

/// <summary>
/// The requirements come from WinState.CreateAndAddTracker, which builds one tracker per passage
/// with the maximum baked in. Whether a passage is available in game is not stored: the game draws a
/// token when GoalFullfilled is true and consumed is false, and GoalFullfilled is this progress
/// against this requirement. Nothing here throws.
/// </summary>
public static class PassageGoals
{
    /// <summary>Separates the entries of a list, flag array or feast tracker.</summary>
    private const char EntrySeparator = '.';

    /// <summary>IntegerTracker maxima, read off WinState.CreateAndAddTracker.</summary>
    private static readonly Dictionary<string, int> CountGoals = new(StringComparer.Ordinal)
    {
        ["Survivor"] = 5,
        ["Outlaw"] = 7,
        ["Hunter"] = 12,
        ["Monk"] = 12,
        ["Saint"] = 12,
    };

    /// <summary>ListTracker item counts: Scholar 3 pearls, Nomad 4 regions, DragonSlayer 6 kills.</summary>
    private static readonly Dictionary<string, int> ItemGoals = new(StringComparer.Ordinal)
    {
        ["Scholar"] = 3,
        ["Nomad"] = 4,
        ["DragonSlayer"] = 6,
    };

    /// <summary>Every one of these is built with a maximum of 1, so the stored fraction is the
    /// progress itself.</summary>
    private static readonly HashSet<string> FractionPassages = new(StringComparer.Ordinal)
    {
        "Chieftain", "Friend", "Martyr", "Mother",
    };

    private const string TravellerPassage = "Traveller";
    private const string PilgrimPassage = "Pilgrim";
    private const string DragonSlayerPassage = "DragonSlayer";
    private const string GourmandPassage = "Gourmand";

    private const double FractionGoal = 1.0;

    /// <summary>An entry with no tracker text at all.</summary>
    public static readonly PassageGoal Nothing = new(PassageTracker.Unknown, null, null, null, "");

    /// <summary>Never throws. A null name or null progress reads the same as an empty one.</summary>
    public static PassageGoal Read(string? name, string? progress)
    {
        string stored = progress ?? "";
        PassageTracker tracker = TrackerFor(name ?? "", stored);

        if (stored.Length == 0)
        {
            return tracker == PassageTracker.Unknown
                ? Nothing
                : new PassageGoal(tracker, null, GoalFor(tracker, name ?? "", stored), null, "");
        }

        return tracker switch
        {
            PassageTracker.Count => ReadCount(name!, stored),
            PassageTracker.Items => ReadItems(name!, stored),
            PassageTracker.Flags => ReadFlags(stored),
            PassageTracker.Feast => ReadFeast(stored),
            PassageTracker.Fraction => ReadFraction(stored),
            _ => new PassageGoal(PassageTracker.Unknown, null, null, null, stored),
        };
    }

    /// <summary>The name settles it except for DragonSlayer, which is a ListTracker under More
    /// Slugcats or Watcher and a BoolArrayTracker without either. A flag array ends in a separator
    /// and a list does not, so the text tells those apart.</summary>
    private static PassageTracker TrackerFor(string name, string stored) => name switch
    {
        GourmandPassage => PassageTracker.Feast,
        TravellerPassage or PilgrimPassage => PassageTracker.Flags,
        DragonSlayerPassage => EndsWithSeparator(stored) ? PassageTracker.Flags : PassageTracker.Items,
        _ when CountGoals.ContainsKey(name) => PassageTracker.Count,
        _ when ItemGoals.ContainsKey(name) => PassageTracker.Items,
        _ when FractionPassages.Contains(name) => PassageTracker.Fraction,
        _ => PassageTracker.Unknown,
    };

    /// <summary>Flags and feasts size themselves from the text, so those have no requirement without
    /// it.</summary>
    private static int? GoalFor(PassageTracker tracker, string name, string stored) => tracker switch
    {
        PassageTracker.Count => CountGoals.TryGetValue(name, out int max) ? max : null,
        PassageTracker.Items => ItemGoals.TryGetValue(name, out int items) ? items : null,
        _ => null,
    };

    private static PassageGoal ReadCount(string name, string stored)
    {
        int needed = CountGoals[name];

        if (!TryParseInt(stored, out int done))
        {
            return new PassageGoal(PassageTracker.Count, null, needed, null, stored);
        }

        // WinState.DeathModifyTracker subtracts on death, so this number is often negative.
        return new PassageGoal(
            PassageTracker.Count,
            done,
            needed,
            done >= needed,
            Fraction(done, needed));
    }

    private static PassageGoal ReadItems(string name, string stored)
    {
        int needed = ItemGoals[name];
        int done = CountEntries(stored);

        return new PassageGoal(PassageTracker.Items, done, needed, done >= needed, Fraction(done, needed));
    }

    private static PassageGoal ReadFlags(string stored)
    {
        int total = 0;
        int set = 0;

        foreach (string entry in Entries(stored))
        {
            total++;
            if (entry == "1")
            {
                set++;
            }
        }

        // BoolArrayTracker.GoalFullfilled returns false on the first entry that is not set, so an
        // array with nothing in it is fulfilled.
        return new PassageGoal(PassageTracker.Flags, set, total, set == total, Fraction(set, total));
    }

    private static PassageGoal ReadFeast(string stored)
    {
        int total = 0;
        int eaten = 0;

        foreach (string entry in Entries(stored))
        {
            total++;
            if (TryParseInt(entry, out int count) && count > 0)
            {
                eaten++;
            }
        }

        // GourFeastTracker.GoalFullfilled needs every food type above 0, and is likewise fulfilled
        // by an empty array.
        return new PassageGoal(PassageTracker.Feast, eaten, total, eaten == total, Fraction(eaten, total));
    }

    private static PassageGoal ReadFraction(string stored)
    {
        if (!double.TryParse(stored, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            return new PassageGoal(PassageTracker.Fraction, null, null, null, stored);
        }

        // The stored text is kept rather than reformatted: rounding it would hide how close a run is
        // to a passage it has almost earned.
        return new PassageGoal(PassageTracker.Fraction, null, null, value >= FractionGoal, stored);
    }

    private static string Fraction(int done, int needed)
        => done.ToString(CultureInfo.InvariantCulture)
            + " / "
            + needed.ToString(CultureInfo.InvariantCulture);

    private static bool EndsWithSeparator(string stored)
        => stored.Length > 0 && stored[stored.Length - 1] == EntrySeparator;

    private static int CountEntries(string stored)
    {
        int count = 0;
        foreach (string _ in Entries(stored))
        {
            count++;
        }

        return count;
    }

    /// <summary>Empties are dropped: a flag array and a feast end in a separator, which would
    /// otherwise count as one more entry than the save holds.</summary>
    private static IEnumerable<string> Entries(string stored)
    {
        foreach (string entry in stored.Split(EntrySeparator))
        {
            if (entry.Length != 0)
            {
                yield return entry;
            }
        }
    }

    private static bool TryParseInt(string text, out int value)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
}
