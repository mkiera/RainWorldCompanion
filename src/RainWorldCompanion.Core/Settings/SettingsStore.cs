using System.Text.Json;
using RainWorldCompanion.Core.System;

namespace RainWorldCompanion.Core.Settings;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Older files were written with PascalCase names and should still load.
        PropertyNameCaseInsensitive = true,
    };

    /// <param name="settingsPath">Null or blank uses <see cref="DefaultSettingsPath"/>.</param>
    public SettingsStore(string? settingsPath = null)
    {
        SettingsPath = string.IsNullOrWhiteSpace(settingsPath)
            ? DefaultSettingsPath
            : Path.GetFullPath(settingsPath.Trim());
    }

    public string SettingsPath { get; }

    public static string DefaultSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RainWorldCompanion",
        "settings.json");

    /// <summary>
    /// A missing, empty or corrupt file falls back to <see cref="AppSettings.CreateDefault"/>, and
    /// blank fields in an otherwise valid file are filled in.
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

        settings.BackupRootPath = SettingsMigration.Repoint(settings.BackupRootPath);
        settings.LibraryRootPath = SettingsMigration.Repoint(settings.LibraryRootPath);

        if (string.IsNullOrWhiteSpace(settings.GameInstallPath))
        {
            settings.GameInstallPath = GameInstallLocator.FindInstallPath();
        }

        if (settings.SchemaVersion <= 0)
        {
            settings.SchemaVersion = 1;
        }

        return settings;
    }

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
    /// Field by field, never all at once: a single Deserialize call throws on one property whose
    /// type does not match and discards the whole file for it, backup root included.
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
    /// JsonElement.TryGetProperty is case-sensitive, and this file has been written with both
    /// PascalCase and camelCase names, so a hand-read property has to match without case.
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
