using System.Text.Json;
using System.Text.Json.Nodes;
using RainWorldCompanion.Core.System;

namespace RainWorldCompanion.Core.Settings;

/// <summary>What a migration attempt did.</summary>
public enum MigrationOutcome
{
    /// <summary>No folder under the old name, so this is a fresh install or an already migrated one.</summary>
    NothingToMove,

    /// <summary>The folder under the new name was already there, so nothing was touched.</summary>
    AlreadyMigrated,

    /// <summary>The folder was renamed and the new name is now the one in use.</summary>
    Moved,

    /// <summary>The rename failed. The old folder is still there and still holds the data.</summary>
    Failed,
}

/// <summary>
/// Carries the settings folder over from the name this app used before it was renamed.
///
/// The folder holds the backups and the save library, which are the only copy of the saves in them,
/// and settings.json is what records where those two live. Leaving the folder behind under the old
/// name would work, but it would mean the one folder a user might go looking for is the one thing
/// still called something else.
///
/// Moving it is cheap and safe in a way that copying would not be. Both names sit directly under
/// %LOCALAPPDATA%, so the move is a rename within one volume: Windows does it by relinking the
/// directory entry, which takes the same time for eight hundred megabytes as for nothing at all
/// and cannot half-finish. A copy of the same data would be minutes of disk and a real chance of
/// stopping partway.
/// </summary>
public static class SettingsMigration
{
    /// <summary>The folder name used before the app was renamed to RainWorld Companion.</summary>
    public const string PreviousFolderName = "RainWorldSaveManager";

    /// <summary>The folder name used now.</summary>
    public const string FolderName = "RainWorldCompanion";

    /// <summary>%LOCALAPPDATA%\RainWorldSaveManager</summary>
    public static string PreviousRoot => Path.Combine(LocalAppData, PreviousFolderName);

    /// <summary>%LOCALAPPDATA%\RainWorldCompanion</summary>
    public static string Root => Path.Combine(LocalAppData, FolderName);

    private static string LocalAppData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>
    /// Renames the old settings folder to the new one, when there is one to rename and nothing
    /// already in the way. Call once at startup, before anything reads the settings.
    /// </summary>
    public static MigrationOutcome MoveFolder() => MoveFolder(PreviousRoot, Root);

    /// <param name="previousRoot">The folder to move.</param>
    /// <param name="root">Where it should end up.</param>
    public static MigrationOutcome MoveFolder(string previousRoot, string root)
    {
        try
        {
            // Something is already here. That is either a migration that has already run or a
            // fresh install that got there first, and in both cases this folder is the live one.
            // Merging the two is not attempted: which copy of a given backup is the real one is
            // not a question this can answer, and guessing wrong destroys a save.
            if (Directory.Exists(root))
            {
                return MigrationOutcome.AlreadyMigrated;
            }

            if (!Directory.Exists(previousRoot))
            {
                return MigrationOutcome.NothingToMove;
            }

            var parent = Path.GetDirectoryName(root);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            Directory.Move(previousRoot, root);
            return MigrationOutcome.Moved;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                     or ArgumentException or NotSupportedException)
        {
            // A file held open by something else, or a permission the user does not have. The old
            // folder is untouched and still holds everything, so the app keeps working from there
            // and can try again next launch. Failing to rename a folder is not a reason to refuse
            // to start.
            return MigrationOutcome.Failed;
        }
    }

    /// <summary>
    /// Rewrites the roots recorded in settings.json so the file agrees with where the folder
    /// actually is. Returns true when it changed something.
    ///
    /// <see cref="Repoint(string)"/> already corrects these every time the settings are read, so
    /// the app works without this. What it does not do is leave the file on disk telling the
    /// truth, and that matters for two reasons: anyone opening settings.json to work out where
    /// their backups went would be told the wrong folder, and the correction only holds while
    /// nothing exists at the old path. Create a folder under the old name a year from now and
    /// every load would go back to pointing at it.
    ///
    /// Edited as JSON rather than through <see cref="SettingsStore.Load"/> because Load fills in
    /// blank fields, and filling in a blank install path means reading the Steam registry key and
    /// probing every library folder it names. One of those can sit on an SMB timeout, and this
    /// runs on the way to showing a window.
    /// </summary>
    public static bool RepointSettingsFile(string settingsPath) =>
        RepointSettingsFile(settingsPath, PreviousRoot, Root);

    public static bool RepointSettingsFile(string settingsPath, string previousRoot, string root)
    {
        try
        {
            if (!File.Exists(settingsPath))
            {
                return false;
            }

            if (JsonNode.Parse(File.ReadAllText(settingsPath)) is not JsonObject settings)
            {
                return false;
            }

            // Both spellings, because this file has been written with camelCase and PascalCase
            // names over its life and the reader accepts either.
            var changed = RepointProperty(settings, "backupRootPath", previousRoot, root)
                | RepointProperty(settings, "libraryRootPath", previousRoot, root);

            if (!changed)
            {
                return false;
            }

            // Through a temp file and a move, the same way SettingsStore.Save writes, so an
            // interrupted rewrite cannot leave a half-written settings.json behind.
            var tempPath = settingsPath + ".tmp";
            File.WriteAllText(tempPath, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tempPath, settingsPath, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                     or JsonException or ArgumentException or NotSupportedException)
        {
            // Repoint still corrects this in memory on every load, so a failure here costs the
            // tidiness of the file and nothing the user can see.
            return false;
        }
    }

    private static bool RepointProperty(JsonObject settings, string name, string previousRoot, string root)
    {
        foreach (var property in settings)
        {
            if (!string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value?.GetValueKind() != JsonValueKind.String)
            {
                return false;
            }

            var stored = property.Value.GetValue<string>();
            var moved = Repoint(stored, previousRoot, root);
            if (string.Equals(stored, moved, StringComparison.Ordinal))
            {
                return false;
            }

            settings[property.Key] = moved;
            return true;
        }

        return false;
    }

    /// <summary>
    /// A stored path that used to sit under the old folder, pointed at where it is now.
    ///
    /// settings.json records the backup and library roots as absolute paths, so a folder renamed
    /// underneath them leaves both pointing at somewhere that no longer exists. To the person
    /// looking, an app that cannot find its backups is an app that lost them.
    /// </summary>
    public static string Repoint(string path) => Repoint(path, PreviousRoot, Root);

    public static string Repoint(string path, string previousRoot, string root)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        var trimmed = path.Trim();
        if (!IsAtOrInside(previousRoot, trimmed))
        {
            return path;
        }

        var moved = root + trimmed[previousRoot.Length..];

        // Only when the old location really has gone and the new one really is there. Both
        // existing means the user has two folders and the stored path is the one they chose, and
        // neither existing means this is a path that was already broken, which is not something to
        // fix by pointing it somewhere else that is also missing.
        if (Directory.Exists(trimmed) || !Directory.Exists(moved))
        {
            return path;
        }

        return moved;
    }

    /// <summary>The path is the folder itself, or something under it.</summary>
    private static bool IsAtOrInside(string container, string candidate)
    {
        if (candidate.Length == container.Length)
        {
            return string.Equals(candidate, container, StringComparison.OrdinalIgnoreCase);
        }

        // Textual rather than resolved through the filesystem, unlike the rest of the containment
        // checks in this project. By the time this runs the old path is gone, so there is nothing
        // left to ask Windows about, and the stored string is all there is to go on.
        return CanonicalPath.IsInsideResolved(container, candidate);
    }
}
