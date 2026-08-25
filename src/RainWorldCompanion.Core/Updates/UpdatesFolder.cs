using RainWorldCompanion.Core.Settings;
using RainWorldCompanion.Core.System;

namespace RainWorldCompanion.Core.Updates;

/// <summary>
/// Where downloaded installers are kept.
///
/// Under the settings folder rather than the temp directory, for two reasons. It survives the
/// cleaners that empty temp, which matters because the file has to still be there when Setup starts
/// reading it, and it is somewhere the uninstaller can name and remove, which temp is not.
/// </summary>
public static class UpdatesFolder
{
    /// <summary>
    /// %LOCALAPPDATA%\RainWorldCompanion\updates
    ///
    /// Called Location rather than Path deliberately. RainWorldCompanion.Core.System exists, so
    /// name lookup inside this assembly finds it before the BCL's System, and a member called Path
    /// would then shadow System.IO.Path for the whole class. CanonicalPath opens with a note about
    /// the same collision.
    /// </summary>
    public static string Location => Path.Combine(SettingsMigration.Root, "updates");

    /// <summary>Creates the folder if it is not there, and returns it.</summary>
    public static string Ensure()
    {
        var path = Location;
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Empties the folder. Call at startup.
    ///
    /// Startup is the next moment these files are provably finished with. The app hands an
    /// installer to Windows and then exits, so it can never delete the one it just ran: it is gone
    /// before Setup has finished with the file. Left alone, that is 43 MB kept forever per update.
    ///
    /// Best effort throughout. A file still locked, or a folder someone has open in Explorer, is a
    /// reason to leave it and try again next launch rather than to interrupt a launch.
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
    /// Whether a path really is inside this folder.
    ///
    /// The one guard standing between "a file this app downloaded" and "a file this app executes",
    /// so it resolves both sides through the filesystem rather than comparing strings. A junction
    /// or a subst drive is a second name for a folder, and a textual prefix test can be walked
    /// around by using one.
    /// </summary>
    public static bool Contains(string? candidate)
        => !string.IsNullOrWhiteSpace(candidate) && CanonicalPath.IsInside(Location, candidate);

    /// <summary>
    /// The full path to write a downloaded asset to, or null when the name is not one this app
    /// would have published. Only the last component of the name is ever used.
    /// </summary>
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
