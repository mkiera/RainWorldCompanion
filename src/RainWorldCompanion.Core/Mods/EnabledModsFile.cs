// RainWorldCompanion.Core.System exists in this assembly, so a using written inside the namespace
// body would bind "System" to that namespace instead of the BCL root.
namespace RainWorldCompanion.Core.Mods;

public static class EnabledModsFile
{
    public const string RelativePath = @"RainWorld_Data\StreamingAssets\enabledMods.txt";

    public const string WorkshopPrefix = "[WORKSHOP]";

    public static readonly IReadOnlySet<string> BuiltIn = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "devtools",
        "expedition",
        "jollycoop",
        "moreslugcats",
        "rwremix",
        "versioning",
        "watcher",
    };

    public static string? PathTo(string? gameInstallPath)
    {
        if (string.IsNullOrWhiteSpace(gameInstallPath))
        {
            return null;
        }

        try
        {
            return Path.Combine(gameInstallPath, RelativePath);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public static IReadOnlyList<string>? Read(string? gameInstallPath)
    {
        string? path = PathTo(gameInstallPath);
        if (path is null)
        {
            return null;
        }

        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return File.ReadAllLines(path).Where(line => line.Trim().Length > 0).ToList();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string? LineFor(ModEntry mod, string? workshopContentPath)
    {
        ArgumentNullException.ThrowIfNull(mod);

        if (IsLocal(mod))
        {
            string folder = mod.FolderName!;
            return BuiltIn.Contains(folder) || BuiltIn.Contains(mod.Id) ? null : folder;
        }

        if (mod.WorkshopId is not { Length: > 0 } workshopId || string.IsNullOrWhiteSpace(workshopContentPath))
        {
            return null;
        }

        try
        {
            // This machine's workshop folder, never a path a recording carried: that names some
            // other player's drive.
            return WorkshopPrefix + Path.Combine(workshopContentPath, workshopId);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    // A mod can sit in both folders, and CurrentModsReader keeps the local copy while still
    // carrying the workshop id for the Steam link. The game loads the local one, so that is the
    // line it wants.
    private static bool IsLocal(ModEntry mod)
        => mod.FolderName is { Length: > 0 }
            && !string.Equals(mod.Origin, ModEntry.WorkshopOrigin, StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<string> Rewrite(
        IReadOnlyList<string> existingLines,
        IReadOnlyList<ModEntry> turnOn,
        IReadOnlyList<ModEntry> turnOff,
        string? workshopContentPath)
    {
        ArgumentNullException.ThrowIfNull(existingLines);
        ArgumentNullException.ThrowIfNull(turnOn);
        ArgumentNullException.ThrowIfNull(turnOff);

        List<string> lines = existingLines
            .Where(line => !turnOff.Any(mod => Names(line, mod)))
            .ToList();

        foreach (ModEntry mod in turnOn)
        {
            if (LineFor(mod, workshopContentPath) is not { } line)
            {
                continue;
            }

            if (!lines.Any(existing => Names(existing, mod)))
            {
                // Appended rather than slotted into load order, because the game rewrites this file
                // from the options file's order the first time it starts.
                lines.Add(line);
            }
        }

        return lines;
    }

    public static bool Names(string line, ModEntry mod)
    {
        ArgumentNullException.ThrowIfNull(mod);

        string trimmed = (line ?? "").Trim();

        if (trimmed.StartsWith(WorkshopPrefix, StringComparison.OrdinalIgnoreCase))
        {
            if (mod.WorkshopId is not { Length: > 0 } workshopId)
            {
                return false;
            }

            string path = trimmed[WorkshopPrefix.Length..]
                .Replace('/', Path.DirectorySeparatorChar)
                .TrimEnd(Path.DirectorySeparatorChar);
            string folder = Path.GetFileName(path);

            return string.Equals(folder, workshopId, StringComparison.OrdinalIgnoreCase);
        }

        // A mod in both folders has a workshop id and still sits here under its folder name, so
        // this asks where the game loads it from rather than whether it has an id.
        return IsLocal(mod) && string.Equals(trimmed, mod.FolderName, StringComparison.OrdinalIgnoreCase);
    }
}
