// Usings sit above the namespace: RainWorldCompanion.Core.System would otherwise shadow System.
namespace RainWorldCompanion.Core.Mods;

// Rain Meadow keeps two lists beside the game's StreamingAssets and treats the lines it finds
// there as the authority: RainMeadowModManager.UpdateFromOrWriteToFile returns the file's own
// active lines, and only appends anything it worked out itself for the next run to read.
public sealed record MeadowModPolicy
{
    public const string HighImpactFileName = "meadow-highimpactmods.txt";

    public const string BannedFileName = "meadow-bannedmods.txt";

    public const string CommentPrefix = "//";

    public const string MeadowModId = "henpemaz_rainmeadow";

    private const string PolicyFolder = @"RainWorld_Data\StreamingAssets";

    public IReadOnlyList<string> HighImpact { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Banned { get; init; } = Array.Empty<string>();

    // False when the game folder held neither list, which happens before Rain Meadow has run once.
    // The match is still worth showing, it just cannot know which of your own mods the mod would
    // have turned off, so the game asks about those itself.
    public bool Read { get; init; }

    public static MeadowModPolicy Empty { get; } = new();

    public static MeadowModPolicy ReadFrom(string? gameInstallPath)
    {
        IReadOnlyList<string>? highImpact = ReadList(gameInstallPath, HighImpactFileName);
        IReadOnlyList<string>? banned = ReadList(gameInstallPath, BannedFileName);

        if (highImpact is null && banned is null)
        {
            return Empty;
        }

        return new MeadowModPolicy
        {
            HighImpact = highImpact ?? Array.Empty<string>(),
            Banned = banned ?? Array.Empty<string>(),
            Read = true,
        };
    }

    // What this machine would advertise as its required mods, which is also the set Rain Meadow
    // compares against a lobby's list to decide what of yours to turn off.
    public IReadOnlyList<string> RequiredFor(CurrentMods current)
    {
        ArgumentNullException.ThrowIfNull(current);

        var wanted = new HashSet<string>(HighImpact, StringComparer.OrdinalIgnoreCase);

        // A required mod drags in what it needs, the same walk Remix does when you tick one.
        foreach (ModEntry mod in current.Enabled.Mods)
        {
            if (wanted.Contains(mod.Id))
            {
                foreach (string need in ModRequirements.Closure(mod.Id, current.Installed))
                {
                    wanted.Add(need);
                }
            }
        }

        var required = new List<string>();
        foreach (ModEntry mod in InLoadOrder(current))
        {
            if (wanted.Contains(mod.Id) && !Holds(required, mod.Id))
            {
                required.Add(mod.Id);
            }
        }

        if (!Holds(required, MeadowModId))
        {
            required.Add(MeadowModId);
        }

        return required;
    }

    // What this machine would advertise as banned: everything either list names that is not on.
    public IReadOnlyList<string> BannedFor(CurrentMods current)
    {
        ArgumentNullException.ThrowIfNull(current);

        var on = new HashSet<string>(
            current.Enabled.Mods.Select(mod => mod.Id),
            StringComparer.OrdinalIgnoreCase);

        var banned = new List<string>();
        foreach (string id in HighImpact.Concat(Banned))
        {
            if (!on.Contains(id)
                && !string.Equals(id, MeadowModId, StringComparison.OrdinalIgnoreCase)
                && !Holds(banned, id))
            {
                banned.Add(id);
            }
        }

        return banned;
    }

    internal static IEnumerable<ModEntry> InLoadOrder(CurrentMods current) => current.Enabled.Mods
        .OrderBy(mod => mod.LoadOrder is null)
        .ThenBy(mod => mod.LoadOrder ?? 0);

    private static bool Holds(List<string> ids, string id) =>
        ids.Any(held => string.Equals(held, id, StringComparison.OrdinalIgnoreCase));

    // Null means the file was not there. A line commented out at its start is an exclusion the
    // player wrote, so it counts as absent rather than present.
    internal static IReadOnlyList<string>? ReadList(string? gameInstallPath, string fileName)
    {
        if (string.IsNullOrWhiteSpace(gameInstallPath))
        {
            return null;
        }

        string path;
        try
        {
            path = Path.Combine(gameInstallPath.Trim(), PolicyFolder, fileName);
        }
        catch (ArgumentException)
        {
            return null;
        }

        string[] lines;
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return ActiveLines(lines);
    }

    internal static IReadOnlyList<string> ActiveLines(IEnumerable<string> lines)
    {
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(CommentPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            // An id is followed by "// name" on the lines the mod writes itself.
            int comment = line.IndexOf(CommentPrefix, StringComparison.Ordinal);
            if (comment >= 0)
            {
                line = line.Substring(0, comment).Trim();
            }

            if (line.Length > 0 && seen.Add(line))
            {
                ids.Add(line);
            }
        }

        return ids;
    }
}
