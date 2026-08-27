// RainWorldCompanion.Core.System exists in this assembly, so a using written inside the namespace
// body would bind "System" to that namespace instead of the BCL root.
using System.Text.Json;

using RainWorldCompanion.Core.Backups;

namespace RainWorldCompanion.Core.Mods;

public sealed class ModStateRestorePoint
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public DateTimeOffset TakenAt { get; set; }

    public ModListSnapshot? Mods { get; set; }

    public List<string> EnabledModsLines { get; set; } = new();

    public string? Because { get; set; }

    public bool UsableForRestore => Mods is { ReadTheEnabledList: true };
}

public sealed class ModStateStore
{
    public const string FileName = "previous.json";

    public ModStateStore(string? folder = null)
    {
        Folder = string.IsNullOrWhiteSpace(folder) ? DefaultFolder : Path.GetFullPath(folder.Trim());
        FilePath = Path.Combine(Folder, FileName);
    }

    public string Folder { get; }

    public string FilePath { get; }

    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RainWorldCompanion",
        "modstate");

    public ModStateRestorePoint? Read()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            return JsonSerializer.Deserialize<ModStateRestorePoint>(File.ReadAllText(FilePath), BackupJson.Options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    public void Write(ModStateRestorePoint point)
    {
        ArgumentNullException.ThrowIfNull(point);

        Directory.CreateDirectory(Folder);

        string temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(point, BackupJson.Options));
        File.Move(temporary, FilePath, overwrite: true);
    }

    public void Clear()
    {
        try
        {
            File.Delete(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
