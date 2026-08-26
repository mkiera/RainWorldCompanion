// Usings sit above the namespace declaration on purpose. RainWorldCompanion.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.

namespace RainWorldCompanion.Core.Saves;

/// <summary>
/// The food meter one slugcat gets, as SlugcatStats.SlugcatFoodMeter returns it.
/// </summary>
/// <param name="MaxPips">Pips the meter holds, the x of the returned IntVector2.</param>
/// <param name="PipsToHibernate">Pips a shelter costs, the y.</param>
public readonly record struct FoodMeter(int MaxPips, int PipsToHibernate);

/// <summary>
/// Turns the raw FOOD number on disk into the food the game starts a run with.
///
/// The game routinely writes a negative FOOD. SaveState.SessionEnded ends every cycle with
/// food = Custom.IntClamp(food, 0, maxFood) - foodToHibernate, so a cycle that banked fewer pips
/// than a shelter costs stores the difference as a negative. A cycle that ended with no living
/// player banks 0 and stores exactly -foodToHibernate: -4 for Survivor, -6 for Hunter.
///
/// Nothing lifts that number back up when the save is read. SaveState.LoadGame parses FOOD
/// straight into SaveState.food, and the two places that use it afterwards each ignore anything
/// below 1. The RainWorldGame constructor only hands pips to the players while the stored number
/// is above zero, so a negative leaves PlayerState.foodInStomach at the 0 its constructor left,
/// and Menu.SlugcatSelectMenu.SlugcatPageContinue runs
/// Custom.IntClamp(food, 0, SlugcatFoodMeter(name).x) before drawing the meter on the save select
/// screen. Both answer 0 to every negative.
///
/// Only whole pips are stored. PlayerState carries quarterFoodPoints alongside foodInStomach, but
/// SaveState has no field for it and SaveState.SaveToString never writes one, so the quarter pips
/// a run has eaten are dropped at the shelter and every save starts a cycle on a pip boundary.
///
/// <see cref="EffectiveFood"/> takes and returns a nullable int so a record that never carried
/// FOOD stays null the whole way through instead of turning into a 0.
/// </summary>
public static class FoodMath
{
    /// <summary>
    /// The meter SlugcatStats.SlugcatFoodMeter hands a slugcat it does not recognise, which is
    /// also Survivor's and Watcher's.
    /// </summary>
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

    /// <summary>
    /// SlugcatStats.SlugcatFoodMeter, reproduced. An id from a mod this app has never heard of
    /// gets <see cref="DefaultMeter"/>, which is what the game's own trailing case returns.
    ///
    /// The game returns most of these only while the expansion that adds the slugcat is on, so a
    /// slugcat whose expansion is off falls to the default there too. No save this app reads
    /// reaches that branch: a campaign only exists in the file once its expansion has run.
    /// </summary>
    public static FoodMeter MeterFor(string? slugcatId)
    {
        if (slugcatId is null)
        {
            return DefaultMeter;
        }

        return Meters.TryGetValue(slugcatId.Trim(), out var meter) ? meter : DefaultMeter;
    }

    /// <summary>
    /// The pips the game gives a run that loads this record: the stored number, or 0 when that is
    /// below zero.
    ///
    /// There is no upper bound here, because the game applies none. A hand-edited FOOD above the
    /// meter's capacity reaches PlayerState.foodInStomach whole, and only the save select screen
    /// clamps it, so reporting the capacity instead would name a number the run does not have.
    /// </summary>
    public static int? EffectiveFood(int? food)
    {
        if (food is not { } stored)
        {
            return null;
        }

        return stored < 0 ? 0 : stored;
    }

    /// <summary>
    /// True when the stored food is not the food the run starts with, which for food means the
    /// number on disk is below zero.
    ///
    /// Named for the one bound that exists rather than for karma's two. KarmaMath.IsStoredOutOfRange
    /// covers a clamp with a cap at the top, and food has nothing there.
    /// </summary>
    public static bool IsStoredNegative(int? food) => food is { } stored && stored < 0;
}
