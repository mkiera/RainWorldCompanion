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

            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return FromJson(document.RootElement);
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

    /// <summary>
    /// Folds a settings object field by field, never all at once.
    ///
    /// A single Deserialize call is all or nothing: one property whose type does not match throws,
    /// and the whole file is discarded for it. That was survivable while every field was years
    /// old, and it stops being survivable once an older build can be installed over a newer one,
    /// because the older build then reads a file written by the newer one. Losing
    /// <see cref="AppSettings.BackupRootPath"/> to an unrelated bad field leaves the backups on
    /// disk with nothing pointing at them, which to the person looking is indistinguishable from
    /// having lost them.
    ///
    /// A document that is not an object still falls back wholesale, because there is nothing in
    /// one to salvage. Only a bad field is survivable, and it costs only itself.
    /// </summary>
    private static AppSettings FromJson(JsonElement root)
    {
        var settings = new AppSettings();

        settings.SchemaVersion = ReadInt(root, "schemaVersion", settings.SchemaVersion);
        settings.GameSavePath = ReadString(root, "gameSavePath", settings.GameSavePath);
        settings.BackupRootPath = ReadString(root, "backupRootPath", settings.BackupRootPath);
        settings.LibraryRootPath = ReadString(root, "libraryRootPath", settings.LibraryRootPath);
        settings.GameInstallPath = ReadStringOrNull(root, "gameInstallPath");
        settings.UpdateChannel = ReadString(root, "updateChannel", settings.UpdateChannel);
        settings.AutoCheckUpdates = ReadBool(root, "autoCheckUpdates", settings.AutoCheckUpdates);
        settings.LastUpdateCheckUtc = ReadTimestamp(root, "lastUpdateCheckUtc");

        return settings;
    }

    /// <summary>
    /// One property, matched without regard to case.
    ///
    /// JsonElement.TryGetProperty is case-sensitive, and this file has been written with both
    /// PascalCase and camelCase names over its life, which is why the serializer options set
    /// PropertyNameCaseInsensitive. Reading properties by hand has to keep that promise, or every
    /// file written before the naming policy was set silently loads as blank.
    /// </summary>
    private static bool TryFind(JsonElement root, string name, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string ReadString(JsonElement root, string name, string fallback)
        => TryFind(root, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static string? ReadStringOrNull(JsonElement root, string name)
        => TryFind(root, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int ReadInt(JsonElement root, string name, int fallback)
        => TryFind(root, name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
            ? number
            : fallback;

    private static bool ReadBool(JsonElement root, string name, bool fallback)
        => TryFind(root, name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => fallback,
            }
            : fallback;

    private static DateTimeOffset? ReadTimestamp(JsonElement root, string name)
        => TryFind(root, name, out var value)
            && value.ValueKind == JsonValueKind.String
            && value.TryGetDateTimeOffset(out var stamp)
            ? stamp
            : null;
}
