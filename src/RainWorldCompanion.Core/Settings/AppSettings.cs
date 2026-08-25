using RainWorldCompanion.Core.System;

namespace RainWorldCompanion.Core.Settings;

/// <summary>
/// The persisted application configuration.
/// </summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    public string GameSavePath { get; set; } = "";

    public string BackupRootPath { get; set; } = "";

    /// <summary>
    /// Where the named save library lives.
    ///
    /// Blank in a settings file written before the library existed. SettingsStore.Load fills a
    /// blank with <see cref="DefaultLibraryRootPath"/>, which is why the schema version did not
    /// have to move for this field.
    /// </summary>
    public string LibraryRootPath { get; set; } = "";

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
    /// Which releases the app is willing to show: "stable", "prerelease" or "alpha".
    ///
    /// Text rather than the enum, because System.Text.Json writes an enum as its ordinal, and a
    /// number would silently mean a different channel the moment one is inserted between two
    /// existing ones. Anything unrecognised reads as stable, which is also the right landing place
    /// for a file written by a later version naming a channel this one has never heard of.
    ///
    /// Additive, like <see cref="LibraryRootPath"/>, so the schema version does not move: a file
    /// written before this existed has no such property, and the initialiser here is what it gets.
    /// </summary>
    public string UpdateChannel { get; set; } = "stable";

    /// <summary>
    /// Whether the app checks for a new version on its own.
    ///
    /// Read by the timer that checks unprompted, and deliberately not by the check itself, so
    /// turning this off still leaves the Check button in the updates window working. The other way
    /// round, the button reports "this is the newest build" without having looked.
    /// </summary>
    public bool AutoCheckUpdates { get; set; } = true;

    /// <summary>
    /// When the last check was made, or null before the first one. Kept so a restart does not
    /// reset the hourly interval and turn every launch into another request.
    /// </summary>
    public DateTimeOffset? LastUpdateCheckUtc { get; set; }

    /// <summary>
    /// A copy, so a dialog can edit the fields it owns without touching the object the rest of the
    /// app is still reading, and without resetting the fields it does not show.
    ///
    /// Every field belongs here. A new one left out is not a compile error: it silently becomes a
    /// field that reverts to its default whenever anything saves a modified copy.
    /// </summary>
    public AppSettings Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        GameSavePath = GameSavePath,
        BackupRootPath = BackupRootPath,
        LibraryRootPath = LibraryRootPath,
        GameInstallPath = GameInstallPath,
        UpdateChannel = UpdateChannel,
        AutoCheckUpdates = AutoCheckUpdates,
        LastUpdateCheckUtc = LastUpdateCheckUtc,
    };

    /// <summary>
    /// %LOCALAPPDATA%\RainWorldCompanion\backups
    /// </summary>
    public static string DefaultBackupRootPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RainWorldCompanion",
        "backups");

    /// <summary>
    /// %LOCALAPPDATA%\RainWorldCompanion\library
    /// </summary>
    public static string DefaultLibraryRootPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RainWorldCompanion",
        "library");

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
        LibraryRootPath = DefaultLibraryRootPath,
    };
}
