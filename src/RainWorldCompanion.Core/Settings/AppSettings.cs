using RainWorldCompanion.Core.System;

namespace RainWorldCompanion.Core.Settings;

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    public string GameSavePath { get; set; } = "";

    public string BackupRootPath { get; set; } = "";

    /// <summary>
    /// Blank in a settings file written before the library existed. SettingsStore.Load fills a
    /// blank with <see cref="DefaultLibraryRootPath"/>.
    /// </summary>
    public string LibraryRootPath { get; set; } = "";

    /// <summary>
    /// Only the slugcat portrait art reads this, so a null or wrong value costs the portraits and
    /// nothing else and SettingsValidation deliberately does not check it.
    /// </summary>
    public string? GameInstallPath { get; set; }

    /// <summary>
    /// "stable", "prerelease" or "alpha". Text rather than the enum, because System.Text.Json
    /// writes an enum as its ordinal and inserting a channel would change what old files mean.
    /// Anything unrecognised reads as stable.
    /// </summary>
    public string UpdateChannel { get; set; } = "stable";

    /// <summary>
    /// Read by the unprompted timer and deliberately not by the check itself, so turning this off
    /// still leaves the Check button working.
    /// </summary>
    public bool AutoCheckUpdates { get; set; } = true;

    /// <summary>
    /// "light" or "dark", text rather than the enum for the same reason as
    /// <see cref="UpdateChannel"/>. Anything else reads as dark, so a file written before the
    /// toggle existed opens dark along with a fresh one.
    /// </summary>
    public string Theme { get; set; } = "dark";

    public DateTimeOffset? LastUpdateCheckUtc { get; set; }

    /// <summary>
    /// Blank on a first run, and blank deliberately does not mean "show them": the first launch
    /// records what it is running and says nothing.
    /// </summary>
    public string LastSeenChangelogVersion { get; set; } = "";

    /// <summary>
    /// Null means never saved, which is not the same as a saved 0: a null leaves the window to
    /// centre itself on the natural default size, a 0 would pin it to the screen edge.
    /// </summary>
    public double? WindowWidth { get; set; }

    public double? WindowHeight { get; set; }

    public double? WindowLeft { get; set; }

    public double? WindowTop { get; set; }

    public bool WindowMaximized { get; set; }

    /// <summary>
    /// Every field belongs here. A new one left out is not a compile error: it silently reverts to
    /// its default whenever anything saves a modified copy.
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
        Theme = Theme,
        LastUpdateCheckUtc = LastUpdateCheckUtc,
        LastSeenChangelogVersion = LastSeenChangelogVersion,
        WindowWidth = WindowWidth,
        WindowHeight = WindowHeight,
        WindowLeft = WindowLeft,
        WindowTop = WindowTop,
        WindowMaximized = WindowMaximized,
    };

    public static string DefaultBackupRootPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RainWorldCompanion",
        "backups");

    public static string DefaultLibraryRootPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RainWorldCompanion",
        "library");

    /// <summary>
    /// <see cref="GameInstallPath"/> is left null on purpose: finding the install probes every
    /// Steam library folder, which can block for the full SMB timeout.
    /// <see cref="SettingsStore.Load"/> fills it instead, and every caller runs Load on a worker.
    /// </summary>
    public static AppSettings CreateDefault() => new()
    {
        SchemaVersion = 1,
        GameSavePath = SavePathResolver.FindSavePath() ?? SavePathResolver.DefaultSavePath,
        BackupRootPath = DefaultBackupRootPath,
        LibraryRootPath = DefaultLibraryRootPath,
    };
}
