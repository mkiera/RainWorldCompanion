using RainWorldSaveManager.Core.System;

namespace RainWorldSaveManager.Core.Settings;

/// <summary>
/// Checks that the configured folders can safely coexist.
///
/// The rule behind every message here is that no folder this app writes to may sit inside another
/// one. A store nested in the save folder would be swept into every backup and deleted by the first
/// restore, and a save folder nested in a store would be overwritten wholesale by one.
/// </summary>
public static class SettingsValidation
{
    /// <summary>
    /// The half of <see cref="Validate"/> that reads only the text: both paths are set and both
    /// are fully qualified. Returns null when the text is fine, which does not mean the pair is
    /// usable, only that the rest of the check is worth running.
    ///
    /// It is separate because it touches no disk. The rest of <see cref="Validate"/> resolves
    /// each path through the filesystem to catch junctions and 8.3 names, and that call blocks
    /// for the full network timeout on a UNC path whose host does not answer, so a caller that
    /// validates on every keystroke runs this part inline and the rest on a worker.
    /// </summary>
    public static string? ValidateText(string gameSavePath, string backupRootPath)
    {
        if (string.IsNullOrWhiteSpace(gameSavePath))
        {
            return "The game save folder is not set.";
        }

        if (string.IsNullOrWhiteSpace(backupRootPath))
        {
            return "The backup folder is not set.";
        }

        if (!IsFullPath(gameSavePath.Trim()))
        {
            return "The game save folder must be a full path, for example C:\\Users\\You\\AppData\\LocalLow\\Videocult\\Rain World.";
        }

        if (!IsFullPath(backupRootPath.Trim()))
        {
            return "The backup folder must be a full path, for example C:\\Users\\You\\AppData\\Local\\RainWorldSaveManager\\backups.";
        }

        return null;
    }

    /// <summary>
    /// Returns null when the pair is usable, otherwise a plain-English reason to show the user.
    ///
    /// Touches the filesystem. See <see cref="ValidateText"/> for the part that does not.
    /// </summary>
    public static string? Validate(string gameSavePath, string backupRootPath)
    {
        var text = ValidateText(gameSavePath, backupRootPath);
        if (text is not null)
        {
            return text;
        }

        var save = gameSavePath.Trim();
        var backup = backupRootPath.Trim();

        string normalisedSave;
        string normalisedBackup;
        try
        {
            normalisedSave = Normalise(save);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "The game save folder is not a valid path.";
        }

        try
        {
            normalisedBackup = Normalise(backup);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "The backup folder is not a valid path.";
        }

        if (string.Equals(normalisedSave, normalisedBackup, StringComparison.OrdinalIgnoreCase))
        {
            return "The backup folder cannot be the same folder as the game save folder.";
        }

        if (IsInside(normalisedBackup, normalisedSave))
        {
            return "The backup folder cannot be inside the game save folder, because backups would then back themselves up and a restore would overwrite them.";
        }

        if (IsInside(normalisedSave, normalisedBackup))
        {
            return "The game save folder cannot be inside the backup folder, because a restore would overwrite the whole backup store.";
        }

        return null;
    }

    /// <summary>
    /// The text half of the three-folder check. See <see cref="ValidateText(string, string)"/> for
    /// why the text and the disk halves are separate.
    /// </summary>
    public static string? ValidateText(string gameSavePath, string backupRootPath, string libraryRootPath)
    {
        var pair = ValidateText(gameSavePath, backupRootPath);
        if (pair is not null)
        {
            return pair;
        }

        if (string.IsNullOrWhiteSpace(libraryRootPath))
        {
            return "The library folder is not set.";
        }

        if (!IsFullPath(libraryRootPath.Trim()))
        {
            return "The library folder must be a full path, for example C:\\Users\\You\\AppData\\Local\\RainWorldSaveManager\\library.";
        }

        return null;
    }

    /// <summary>
    /// Returns null when all three folders can coexist, otherwise a plain-English reason.
    ///
    /// Touches the filesystem. See <see cref="ValidateText(string, string, string)"/> for the part
    /// that does not.
    /// </summary>
    public static string? Validate(string gameSavePath, string backupRootPath, string libraryRootPath)
    {
        var text = ValidateText(gameSavePath, backupRootPath, libraryRootPath);
        if (text is not null)
        {
            return text;
        }

        var pair = Validate(gameSavePath, backupRootPath);
        if (pair is not null)
        {
            return pair;
        }

        string normalisedLibrary;
        try
        {
            normalisedLibrary = Normalise(libraryRootPath.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "The library folder is not a valid path.";
        }

        var normalisedSave = Normalise(gameSavePath.Trim());
        var normalisedBackup = Normalise(backupRootPath.Trim());

        if (string.Equals(normalisedLibrary, normalisedSave, StringComparison.OrdinalIgnoreCase))
        {
            return "The library folder cannot be the same folder as the game save folder.";
        }

        if (string.Equals(normalisedLibrary, normalisedBackup, StringComparison.OrdinalIgnoreCase))
        {
            return "The library folder cannot be the same folder as the backup folder.";
        }

        if (IsInside(normalisedLibrary, normalisedSave))
        {
            return "The library folder cannot be inside the game save folder, because a restore would delete the saves kept in it.";
        }

        if (IsInside(normalisedSave, normalisedLibrary))
        {
            return "The game save folder cannot be inside the library folder, because storing a slot would then copy the library into itself.";
        }

        if (IsInside(normalisedLibrary, normalisedBackup))
        {
            return "The library folder cannot be inside the backup folder, because deleting a backup would take the saves kept in it.";
        }

        if (IsInside(normalisedBackup, normalisedLibrary))
        {
            return "The backup folder cannot be inside the library folder, because every backup would then be stored twice.";
        }

        return null;
    }

    private static bool IsFullPath(string path) => Path.IsPathFullyQualified(path);

    /// <summary>
    /// Reduces a path to the one name Windows knows the folder by.
    ///
    /// Comparing the text alone is not enough. A junction, a subst drive, an 8.3 short name and
    /// a \\?\ prefix are all second names for the same folder, and none of them shares a textual
    /// prefix with the first name, so a backup root aliased into the save folder would pass a
    /// string comparison and then be swept into every backup and deleted by the first restore.
    /// Trailing separators are dropped so C:\Foo\ and C:\Foo compare equal, and a folder that
    /// does not exist yet falls back to the textual form, which is the right answer for one that
    /// is about to be created.
    /// </summary>
    private static string Normalise(string path) => CanonicalPath.Resolve(path);

    // Separator-aware so C:\FooBar is not treated as living inside C:\Foo.
    private static bool IsInside(string candidate, string container)
        => CanonicalPath.IsInsideResolved(container, candidate);
}
