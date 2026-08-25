using System.Text.RegularExpressions;

namespace RainWorldCompanion.Core.System;

/// <summary>
/// Locates the Rain World save directory and recognises whether a folder looks like one.
/// </summary>
public static class SavePathResolver
{
    // LocalLow has no Environment.SpecialFolder member, so the path is composed from the user
    // profile rather than looked up.
    public static string DefaultSavePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "AppData",
        "LocalLow",
        "Videocult",
        "Rain World");

    private static readonly string[] MarkerFileNames = { "sav", "sav2", "sav3", "options" };

    // exp1, exp3, expCore1..3 and friends. Anchored so "sav - Copy" style strays never match.
    private static readonly Regex ExpansionFilePattern = new(
        @"^exp(Core)?[0-9]+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Returns <see cref="DefaultSavePath"/> when it exists on disk, otherwise null.
    /// </summary>
    public static string? FindSavePath()
    {
        try
        {
            return Directory.Exists(DefaultSavePath) ? DefaultSavePath : null;
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

    /// <summary>
    /// True when the directory exists and holds at least one recognised save container file.
    /// The settings dialog uses this to warn about a folder that is not a save root.
    /// </summary>
    public static bool LooksLikeSaveRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            if (!Directory.Exists(path))
            {
                return false;
            }

            foreach (var filePath in Directory.EnumerateFiles(path))
            {
                var fileName = Path.GetFileName(filePath);

                foreach (var marker in MarkerFileNames)
                {
                    if (string.Equals(fileName, marker, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                if (ExpansionFilePattern.IsMatch(fileName))
                {
                    return true;
                }
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            // An invalid path never names a save root.
            return false;
        }

        return false;
    }
}
