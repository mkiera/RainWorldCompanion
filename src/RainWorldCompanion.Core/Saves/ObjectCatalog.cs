// RainWorldCompanion.Core.System exists in this assembly, so a using written inside the namespace
// body would bind "System" to that namespace instead of the BCL root.

namespace RainWorldCompanion.Core.Saves;

/// <param name="Name">The game's own spelling, which is what goes in the file.</param>
/// <param name="DisplayName">The same name with its words separated, for reading.</param>
/// <param name="Tail">The fields the game's reader takes after the position, for a plain new one.
/// Empty for a type that carries nothing beyond the base, null when this app cannot build one.</param>
public sealed record ObjectKind(
    string Name,
    string DisplayName,
    CreatureSource Source,
    IReadOnlyList<string>? Tail)
{
    public bool CanBuild => Tail is not null;
}

/// <summary>
/// Taken from SaveState.AbstractPhysicalObjectFromString rather than guessed: that method
/// special-cases twenty types at their own indices, and the whole of it sits inside a try, so a blob
/// short a field is dropped silently rather than reported. The list suggests, it does not decide.
/// </summary>
public static class ObjectCatalog
{
    /// <summary>Nothing after the position, which is what a rock is.</summary>
    private static readonly string[] Bare = Array.Empty<string>();

    /// <summary>Origin room and placed-object index, both -1 for something never placed in a level.</summary>
    private static readonly string[] Consumable = { "-1", "-1" };

    /// <summary>Ammo style, then a count for each of the twelve kinds of ammo, separated among
    /// themselves by &lt;JRa&gt;.</summary>
    private static readonly string[] Rifle = { "Rock", string.Join("<JRa>", Enumerable.Repeat("0", 12)) };

