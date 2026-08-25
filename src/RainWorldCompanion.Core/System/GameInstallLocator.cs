// Usings sit above the namespace declaration on purpose. RainWorldCompanion.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace RainWorldCompanion.Core.System;

/// <summary>
/// Finds the Rain World install and the slugcat portrait art inside it.
///
/// The install is optional to this app. It is read for the portrait PNGs and nothing else, so
/// every method here answers null or false rather than throwing: a missing, moved or locked
/// install costs an icon and must never stop a backup or a restore.
///
/// The PNGs belong to the game publisher. They are read from the player's own install at
/// runtime and are never copied into this repo or shipped with it.
/// </summary>
public static class GameInstallLocator
{
    /// <summary>Where the Steam client installs itself unless the user moved it.</summary>
    public const string SteamDefaultRoot = @"C:\Program Files (x86)\Steam";

    private const string SteamAppsFolder = "steamapps";
    private const string LibraryFile = "libraryfolders.vdf";
    private const string GameFolder = "Rain World";
    private const string DataFolder = "RainWorld_Data";
    private const string StreamingAssetsFolder = "StreamingAssets";
    private const string IllustrationsFolder = "illustrations";
    private const string ModsFolder = "mods";

    /// <summary>The install path a default Steam setup ends up with.</summary>
    public static string SteamDefaultInstallPath { get; } =
        Path.Combine(SteamDefaultRoot, SteamAppsFolder, "common", GameFolder);

