// RainWorldCompanion.Core.System exists in this assembly, so a using written inside the namespace
// body would bind "System" to that namespace instead of the BCL root.

namespace RainWorldCompanion.Core.Saves;

/// <param name="ColorHex">A "#RRGGBB" string.</param>
public sealed record SlugcatInfo(string Id, string DisplayName, string ColorHex);

/// <summary>The ids are the values a save writes in the "SAV STATE NUMBER" field. A save can carry
/// an id from a mod this catalog has never heard of, so lookup always answers.</summary>
public static class SlugcatCatalog
{
    /// <summary>Colour used for a slugcat id the catalog does not know.</summary>
    public const string NeutralColorHex = "#9E9E9E";

    /// <summary>Display name used when a record carries no slugcat id at all.</summary>
    public const string UnknownDisplayName = "(unknown)";

    private static readonly SlugcatInfo[] KnownEntries =
    {
        new("White", "Survivor", "#E5E0DC"),
        new("Yellow", "Monk", "#F2D74E"),
        new("Red", "Hunter", "#D14B4B"),
        new("Gourmand", "Gourmand", "#EBC99B"),
        new("Artificer", "Artificer", "#8C3B47"),
        new("Rivulet", "Rivulet", "#6FD3E8"),
        new("Spear", "Spearmaster", "#7A4B8C"),
        new("Saint", "Saint", "#8FD36B"),
        new("Inv", "Inv", "#4A5560"),
        new("Watcher", "Watcher", "#4C6E7A"),
    };

    private static readonly Dictionary<string, SlugcatInfo> ById = BuildIndex();

    /// <summary>The slugcats shipped with the game and its official expansions.</summary>
    public static IReadOnlyList<SlugcatInfo> Known => KnownEntries;

    /// <summary>Never returns null: an unknown id becomes an entry showing the raw id in neutral
    /// grey, and a blank one becomes the "(unknown)" entry.</summary>
    public static SlugcatInfo ForId(string? slugcatId)
    {
        if (string.IsNullOrWhiteSpace(slugcatId))
        {
            return new SlugcatInfo("", UnknownDisplayName, NeutralColorHex);
        }

        var id = slugcatId.Trim();
        return ById.TryGetValue(id, out var known)
            ? known
            : new SlugcatInfo(id, id, NeutralColorHex);
    }

    private static Dictionary<string, SlugcatInfo> BuildIndex()
    {
        var index = new Dictionary<string, SlugcatInfo>(
            KnownEntries.Length,
            StringComparer.OrdinalIgnoreCase);

        foreach (var entry in KnownEntries)
        {
            index[entry.Id] = entry;
        }

        return index;
    }
}
