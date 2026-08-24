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
    /// Where Rain World is installed, used only to read the slugcat portrait art out of the
    /// player's own copy of the game.
    ///
    /// Optional on purpose. A null, stale or plain wrong value costs the portraits and nothing
    /// else, so SettingsValidation does not check it: a bad install path must never block a
    /// backup or a restore. The UI falls back to an icon it draws itself.
    /// </summary>
    public string? GameInstallPath { get; set; }

    /// <summary>
    /// %LOCALAPPDATA%\RainWorldSaveManager\backups
    /// </summary>
    public static string DefaultBackupRootPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RainWorldSaveManager",
        "backups");

    /// <summary>
    /// Settings for a first run: the detected save directory, or the standard location when
    /// nothing is installed yet, and the default backup root.
    ///
    /// <see cref="GameInstallPath"/> is left null. Finding the install means reading the Steam
    /// registry value, parsing every libraryfolders.vdf and probing each library it names, and a
    /// library on a share whose machine is off makes that probe block for the full SMB timeout.
    /// <see cref="SettingsStore.Load"/> fills the field instead, and every caller runs Load on a
    /// worker. A null install costs the portraits and nothing else.
    /// </summary>
    public static AppSettings CreateDefault() => new()
    {
        SchemaVersion = 1,
        GameSavePath = SavePathResolver.FindSavePath() ?? SavePathResolver.DefaultSavePath,
        BackupRootPath = DefaultBackupRootPath,
    };
}
