using RainWorldSaveManager.Core.System;

namespace RainWorldSaveManager.Core.Settings;

/// <summary>
/// The persisted application configuration.
/// </summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    public string GameSavePath { get; set; } = "";

    public string BackupRootPath { get; set; } = "";

    /// <summary>
    /// %LOCALAPPDATA%\RainWorldSaveManager\backups
    /// </summary>
    public static string DefaultBackupRootPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RainWorldSaveManager",
        "backups");

    /// <summary>
    /// Settings for a first run: the detected save directory, or the standard location when
    /// nothing is installed yet, plus the default backup root.
    /// </summary>
    public static AppSettings CreateDefault() => new()
    {
        SchemaVersion = 1,
        GameSavePath = SavePathResolver.FindSavePath() ?? SavePathResolver.DefaultSavePath,
        BackupRootPath = DefaultBackupRootPath,
    };
}
