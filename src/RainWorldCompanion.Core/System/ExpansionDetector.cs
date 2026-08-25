// Usings sit above the namespace declaration on purpose. RainWorldCompanion.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
namespace RainWorldCompanion.Core.System;

/// <summary>
/// Which of the game's two expansions are installed.
/// </summary>
/// <param name="Downpour">More Slugcats Expansion, which the game calls moreslugcats.</param>
/// <param name="Watcher">The Watcher.</param>
/// <param name="CheckedTheInstall">
/// False when the game folder was not given or could not be read, so the two answers above are
/// "not known" rather than "not installed". Worth keeping apart: advice built on a guess should
/// say less than advice built on a look.
/// </param>
public sealed record ExpansionPresence(bool Downpour, bool Watcher, bool CheckedTheInstall)
{
    /// <summary>What to use when the game folder is unknown.</summary>
    public static ExpansionPresence Unknown { get; } = new(false, false, false);
}

/// <summary>
/// Looks in the game folder for the expansions.
///
/// Both ship as folders under the game's own mods directory rather than through the workshop, and
/// neither appears in StreamingAssets\enabledMods.txt, which lists workshop mods and local ones but
/// not the expansions. So the folder being there is the signal: a creature from an expansion that
/// is not installed is one the game has never heard of.
///
/// The game path is optional throughout this app, so every answer here is allowed to be "did not
/// look". Nothing is refused on the strength of it.
/// </summary>
public static class ExpansionDetector
{
    /// <summary>The id in modinfo.json, and the folder name, for More Slugcats Expansion.</summary>
    public const string DownpourModId = "moreslugcats";

    /// <summary>The id in modinfo.json, and the folder name, for The Watcher.</summary>
    public const string WatcherModId = "watcher";

    /// <summary>Where the game keeps the expansions, relative to the install root.</summary>
    private const string ModsRelativePath = @"RainWorld_Data\StreamingAssets\mods";

    /// <summary>Never throws. An unreadable game folder costs the answer rather than the caller.</summary>
    public static ExpansionPresence Detect(string? gameInstallPath)
    {
        if (string.IsNullOrWhiteSpace(gameInstallPath))
        {
            return ExpansionPresence.Unknown;
        }

        string mods;
        try
        {
            mods = Path.Combine(gameInstallPath, ModsRelativePath);

            if (!Directory.Exists(mods))
            {
                return ExpansionPresence.Unknown;
            }
        }
        catch (ArgumentException)
        {
            return ExpansionPresence.Unknown;
        }

        return new ExpansionPresence(
            IsInstalled(mods, DownpourModId),
            IsInstalled(mods, WatcherModId),
            CheckedTheInstall: true);
    }

    /// <summary>
    /// An expansion counts as installed when its folder is there with its modinfo.json in it. The
    /// folder alone would also count a leftover empty directory, which happens after an uninstall.
    /// </summary>
    private static bool IsInstalled(string modsPath, string modId)
    {
        try
        {
            return File.Exists(Path.Combine(modsPath, modId, "modinfo.json"));
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
