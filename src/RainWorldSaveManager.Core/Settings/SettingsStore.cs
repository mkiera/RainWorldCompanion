using System.Text.Json;
using RainWorldSaveManager.Core.System;

namespace RainWorldSaveManager.Core.Settings;

/// <summary>
/// Reads and writes <see cref="AppSettings"/> as JSON.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Older files were written with PascalCase names and should still load.
        PropertyNameCaseInsensitive = true,
    };

    /// <param name="settingsPath">
    /// Where to store the file. Null or blank uses <see cref="DefaultSettingsPath"/>.
    /// </param>
    public SettingsStore(string? settingsPath = null)
    {
        SettingsPath = string.IsNullOrWhiteSpace(settingsPath)
            ? DefaultSettingsPath
            : Path.GetFullPath(settingsPath.Trim());
    }

    public string SettingsPath { get; }

    /// <summary>
    /// %LOCALAPPDATA%\RainWorldSaveManager\settings.json
    /// </summary>
    public static string DefaultSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RainWorldSaveManager",
        "settings.json");

    /// <summary>
    /// Loads the settings. A missing, empty or corrupt file falls back to
    /// <see cref="AppSettings.CreateDefault"/>. Blank fields in an otherwise valid file are
    /// filled in, so a partial file from an older version still yields a usable configuration.
    /// </summary>
    public AppSettings Load()
    {
        var settings = ReadFile();
        settings ??= AppSettings.CreateDefault();

        if (string.IsNullOrWhiteSpace(settings.GameSavePath))
        {
            settings.GameSavePath = SavePathResolver.FindSavePath() ?? SavePathResolver.DefaultSavePath;
        }

        if (string.IsNullOrWhiteSpace(settings.BackupRootPath))
        {
            settings.BackupRootPath = AppSettings.DefaultBackupRootPath;
        }

        if (string.IsNullOrWhiteSpace(settings.LibraryRootPath))
        {
            settings.LibraryRootPath = AppSettings.DefaultLibraryRootPath;
        }

        if (string.IsNullOrWhiteSpace(settings.GameInstallPath))
        {
            // Stays null when no install is found. The portraits are the only thing that reads
            // it, so an absent install is a normal state and not a validation failure.
            settings.GameInstallPath = GameInstallLocator.FindInstallPath();
        }

        if (settings.SchemaVersion <= 0)
        {
            settings.SchemaVersion = 1;
        }

        return settings;
    }

    /// <summary>
    /// Writes the settings through a .tmp file so an interrupted write cannot leave a
    /// half-written settings.json behind.
    /// </summary>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var directory = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = SettingsPath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(settings, SerializerOptions));
        File.Move(tempPath, SettingsPath, overwrite: true);
    }

    private AppSettings? ReadFile()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return null;
            }

            var json = File.ReadAllText(SettingsPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
