// Usings sit above the namespace: RainWorldCompanion.Core.System would otherwise shadow System.
namespace RainWorldCompanion.Core.System;

/// <param name="Downpour">More Slugcats Expansion, which the game calls moreslugcats.</param>
/// <param name="CheckedTheInstall">
/// False when the game folder was not given or could not be read, so the two answers above are
/// "not known" rather than "not installed".
/// </param>
public sealed record ExpansionPresence(bool Downpour, bool Watcher, bool CheckedTheInstall)
{
    public static ExpansionPresence Unknown { get; } = new(false, false, false);
}

/// <summary>
/// Both expansions ship as folders under the game's own mods directory and neither appears in
/// StreamingAssets\enabledMods.txt, so the folder being there is the only signal.
/// </summary>
public static class ExpansionDetector
{
    public const string DownpourModId = "moreslugcats";

    public const string WatcherModId = "watcher";

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

    /// <summary>The folder alone would also count a leftover empty directory after an uninstall.</summary>
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
