// RainWorldCompanion.Core.System exists in this assembly, so a using written inside the namespace
// body would bind "System" to that namespace instead of the BCL root.
namespace RainWorldCompanion.Core.Mods;

/// <summary>
/// The list the mod loader reads before the game starts, one path per line in load order. It is not
/// the same list as the options file: the options file names every mod by id, and this one names by
/// path and leaves the game's own folders out. On a live install the options file held 21 ids and
/// this file held 16 lines, the five absent ones being <see cref="BuiltIn"/> folders.
///
/// Both have to be written together. Writing only the options file leaves the loader pulling in the
/// previous run's plugins, which is the state a player would describe as the change not taking.
/// </summary>
public static class EnabledModsFile
{
    public const string RelativePath = @"RainWorld_Data\StreamingAssets\enabledMods.txt";

    /// <summary>Marks a line as an absolute path into the Steam workshop rather than a folder name.</summary>
    public const string WorkshopPrefix = "[WORKSHOP]";

    /// <summary>Folders the game ships inside its own mods folder. They turn on and off like any
    /// other mod, but the loader knows them by name and this file never carries a line for one.
    /// A third party mod living in the same folder, as Devourment does, still gets its line.</summary>
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

    /// <summary>Never throws. Null means the file could not be read at all, which is different from
    /// an empty list: an install with no mods on still has the file, holding nothing.</summary>
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

    /// <summary>The line the loader wants for one mod. Null when it should have none: a built-in
    /// folder, or a mod whose place on this machine is not known.</summary>
    public static string? LineFor(ModEntry mod, string? workshopContentPath)
    {
        ArgumentNullException.ThrowIfNull(mod);

        if (mod.WorkshopId is { Length: > 0 } workshopId)
        {
            if (string.IsNullOrWhiteSpace(workshopContentPath))
            {
                return null;
            }

            try
            {
                // Built from the workshop folder on this machine, never from a path a recording
                // carried, because that path names some other player's drive.
                return WorkshopPrefix + Path.Combine(workshopContentPath, workshopId);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        string? folder = mod.FolderName is { Length: > 0 } name ? name : null;
        if (folder is null || BuiltIn.Contains(folder) || BuiltIn.Contains(mod.Id))
        {
            return null;
        }

        return folder;
    }

    /// <summary>
    /// Drops the lines for mods being turned off, adds lines for mods being turned on, and leaves
    /// every other line where it was. Lines are edited rather than the file rebuilt, so a mod this
    /// app could not resolve keeps the line it already had instead of being silently switched off.
    /// </summary>
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

    /// <summary>Whether a stored line is this mod's. Matched on the workshop id or the folder name
    /// rather than on the whole path, which differs by drive, separator and case between machines.</summary>
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

        return mod.WorkshopId is null or ""
            && mod.FolderName is { Length: > 0 } name
            && string.Equals(trimmed, name, StringComparison.OrdinalIgnoreCase);
    }
}