    // A vdf is quoted key/value text. Only the "path" keys matter here, and their values escape
    // backslashes, so C:\Games arrives as "C:\\Games".
    private static readonly Regex LibraryPathPattern = new(
        @"""path""\s*""((?:\\.|[^""\\])*)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // multiplayerportrait<COLOUR><ALIVE>-<slugcat>.png. The second digit is the eyes: 1 is the
    // living face, 0 is the death portrait with crossed-out eyes, which this app never shows.
    private const string PortraitPrefix = "multiplayerportrait";

    // Colour digits: 0 white, 1 yellow, 2 pink, 3 blue, 4 the slugcat's own colour. Modded
    // slugcats ship 4; the three vanilla ones predate that digit and use their own tint instead.
    private static readonly string[] PortraitColourOrder = { "4", "0", "1", "2", "3" };

    private static readonly Dictionary<string, string> VanillaPortraitColour =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["white"] = "0",
            ["yellow"] = "1",
            ["red"] = "2",
        };

    /// <summary>
    /// The first Steam location that holds an install, or null when none does. Tries the default
    /// Steam folder, then every library named in libraryfolders.vdf, then the Steam folder
    /// recorded in the registry.
    /// </summary>
    public static string? FindInstallPath()
    {
        foreach (var candidate in CandidateInstallPaths())
        {
            if (LooksLikeInstall(candidate))
            {
                return Normalise(candidate);
            }
        }

        return null;
    }

    /// <summary>
    /// True when the folder exists and holds RainWorld_Data\StreamingAssets, which is where the
    /// portrait art lives.
    /// </summary>
    public static bool LooksLikeInstall(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var streamingAssets = Path.Combine(path.Trim(), DataFolder, StreamingAssetsFolder);
            return Directory.Exists(streamingAssets);
        }
        catch (Exception)
        {
            // An unusable path, a locked folder or a drive that went away all mean "not an
            // install" rather than an error worth surfacing.
            return false;
        }
    }

    /// <summary>
    /// The portrait PNG for a slugcat, or null when the install has none. Inv has no portrait at
    /// all, so null is its normal answer and the caller draws its own fallback.
    ///
    /// Files are named multiplayerportrait&lt;colour&gt;&lt;alive&gt;-&lt;lowercased id&gt;.png.
    /// Only the alive variants are considered. The base illustrations folder is searched first,
    /// then every mods\*\illustrations folder, so a slugcat that ships in an expansion is found
    /// without the expansion names being written down here.
    /// </summary>
    public static string? FindPortraitFile(string installPath, string slugcatId)
    {
        if (string.IsNullOrWhiteSpace(installPath) || string.IsNullOrWhiteSpace(slugcatId))
        {
            return null;
        }

        var id = slugcatId.Trim().ToLowerInvariant();
        var folders = IllustrationFolders(installPath.Trim());
        if (folders.Count == 0)
        {
            return null;
        }

        foreach (var colour in ColourOrderFor(id))
        {
            var fileName = PortraitPrefix + colour + "1-" + id + ".png";

            foreach (var folder in folders)
            {
                var candidate = Path.Combine(folder, fileName);
                if (FileExists(candidate))
                {
                    return candidate;
                }
            }
        }

        // Nothing in the known colour digits. Take any living portrait for this id, whatever
        // colour digit a mod chose.
        var anyColour = new Regex(
            "^" + Regex.Escape(PortraitPrefix) + @"\d1-" + Regex.Escape(id) + @"\.png$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        foreach (var folder in folders)
        {
            var files = EnumerateFiles(folder, PortraitPrefix + "*-" + id + ".png");
            files.Sort(StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
            {
                if (anyColour.IsMatch(Path.GetFileName(file)))
                {
                    return file;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The colour digits to try, most wanted first. The three vanilla slugcats lead with their
    /// own tint, everything else leads with digit 4.
    /// </summary>
    private static IEnumerable<string> ColourOrderFor(string lowercasedId)
    {
        if (VanillaPortraitColour.TryGetValue(lowercasedId, out var own))
        {
            yield return own;
        }

        foreach (var colour in PortraitColourOrder)
        {
            if (!string.Equals(colour, own, StringComparison.Ordinal))
            {
                yield return colour;
            }
        }
    }

    /// <summary>
    /// The base illustrations folder followed by every mods\*\illustrations folder, in a stable
    /// order. Only folders that exist are returned.
    /// </summary>
    private static List<string> IllustrationFolders(string installPath)
    {
        var folders = new List<string>();

        try
        {
            var streamingAssets = Path.Combine(installPath, DataFolder, StreamingAssetsFolder);

            var baseFolder = Path.Combine(streamingAssets, IllustrationsFolder);
            if (Directory.Exists(baseFolder))
            {
                folders.Add(baseFolder);
            }

            var mods = Path.Combine(streamingAssets, ModsFolder);
            if (!Directory.Exists(mods))
            {
                return folders;
            }

            var modFolders = Directory.EnumerateDirectories(mods).ToList();
            modFolders.Sort(StringComparer.OrdinalIgnoreCase);

            foreach (var mod in modFolders)
            {
                var modIllustrations = Path.Combine(mod, IllustrationsFolder);
                if (Directory.Exists(modIllustrations))
                {
                    folders.Add(modIllustrations);
                }
            }
        }
        catch (Exception)
        {
            // Whatever was collected before the failure is still worth searching.
        }

        return folders;
    }

    /// <summary>
    /// Steam locations to test, in the order the requirements set out: the default install, then
    /// the libraries listed in the default Steam folder's vdf, then the Steam folder from the
    /// registry, then that folder's own vdf.
    /// </summary>
    private static IEnumerable<string> CandidateInstallPaths()
    {
        yield return SteamDefaultInstallPath;

        foreach (var path in LibraryInstallPaths(SteamDefaultRoot))
        {
            yield return path;
        }

        var registryRoot = SteamRootFromRegistry();
        if (string.IsNullOrWhiteSpace(registryRoot))
        {
            yield break;
        }

        yield return Path.Combine(registryRoot, SteamAppsFolder, "common", GameFolder);

        foreach (var path in LibraryInstallPaths(registryRoot))
        {
            yield return path;
        }
    }

    /// <summary>
    /// Every library named in a Steam folder's libraryfolders.vdf, each turned into the path the
    /// game would sit at inside it.
    /// </summary>
    private static List<string> LibraryInstallPaths(string steamRoot)
    {
        var paths = new List<string>();

        string text;
        try
        {
            var vdfPath = Path.Combine(steamRoot, SteamAppsFolder, LibraryFile);
            if (!File.Exists(vdfPath))
            {
                return paths;
            }

            text = File.ReadAllText(vdfPath);
        }
        catch (Exception)
        {
            return paths;
        }

        foreach (Match match in LibraryPathPattern.Matches(text))
        {
            var library = UnescapeVdf(match.Groups[1].Value);
            if (string.IsNullOrWhiteSpace(library))
            {
                continue;
            }

            try
            {
                paths.Add(Path.Combine(library, SteamAppsFolder, "common", GameFolder));
            }
            catch (ArgumentException)
            {
                // A value with characters no path can hold is skipped.
            }
        }

        return paths;
    }

    /// <summary>
    /// HKCU\Software\Valve\Steam\SteamPath, which the client writes on install. Null off Windows
    /// or when the value is missing.
    /// </summary>
    private static string? SteamRootFromRegistry()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var value = key?.GetValue("SteamPath") as string;
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Undoes the vdf's backslash escaping. Steam writes a path as "C:\\Games", and \" \n \t
    /// occur too.
    ///
    /// A backslash followed by anything else is left alone rather than swallowed, so a file some
    /// other tool wrote with single backslashes still yields a usable path instead of C:Games.
    /// </summary>
    private static string UnescapeVdf(string value)
    {
        if (!value.Contains('\\'))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);

        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 >= value.Length)
            {
                builder.Append(value[i]);
                continue;
            }

            switch (value[i + 1])
            {
                case '\\':
                case '"':
                    builder.Append(value[i + 1]);
                    i++;
                    break;
                case 'n':
                    builder.Append('\n');
                    i++;
                    break;
                case 't':
                    builder.Append('\t');
                    i++;
                    break;
                default:
                    builder.Append(value[i]);
                    break;
            }
        }

        return builder.ToString();
    }

    private static bool FileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static List<string> EnumerateFiles(string folder, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(folder, pattern).ToList();
        }
        catch (Exception)
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// Tidies a found path into one form, so a Steam root written with forward slashes does not
    /// end up stored in the settings file that way.
    /// </summary>
    private static string Normalise(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        }
        catch (Exception)
        {
            return path;
        }
    }
}
