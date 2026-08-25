// Usings sit above the namespace declaration on purpose. RainWorldCompanion.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.

namespace RainWorldCompanion.Core.Saves;

/// <summary>
/// One region: the code a save stores, the name the game shows, and whether an echo lives there.
///
/// Named for the world rather than called RegionInfo, which is a type in System.Globalization that
/// any file formatting a number for a culture already has in scope.
/// </summary>
public sealed record WorldRegion(string Code, string DisplayName, bool HasEcho);

/// <summary>
/// The regions of the game and its expansions.
///
/// The list is baked in rather than read from the install, so the editor works with the game
/// uninstalled or on another machine. It was extracted from the world files of Rain World 1.10
/// with Downpour and The Watcher: a region is a folder under StreamingAssets\world holding a
/// world_&lt;code&gt;.txt, its name comes from displayname.txt beside it, and it has an echo when a
/// room in it places a GhostSpot.
///
/// Being baked in makes it a suggestion, never a rule. A region from a mod this list has never
/// heard of still has to be typeable, so every lookup answers rather than failing and every
/// picker built on it accepts text that matches nothing here.
/// </summary>
public static class RegionCatalog
{
    private static readonly WorldRegion[] KnownEntries =
    {
        new("CC", "Chimney Canopy", true),
        new("CL", "Silent Construct", true),
        new("DM", "Looks to the Moon", false),
        new("DS", "Drainage System", false),
        new("GW", "Garbage Wastes", false),
        new("HI", "Industrial Complex", false),
        new("HR", "Rubicon", false),
        new("LC", "Metropolis", true),
        new("LF", "Farm Arrays", true),
        new("LM", "Waterfront Facility", false),
        new("MS", "Submerged Superstructure", true),
        new("OE", "Outer Expanse", false),
        new("RM", "The Rot", false),
        new("SB", "Subterranean", true),
        new("SH", "Shaded Citadel", true),
        new("SI", "Sky Islands", true),
        new("SL", "Shoreline", true),
        new("SS", "Five Pebbles", false),
        new("SU", "Outskirts", false),
        new("UG", "Undergrowth", true),
        new("UW", "The Exterior", true),
        new("VS", "Pipeyard", false),
        new("WARA", "Shattered Terrace", false),
        new("WARB", "Salination", false),
        new("WARC", "Fetid Glen", false),
        new("WARD", "Cold Storage", false),
        new("WARE", "Heat Ducts", false),
        new("WARF", "Aether Ridge", false),
        new("WARG", "The Surface", false),
        new("WAUA", "Ancient Urban", false),
        new("WBLA", "Badlands", false),
        new("WDSR", "Decaying Tunnels", false),
        new("WGWR", "Infested Wastes", false),
        new("WHIR", "Corrupted Factories", false),
        new("WMPA", "Migration Path", false),
        new("WORA", "Outer Rim", false),
        new("WPGA", "Pillar Grove", false),
        new("WPTA", "Signal Spires", false),
        new("WRFA", "Coral Caves", false),
        new("WRFB", "Turbulent Pump", false),
        new("WRRA", "Rusted Wrecks", false),
        new("WRSA", "Daemon", false),
        new("WSKA", "Torrential Railways", false),
        new("WSKB", "Sunbaked Alley", false),
        new("WSKC", "Stormy Coast", false),
        new("WSKD", "Shrouded Stacks", false),
        new("WSSR", "Unfortunate Evolution", false),
        new("WSUR", "Crumbling Fringes", false),
        new("WTDA", "Torrid Desert", false),
        new("WTDB", "Desolate Tract", false),
        new("WVWA", "Verdant Waterways", false),
        new("WVWB", "Fractured Gateways", false),
    };

    private static readonly Dictionary<string, WorldRegion> ByCode =
        KnownEntries.ToDictionary(r => r.Code, StringComparer.OrdinalIgnoreCase);

    /// <summary>Every region this app knows, ordered by code.</summary>
    public static IReadOnlyList<WorldRegion> Known => KnownEntries;

    /// <summary>The regions an echo can be met in, which is what an echo editor lists.</summary>
    public static IReadOnlyList<WorldRegion> WithEchoes { get; } =
        KnownEntries.Where(r => r.HasEcho).ToArray();

    /// <summary>
    /// Looks up a region code, ignoring case. Never returns null: a code this list does not hold
    /// comes back with the raw code as its name, because a save from a modded region is still a
    /// save worth showing.
    /// </summary>
    public static WorldRegion ForCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return new WorldRegion("", "(unknown)", false);
        }

        string trimmed = code.Trim();
        return ByCode.TryGetValue(trimmed, out WorldRegion? known) ? known : new WorldRegion(trimmed, trimmed, false);
    }

    public static bool IsKnown(string? code) => !string.IsNullOrWhiteSpace(code) && ByCode.ContainsKey(code.Trim());

    /// <summary>Regions whose code or name contains the query. A blank query matches everything.</summary>
    public static IEnumerable<WorldRegion> Search(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return KnownEntries;
        }

        string trimmed = query.Trim();
        return KnownEntries.Where(r =>
            r.Code.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
            || r.DisplayName.Contains(trimmed, StringComparison.OrdinalIgnoreCase));
    }
}
