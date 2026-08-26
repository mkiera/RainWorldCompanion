using RainWorldCompanion.Core.Settings;
using RainWorldCompanion.Core.System;

namespace RainWorldCompanion.Core.Updates;

/// <summary>
/// Under the settings folder rather than the temp directory: it survives the cleaners that empty
/// temp, and it is somewhere the uninstaller can name and remove.
/// </summary>
public static class UpdatesFolder
{
    /// <summary>
    /// Called Location rather than Path: RainWorldCompanion.Core.System exists, so a member called
    /// Path would shadow System.IO.Path for the whole class.
    /// </summary>
    public static string Location => Path.Combine(SettingsMigration.Root, "updates");

    public static string Ensure()
    {
        var path = Location;
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Call at startup. The app hands an installer to Windows and then exits, so it can never
    /// delete the one it just ran.
    /// </summary>
    public static void Clear()
    {
        try
        {
            var path = Location;
            if (!Directory.Exists(path))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(path))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }

            foreach (var directory in Directory.EnumerateDirectories(path))
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// The one guard between a file this app downloaded and a file this app executes, so it
    /// resolves both sides through the filesystem rather than comparing strings.
    /// </summary>
    public static bool Contains(string? candidate)
        => !string.IsNullOrWhiteSpace(candidate) && CanonicalPath.IsInside(Location, candidate);

    /// <summary>Null when the name is not one this app would have published.</summary>
    public static string? PathFor(string? assetName)
    {
        if (!UpdateUrls.IsSafeAssetName(assetName))
        {
            return null;
        }

        var fileName = Path.GetFileName(assetName!);
        return fileName.Length == 0 ? null : Path.Combine(Location, fileName);
    }
}
