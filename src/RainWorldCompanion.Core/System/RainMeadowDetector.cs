// Usings sit above the namespace declaration on purpose. RainWorldCompanion.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Text.Json;

namespace RainWorldCompanion.Core.System;

/// <summary>
/// Whether Rain Meadow is on this machine, and how we know.
/// </summary>
/// <param name="Present">
/// True when the mod is installed, or when the save folder carries something only Rain Meadow
/// writes. The window shows its online section on this and hides it entirely otherwise.
/// </param>
/// <param name="Enabled">
/// True only when the mod appears in the game's own enabled list. False does not mean absent: it
/// also covers an install we could not read because the game folder is unknown.
/// </param>
/// <param name="Version">Version from the mod's modinfo.json, when it was read.</param>
/// <param name="Reason">Plain sentence naming the evidence, for the tooltip and for support.</param>
public sealed record RainMeadowPresence(bool Present, bool Enabled, string? Version, string Reason)
{
    public static RainMeadowPresence Absent { get; } =
        new(false, false, null, "No Rain Meadow install and nothing it writes was found.");
}

/// <summary>
/// Works out whether Rain Meadow is installed.
///
/// The mod ships from the Steam workshop rather than the game's own mods folder, and the game
/// records what is turned on in StreamingAssets\enabledMods.txt as a list of workshop paths and
/// bare folder names. That file is the authority, but it lives in the game install, and this app
/// treats the game path as optional because it only ever needed it for slugcat portraits. So the
/// save folder is checked too: several files there are written by nothing but Rain Meadow, and
/// finding one is enough to know the player uses it.
/// </summary>
public static class RainMeadowDetector
{
    /// <summary>The mod id in its modinfo.json, and the name of its Remix config file.</summary>
    public const string ModId = "henpemaz_rainmeadow";

    /// <summary>The game's list of what is turned on, relative to the install root.</summary>
    private const string EnabledModsRelativePath = @"RainWorld_Data\StreamingAssets\enabledMods.txt";

    /// <summary>Local mods live here, and a bare line in the enabled list names one of them.</summary>
    private const string ModsRelativePath = @"RainWorld_Data\StreamingAssets\mods";

    /// <summary>A workshop line carries this prefix and then an absolute path.</summary>
    private const string WorkshopPrefix = "[WORKSHOP]";

    /// <summary>Files in the save folder that only Rain Meadow writes.</summary>
    private static readonly string[] SaveFolderEvidence =
    {
        "meadow.json",
        @"ModConfigs\" + ModId + ".txt",
        "online_sav",
        "online_sav2",
        "online_sav3",
    };

    /// <summary>
    /// Never throws. An unreadable game folder or a malformed modinfo.json costs the authoritative
    /// answer and falls back to the save folder evidence.
    /// </summary>
    public static RainMeadowPresence Detect(string? saveFolder, string? gameInstallPath)
    {
        RainMeadowPresence? fromGame = DetectFromGame(gameInstallPath);
        if (fromGame is not null && fromGame.Enabled)
        {
            return fromGame;
        }

        string? evidence = FindSaveFolderEvidence(saveFolder);
        if (evidence is not null)
        {
            // An install we found but could not confirm as enabled still contributes its version.
            return new RainMeadowPresence(
                true,
                fromGame?.Enabled ?? false,
                fromGame?.Version,
                "The save folder holds " + evidence + ", which only Rain Meadow writes.");
        }

        return fromGame ?? RainMeadowPresence.Absent;
    }

    /// <summary>
    /// Reads the game's enabled list. Null when the game folder is unknown or unreadable, which is
    /// different from a readable list that does not mention the mod.
    /// </summary>
    private static RainMeadowPresence? DetectFromGame(string? gameInstallPath)
    {
        if (string.IsNullOrWhiteSpace(gameInstallPath))
        {
            return null;
        }

        string enabledList;
        try
        {
            enabledList = Path.Combine(gameInstallPath, EnabledModsRelativePath);
            if (!File.Exists(enabledList))
            {
                return null;
            }
        }
        catch (ArgumentException)
        {
            return null;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(enabledList);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            string? modFolder = line.StartsWith(WorkshopPrefix, StringComparison.OrdinalIgnoreCase)
                ? line.Substring(WorkshopPrefix.Length).Trim()
                : SafeCombine(gameInstallPath, ModsRelativePath, line);

            if (modFolder is null)
            {
                continue;
            }

            string? version = ReadModVersionIfMeadow(modFolder);
            if (version is not null)
            {
                return new RainMeadowPresence(
                    true,
                    true,
                    version.Length == 0 ? null : version,
                    "Rain Meadow is turned on in the game's own mod list.");
            }
        }

        return new RainMeadowPresence(
            false,
            false,
            null,
            "The game's mod list does not include Rain Meadow.");
    }

    /// <summary>
    /// The version string when this folder is Rain Meadow, an empty string when it is Rain Meadow
    /// with no readable version, and null when it is some other mod.
    /// </summary>
    private static string? ReadModVersionIfMeadow(string modFolder)
    {
        string? infoPath = SafeCombine(modFolder, "modinfo.json");
        if (infoPath is null || !FileExistsSafe(infoPath))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(infoPath),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!document.RootElement.TryGetProperty("id", out JsonElement id)
                || id.ValueKind != JsonValueKind.String
                || !string.Equals(id.GetString(), ModId, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return document.RootElement.TryGetProperty("version", out JsonElement version)
                && version.ValueKind == JsonValueKind.String
                ? version.GetString() ?? ""
                : "";
        }
        catch (JsonException)
        {
            return null;
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

    /// <summary>The first Rain Meadow file found in the save folder, or null.</summary>
    private static string? FindSaveFolderEvidence(string? saveFolder)
    {
        if (string.IsNullOrWhiteSpace(saveFolder))
        {
            return null;
        }

        foreach (string relative in SaveFolderEvidence)
        {
            string? full = SafeCombine(saveFolder, relative);
            if (full is not null && FileExistsSafe(full))
            {
                return relative;
            }
        }

        return null;
    }

    private static string? SafeCombine(params string[] parts)
    {
        try
        {
            return Path.Combine(parts);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool FileExistsSafe(string path)
    {
        try
        {
            return File.Exists(path);
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
