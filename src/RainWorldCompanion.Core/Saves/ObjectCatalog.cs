// Usings sit above the namespace declaration on purpose. RainWorldCompanion.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.

namespace RainWorldCompanion.Core.Saves;

/// <summary>One kind of item, and what a fresh one of it looks like in a save.</summary>
/// <param name="Name">The game's own spelling, which is what goes in the file.</param>
/// <param name="DisplayName">The same name with its words separated, for reading.</param>
/// <param name="Tail">
/// The fields the game's reader takes after the position, for a plain new one. Empty for a type
/// that carries nothing beyond the base. Null when this app cannot build one, which is different
/// from a type that needs nothing.
/// </param>
public sealed record ObjectKind(
    string Name,
    string DisplayName,
    CreatureSource Source,
    IReadOnlyList<string>? Tail)
{
    /// <summary>Whether this app can write one the game will read back as an item.</summary>
    public bool CanBuild => Tail is not null;
}

/// <summary>
/// The item names the game registers, and what each one needs written after its position.
///
/// Taken from SaveState.AbstractPhysicalObjectFromString rather than guessed. That method
/// special-cases twenty types, each reading its own fields at its own indices, then falls through
/// to a consumable case for another fifteen and a bare case for everything else. The whole method
/// sits inside a try, so a blob that is short a field is not reported: the object is dropped and
/// the rest of the save loads. That is why the tails are here rather than left to a general rule.
///
/// The defaults are what the game itself writes. An origin room and placed-object index of -1 mean
/// the item was not placed in a level, which is what a real save shows for a pearl carried around.
/// Field counts were checked against 89 item blobs in a played save.
///
/// The list suggests, it does not decide. A name from a mod is still a name a save can hold.
/// </summary>
public static class ObjectCatalog
{
    /// <summary>Nothing after the position, which is what a rock is.</summary>
    private static readonly string[] Bare = Array.Empty<string>();

    /// <summary>Origin room and placed-object index, both -1 for something never placed in a level.</summary>
    private static readonly string[] Consumable = { "-1", "-1" };

    /// <summary>
    /// A rifle carries its ammo style and then a count for each of the twelve kinds of ammo,
    /// separated among themselves by &lt;JRa&gt;.
    /// </summary>
    private static readonly string[] Rifle = { "Rock", string.Join("<JRa>", Enumerable.Repeat("0", 12)) };

    private static readonly ObjectKind[] AllObjects =
    {
        // ---- the base game ----
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

        // CollisionField is a field rather than a pickup, and its own Type has to be named at a
        // fixed index. Nothing here knows those names, so nothing here writes one.
        new("CollisionField", "Collision Field", CreatureSource.Vanilla, null),

        // ---- Downpour ----
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

        // ---- The Watcher ----
        new("FireSpriteLarva", "Fire Sprite Larva", CreatureSource.Watcher, Bare),
        new("Boomerang", "Boomerang", CreatureSource.Watcher, Bare),
        new("SpinToy", "Spin Toy", CreatureSource.Watcher, Bare),
        new("BallToy", "Ball Toy", CreatureSource.Watcher, Bare),
        new("SoftToy", "Soft Toy", CreatureSource.Watcher, Bare),
        new("WeirdToy", "Weird Toy", CreatureSource.Watcher, Bare),
        new("RippleJelly", "Ripple Jelly", CreatureSource.Watcher, Bare),

        // PrinceBulb reads its own fields through AbstractPrinceBulb.FromStrings rather than in the
        // method above, so what it needs is not pinned here and nothing writes one.
        new("PrinceBulb", "Prince Bulb", CreatureSource.Watcher, null),
    };

    private static readonly Dictionary<string, ObjectKind> ByName =
        AllObjects.ToDictionary(kind => kind.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Every item the game registers that something could carry, base game first.</summary>
    public static IReadOnlyList<ObjectKind> Known => AllObjects;

    public static bool IsKnown(string? name)
        => !string.IsNullOrWhiteSpace(name) && ByName.ContainsKey(name.Trim());

    /// <summary>
    /// The catalog entry for a name, or one built from the name itself when the catalog has never
    /// heard of it. An item a mod added is still an item the save holds, and its tail is unknown
    /// rather than empty.
    /// </summary>
    public static ObjectKind ForName(string? name)
    {
        string trimmed = (name ?? "").Trim();

        return ByName.TryGetValue(trimmed, out ObjectKind? known)
            ? known
            : new ObjectKind(trimmed, trimmed, CreatureSource.Vanilla, null);
    }

    /// <summary>Items matching what has been typed, names starting with it first.</summary>
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
