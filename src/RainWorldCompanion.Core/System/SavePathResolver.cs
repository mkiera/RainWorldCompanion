using System.Text.RegularExpressions;

namespace RainWorldCompanion.Core.System;

public static class SavePathResolver
{
    // LocalLow has no Environment.SpecialFolder member, so this is composed from the user profile.
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
            return false;
        }

        return false;
    }
}
