// RainWorldCompanion.Core.System exists in this assembly, so a using written inside the namespace
// body would bind "System" to that namespace instead of the BCL root.

namespace RainWorldCompanion.Core.Saves;

public enum CreatureSource
{
    Vanilla,
    Downpour,
    Watcher,
}

/// <param name="Name">The game's own spelling, which is what goes in the file.</param>
/// <param name="DisplayName">The same name with its words separated, for reading.</param>
public sealed record CreatureKind(string Name, string DisplayName, CreatureSource Source);

/// <summary>
/// The creature names the game registers, taken off the game assembly rather than written from
/// memory. StandardGroundCreature and LizardTemplate are left out: both are bases the game builds
/// other templates from rather than creatures anything spawns. The list suggests, it does not
/// decide, so nothing here rejects a name for being absent.
/// </summary>
public static class CreatureCatalog
{
    private static readonly CreatureKind[] AllCreatures =
    {
        new("Slugcat", "Slugcat", CreatureSource.Vanilla),
        new("PinkLizard", "Pink Lizard", CreatureSource.Vanilla),
        new("GreenLizard", "Green Lizard", CreatureSource.Vanilla),
        new("BlueLizard", "Blue Lizard", CreatureSource.Vanilla),
        new("YellowLizard", "Yellow Lizard", CreatureSource.Vanilla),
        new("WhiteLizard", "White Lizard", CreatureSource.Vanilla),
        new("RedLizard", "Red Lizard", CreatureSource.Vanilla),
        new("BlackLizard", "Black Lizard", CreatureSource.Vanilla),
        new("Salamander", "Salamander", CreatureSource.Vanilla),
        new("CyanLizard", "Cyan Lizard", CreatureSource.Vanilla),
        new("Fly", "Fly", CreatureSource.Vanilla),
        new("Leech", "Leech", CreatureSource.Vanilla),
        new("SeaLeech", "Sea Leech", CreatureSource.Vanilla),
        new("Snail", "Snail", CreatureSource.Vanilla),
        new("Vulture", "Vulture", CreatureSource.Vanilla),
        new("GarbageWorm", "Garbage Worm", CreatureSource.Vanilla),
        new("LanternMouse", "Lantern Mouse", CreatureSource.Vanilla),
        new("CicadaA", "Cicada A", CreatureSource.Vanilla),
        new("CicadaB", "Cicada B", CreatureSource.Vanilla),
        new("Spider", "Spider", CreatureSource.Vanilla),
        new("JetFish", "Jet Fish", CreatureSource.Vanilla),
        new("BigEel", "Big Eel", CreatureSource.Vanilla),
        new("Deer", "Deer", CreatureSource.Vanilla),
        new("TubeWorm", "Tube Worm", CreatureSource.Vanilla),
        new("DaddyLongLegs", "Daddy Long Legs", CreatureSource.Vanilla),
        new("BrotherLongLegs", "Brother Long Legs", CreatureSource.Vanilla),
        new("TentaclePlant", "Tentacle Plant", CreatureSource.Vanilla),
        new("PoleMimic", "Pole Mimic", CreatureSource.Vanilla),
        new("MirosBird", "Miros Bird", CreatureSource.Vanilla),
        new("TempleGuard", "Temple Guard", CreatureSource.Vanilla),
        new("Centipede", "Centipede", CreatureSource.Vanilla),
        new("RedCentipede", "Red Centipede", CreatureSource.Vanilla),
        new("Centiwing", "Centiwing", CreatureSource.Vanilla),
        new("SmallCentipede", "Small Centipede", CreatureSource.Vanilla),
        new("Scavenger", "Scavenger", CreatureSource.Vanilla),
        new("Overseer", "Overseer", CreatureSource.Vanilla),
        new("VultureGrub", "Vulture Grub", CreatureSource.Vanilla),
        new("EggBug", "Egg Bug", CreatureSource.Vanilla),
        new("BigSpider", "Big Spider", CreatureSource.Vanilla),
        new("SpitterSpider", "Spitter Spider", CreatureSource.Vanilla),
        new("SmallNeedleWorm", "Small Needle Worm", CreatureSource.Vanilla),
        new("BigNeedleWorm", "Big Needle Worm", CreatureSource.Vanilla),
        new("DropBug", "Drop Bug", CreatureSource.Vanilla),
        new("KingVulture", "King Vulture", CreatureSource.Vanilla),
        new("Hazer", "Hazer", CreatureSource.Vanilla),
        new("MirosVulture", "Miros Vulture", CreatureSource.Downpour),
        new("SpitLizard", "Spit Lizard", CreatureSource.Downpour),
        new("EelLizard", "Eel Lizard", CreatureSource.Downpour),
        new("MotherSpider", "Mother Spider", CreatureSource.Downpour),
        new("TerrorLongLegs", "Terror Long Legs", CreatureSource.Downpour),
        new("AquaCenti", "Aqua Centi", CreatureSource.Downpour),
        new("StowawayBug", "Stowaway Bug", CreatureSource.Downpour),
        new("ScavengerElite", "Scavenger Elite", CreatureSource.Downpour),
        new("Inspector", "Inspector", CreatureSource.Downpour),
        new("Yeek", "Yeek", CreatureSource.Downpour),
        new("BigJelly", "Big Jelly", CreatureSource.Downpour),
        new("JungleLeech", "Jungle Leech", CreatureSource.Downpour),
        new("ZoopLizard", "Zoop Lizard", CreatureSource.Downpour),
        new("HunterDaddy", "Hunter Daddy", CreatureSource.Downpour),
        new("FireBug", "Fire Bug", CreatureSource.Downpour),
        new("SlugNPC", "Slug NPC", CreatureSource.Downpour),
        new("ScavengerKing", "Scavenger King", CreatureSource.Downpour),
        new("TrainLizard", "Train Lizard", CreatureSource.Downpour),
        new("DrillCrab", "Drill Crab", CreatureSource.Watcher),
        new("TowerCrab", "Tower Crab", CreatureSource.Watcher),
        new("Barnacle", "Barnacle", CreatureSource.Watcher),
        new("SandGrub", "Sand Grub", CreatureSource.Watcher),
        new("BigSandGrub", "Big Sand Grub", CreatureSource.Watcher),
        new("BigMoth", "Big Moth", CreatureSource.Watcher),
        new("SmallMoth", "Small Moth", CreatureSource.Watcher),
        new("BoxWorm", "Box Worm", CreatureSource.Watcher),
        new("FireSprite", "Fire Sprite", CreatureSource.Watcher),
        new("Rattler", "Rattler", CreatureSource.Watcher),
        new("SkyWhale", "Sky Whale", CreatureSource.Watcher),
        new("ScavengerTemplar", "Scavenger Templar", CreatureSource.Watcher),
        new("ScavengerDisciple", "Scavenger Disciple", CreatureSource.Watcher),
        new("Loach", "Loach", CreatureSource.Watcher),
        new("RotLoach", "Rot Loach", CreatureSource.Watcher),
        new("BlizzardLizard", "Blizzard Lizard", CreatureSource.Watcher),
        new("BasiliskLizard", "Basilisk Lizard", CreatureSource.Watcher),
        new("IndigoLizard", "Indigo Lizard", CreatureSource.Watcher),
        new("PeachLizard", "Peach Lizard", CreatureSource.Watcher),
        new("Rat", "Rat", CreatureSource.Watcher),
        new("Frog", "Frog", CreatureSource.Watcher),
        new("Tardigrade", "Tardigrade", CreatureSource.Watcher),
        new("GrappleSnake", "Grapple Snake", CreatureSource.Watcher),
        new("Millipede", "Millipede", CreatureSource.Watcher),
        new("Angler", "Angler", CreatureSource.Watcher),
        new("RippleSpider", "Ripple Spider", CreatureSource.Watcher),
        new("MothGrub", "Moth Grub", CreatureSource.Watcher),    };

    private static readonly Dictionary<string, CreatureKind> ByName =
        AllCreatures.ToDictionary(creature => creature.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Base game first.</summary>
    public static IReadOnlyList<CreatureKind> Known => AllCreatures;

    public static bool IsKnown(string? name)
        => !string.IsNullOrWhiteSpace(name) && ByName.ContainsKey(name.Trim());

    /// <summary>An unknown name comes back as an entry built from the name itself.</summary>
    public static CreatureKind ForName(string? name)
    {
        string trimmed = (name ?? "").Trim();

        return ByName.TryGetValue(trimmed, out CreatureKind? known)
            ? known
            : new CreatureKind(trimmed, trimmed, CreatureSource.Vanilla);
    }

    /// <summary>Names starting with the query first. An empty query offers the whole list.</summary>
    public static IEnumerable<CreatureKind> Search(string? query)
    {
        string trimmed = (query ?? "").Trim();

        if (trimmed.Length == 0)
        {
            return AllCreatures;
        }

        return AllCreatures
            .Where(creature =>
                creature.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
                || creature.DisplayName.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(creature => creature.Name.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase))
            .ThenBy(creature => creature.Name, StringComparer.OrdinalIgnoreCase);
    }
}
