using System.Text.Json;
using System.Text.Json.Nodes;
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

    /// <summary>
    /// What the window needs before it is shown, its geometry and its theme, read straight off
    /// disk with no path resolution so it is safe to call on the UI thread. <see cref="Load"/>
    /// instead runs <see cref="SavePathResolver.FindSavePath"/> on a first-run file, which can
    /// stall.
    /// </summary>
    public AppSettings? ReadForStartup() => ReadFile();

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var directory = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = SettingsPath + ".tmp";
        File.WriteAllText(tempPath, Merge(settings, ReadObject()));
        File.Move(tempPath, SettingsPath, overwrite: true);
    }

    // Carries across keys this build has no property for. Serializing the object alone drops them,
    // so one run of an older build erased whatever a newer one had written.
    private static string Merge(AppSettings settings, JsonObject? existing)
    {
        var written = JsonSerializer.SerializeToNode(settings, SerializerOptions)!.AsObject();
        if (existing is null)
        {
            return written.ToJsonString(SerializerOptions);
        }

        // Matched without case, or a file still holding PascalCase names would come back with both
        // spellings of every property and the reader would be free to pick either.
        var known = written.Select(property => property.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var property in existing)
        {
            if (!known.Contains(property.Key))
            {
                written[property.Key] = property.Value?.DeepClone();
            }
        }

        return written.ToJsonString(SerializerOptions);
    }

    private JsonObject? ReadObject()
    {
        try
        {
            return File.Exists(SettingsPath)
                ? JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject
                : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException
                                      or NotSupportedException or ArgumentException)
        {
            return null;
        }
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
        settings.Theme = ReadString(root, "theme", settings.Theme);
        settings.LastUpdateCheckUtc = ReadTimestamp(root, "lastUpdateCheckUtc");
        settings.InstallId = ReadString(root, "installId", settings.InstallId);
        settings.TelemetryEnabled = ReadBool(root, "telemetryEnabled", settings.TelemetryEnabled);
        settings.LastSeenChangelogVersion = ReadString(root, "lastSeenChangelogVersion", settings.LastSeenChangelogVersion);
        settings.WindowWidth = ReadDoubleOrNull(root, "windowWidth");
        settings.WindowHeight = ReadDoubleOrNull(root, "windowHeight");
        settings.WindowLeft = ReadDoubleOrNull(root, "windowLeft");
        settings.WindowTop = ReadDoubleOrNull(root, "windowTop");
        settings.WindowMaximized = ReadBool(root, "windowMaximized", settings.WindowMaximized);

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

    private static double? ReadDoubleOrNull(JsonElement root, string name)
        => TryFind(root, name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var number)
            ? number
            : null;

    private static DateTimeOffset? ReadTimestamp(JsonElement root, string name)
        => TryFind(root, name, out var value)
            && value.ValueKind == JsonValueKind.String
            && value.TryGetDateTimeOffset(out var stamp)
            ? stamp
            : null;
}