    private static readonly ObjectKind[] AllObjects =
    {
        new("Rock", "Rock", CreatureSource.Vanilla, Bare),
        new("Spear", "Spear", CreatureSource.Vanilla, new[] { "0", "0", "0", "0", "0", "0", "0", "0" }),
        new("FlareBomb", "Flare Bomb", CreatureSource.Vanilla, Consumable),
        new("VultureMask", "Vulture Mask", CreatureSource.Vanilla, new[] { "0", "0" }),
        new("PuffBall", "Puff Ball", CreatureSource.Vanilla, Consumable),
        new("GraffitiBomb", "Graffiti Bomb", CreatureSource.Vanilla, Consumable),
        new("DangleFruit", "Dangle Fruit", CreatureSource.Vanilla, new[] { "-1", "-1", "0" }),
        new("PebblesPearl", "Pebbles Pearl", CreatureSource.Vanilla, new[] { "-1", "-1", "PebblesPearl", "0", "1" }),
        new("SLOracleSwarmer", "Looks to the Moon Swarmer", CreatureSource.Vanilla, Bare),
        new("SSOracleSwarmer", "Five Pebbles Swarmer", CreatureSource.Vanilla, Bare),
        new("DataPearl", "Data Pearl", CreatureSource.Vanilla, new[] { "-1", "-1", "Misc" }),
        new("SeedCob", "Seed Cob", CreatureSource.Vanilla, Bare),
        new("WaterNut", "Water Nut", CreatureSource.Vanilla, new[] { "-1", "-1", "0" }),
        new("JellyFish", "Jellyfish", CreatureSource.Vanilla, Consumable),
        new("Lantern", "Scavenger Lantern", CreatureSource.Vanilla, Bare),
        new("KarmaFlower", "Karma Flower", CreatureSource.Vanilla, Consumable),
        new("Mushroom", "Mushroom", CreatureSource.Vanilla, Consumable),
        new("VoidSpawn", "Void Spawn", CreatureSource.Vanilla, Bare),
        new("FirecrackerPlant", "Firecracker Plant", CreatureSource.Vanilla, Consumable),
        new("SlimeMold", "Slime Mold", CreatureSource.Vanilla, Consumable),
        new("FlyLure", "Fly Lure", CreatureSource.Vanilla, Consumable),
        new("ScavengerBomb", "Scavenger Bomb", CreatureSource.Vanilla, Bare),
        new("SporePlant", "Spore Plant", CreatureSource.Vanilla, new[] { "-1", "-1", "0", "0" }),
        new("AttachedBee", "Attached Bee", CreatureSource.Vanilla, Bare),
        new("EggBugEgg", "Egg Bug Egg", CreatureSource.Vanilla, new[] { "0" }),
        new("NeedleEgg", "Needle Egg", CreatureSource.Vanilla, Consumable),
        new("DartMaggot", "Dart Maggot", CreatureSource.Vanilla, Bare),
        new("BubbleGrass", "Bubble Grass", CreatureSource.Vanilla, new[] { "-1", "-1", "1" }),
        new("NSHSwarmer", "No Significant Harassment Swarmer", CreatureSource.Vanilla, Bare),
        new("OverseerCarcass", "Overseer Carcass", CreatureSource.Vanilla, new[] { "0.5", "0.5", "0.5", "0" }),
        new("BlinkingFlower", "Blinking Flower", CreatureSource.Vanilla, Bare),
        new("Pomegranate", "Pomegranate", CreatureSource.Vanilla, new[] { "-1", "-1", "0", "0", "0" }),
        new("LobeTree", "Lobe Tree", CreatureSource.Vanilla, Bare),

        // CollisionField needs its own Type named at a fixed index, and nothing here knows those.
        new("CollisionField", "Collision Field", CreatureSource.Vanilla, null),

        new("SingularityBomb", "Singularity Bomb", CreatureSource.Downpour, Bare),
        new("Seed", "Seed", CreatureSource.Downpour, Consumable),
        new("GooieDuck", "Gooieduck", CreatureSource.Downpour, Consumable),
        new("LillyPuck", "Lillypuck", CreatureSource.Downpour, new[] { "-1", "-1", "3" }),
        new("GlowWeed", "Glow Weed", CreatureSource.Downpour, Consumable),
        new("DandelionPeach", "Dandelion Peach", CreatureSource.Downpour, Consumable),
        new("JokeRifle", "Joke Rifle", CreatureSource.Downpour, Rifle),
        new("Bullet", "Bullet", CreatureSource.Downpour, new[] { "Rock", "0" }),
        new("Spearmasterpearl", "Spearmaster Pearl", CreatureSource.Downpour, Consumable),
        new("FireEgg", "Fire Egg", CreatureSource.Downpour, new[] { "0", "0" }),
        new("EnergyCell", "Energy Cell", CreatureSource.Downpour, Bare),
        new("Germinator", "Germinator", CreatureSource.Downpour, Bare),
        new("MoonCloak", "Moon Cloak", CreatureSource.Downpour, Consumable),
        new("HalcyonPearl", "Halcyon Pearl", CreatureSource.Downpour, new[] { "-1", "-1", "Misc" }),
        new("HRGuard", "Hunter's Rot Guard", CreatureSource.Downpour, Consumable),

        new("FireSpriteLarva", "Fire Sprite Larva", CreatureSource.Watcher, Bare),
        new("Boomerang", "Boomerang", CreatureSource.Watcher, Bare),
        new("SpinToy", "Spin Toy", CreatureSource.Watcher, Bare),
        new("BallToy", "Ball Toy", CreatureSource.Watcher, Bare),
        new("SoftToy", "Soft Toy", CreatureSource.Watcher, Bare),
        new("WeirdToy", "Weird Toy", CreatureSource.Watcher, Bare),
        new("RippleJelly", "Ripple Jelly", CreatureSource.Watcher, Bare),

        // PrinceBulb reads its own fields through AbstractPrinceBulb.FromStrings, which is not pinned
        // here, so nothing writes one.
        new("PrinceBulb", "Prince Bulb", CreatureSource.Watcher, null),
    };

    private static readonly Dictionary<string, ObjectKind> ByName =
        AllObjects.ToDictionary(kind => kind.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Base game first.</summary>
    public static IReadOnlyList<ObjectKind> Known => AllObjects;

    public static bool IsKnown(string? name)
        => !string.IsNullOrWhiteSpace(name) && ByName.ContainsKey(name.Trim());

    /// <summary>An unknown name comes back as an entry built from the name itself, with its tail
    /// unknown rather than empty.</summary>
    public static ObjectKind ForName(string? name)
    {
        string trimmed = (name ?? "").Trim();

        return ByName.TryGetValue(trimmed, out ObjectKind? known)
            ? known
            : new ObjectKind(trimmed, trimmed, CreatureSource.Vanilla, null);
    }

    /// <summary>Names starting with the query first.</summary>
    public static IEnumerable<ObjectKind> Search(string? query)
    {
        string trimmed = (query ?? "").Trim();

        if (trimmed.Length == 0)
        {
            return AllObjects;
        }

        return AllObjects
            .Where(kind =>
                kind.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
                || kind.DisplayName.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(kind => kind.Name.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase))
            .ThenBy(kind => kind.Name, StringComparer.OrdinalIgnoreCase);
    }
}
