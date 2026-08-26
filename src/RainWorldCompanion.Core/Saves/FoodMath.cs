// RainWorldCompanion.Core.System exists in this assembly, so a using written inside the namespace
// body would bind "System" to that namespace instead of the BCL root.

namespace RainWorldCompanion.Core.Saves;

/// <param name="MaxPips">Pips the meter holds, the x of the returned IntVector2.</param>
/// <param name="PipsToHibernate">Pips a shelter costs, the y.</param>
public readonly record struct FoodMeter(int MaxPips, int PipsToHibernate);

/// <summary>
/// The game routinely writes a negative FOOD: SaveState.SessionEnded ends every cycle with
/// food = Custom.IntClamp(food, 0, maxFood) - foodToHibernate, so a cycle that banked fewer pips
/// than a shelter costs stores the difference. Nothing lifts it back up on read, and the two places
/// that use it afterwards each ignore anything below 1. Only whole pips are stored.
/// </summary>
public static class FoodMath
{
    /// <summary>What SlugcatStats.SlugcatFoodMeter hands a slugcat it does not recognise, which is
    /// also Survivor's and Watcher's.</summary>
    public static readonly FoodMeter DefaultMeter = new(7, 4);

    private static readonly Dictionary<string, FoodMeter> Meters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["White"] = new(7, 4),
        ["Yellow"] = new(5, 3),
        ["Red"] = new(9, 6),
        ["Rivulet"] = new(6, 5),
        ["Artificer"] = new(9, 6),
        ["Saint"] = new(5, 4),
        ["Spear"] = new(10, 5),
        ["Gourmand"] = new(11, 7),
        ["Slugpup"] = new(3, 2),
        // Sofanthiel registers its name as "Inv", and that is the id a save writes.
        ["Inv"] = new(12, 12),
        ["Watcher"] = new(7, 4),
    };

    /// <summary>SlugcatStats.SlugcatFoodMeter, reproduced. An unrecognised id gets
    /// <see cref="DefaultMeter"/>, which is what the game's own trailing case returns.</summary>
    public static FoodMeter MeterFor(string? slugcatId)
    {
        if (slugcatId is null)
        {
            return DefaultMeter;
        }

        return Meters.TryGetValue(slugcatId.Trim(), out var meter) ? meter : DefaultMeter;
    }

    /// <summary>The stored number, or 0 when it is below zero. No upper bound, because the game
    /// applies none: a hand-edited FOOD above the meter's capacity reaches the run whole.</summary>
    public static int? EffectiveFood(int? food)
    {
        if (food is not { } stored)
        {
            return null;
        }

        return stored < 0 ? 0 : stored;
    }

    /// <summary>Named for the one bound that exists rather than for karma's two: food has no cap at
    /// the top the way KarmaMath.IsStoredOutOfRange covers.</summary>
    public static bool IsStoredNegative(int? food) => food is { } stored && stored < 0;
}
