using System.Text.Json;
using System.Text.Json.Nodes;
using RainWorldCompanion.Core.System;

namespace RainWorldCompanion.Core.Settings;

public enum MigrationOutcome
{
    NothingToMove,

    AlreadyMigrated,

    Moved,

    Failed,
}

/// <summary>
/// Carries the settings folder over from the name this app used before it was renamed.
/// </summary>
public static class SettingsMigration
{
    public const string PreviousFolderName = "RainWorldSaveManager";

    public const string FolderName = "RainWorldCompanion";

    public static string PreviousRoot => Path.Combine(LocalAppData, PreviousFolderName);

    public static string Root => Path.Combine(LocalAppData, FolderName);

    private static string LocalAppData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>Call once at startup, before anything reads the settings.</summary>
    public static MigrationOutcome MoveFolder() => MoveFolder(PreviousRoot, Root);

    public static MigrationOutcome MoveFolder(string previousRoot, string root)
    {
        try
        {
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
            return MigrationOutcome.Failed;
        }
    }

    /// <summary>
    /// Rewrites the roots recorded in settings.json so the file agrees with where the folder
    /// actually is. Edited as JSON rather than through <see cref="SettingsStore.Load"/> because
    /// Load fills in a blank install path by probing every Steam library folder, which can sit on
    /// an SMB timeout, and this runs on the way to showing a window.
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

            // This file has been written with camelCase and PascalCase names over its life, and
            // the reader accepts either.
            var changed = RepointProperty(settings, "backupRootPath", previousRoot, root)
                | RepointProperty(settings, "libraryRootPath", previousRoot, root);

            if (!changed)
            {
                return false;
            }

            var tempPath = settingsPath + ".tmp";
            File.WriteAllText(tempPath, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tempPath, settingsPath, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                     or JsonException or ArgumentException or NotSupportedException)
        {
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

    /// <summary>A stored path that used to sit under the old folder, pointed at where it is now.</summary>
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

        // Both existing means the user has two folders and the stored path is the one they chose,
        // and neither existing means this is a path that was already broken.
        if (Directory.Exists(trimmed) || !Directory.Exists(moved))
        {
            return path;
        }

        return moved;
    }

    private static bool IsAtOrInside(string container, string candidate)
    {
        if (candidate.Length == container.Length)
        {
            return string.Equals(candidate, container, StringComparison.OrdinalIgnoreCase);
        }

        // By the time this runs the old path is gone, so there is nothing left to ask Windows
        // about, and the stored string is all there is to go on.
        return CanonicalPath.IsInsideResolved(container, candidate);
    }
}
