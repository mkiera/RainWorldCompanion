// RainWorldCompanion.Core.System exists in this assembly, so a using written inside the namespace
// body would bind "System" to that namespace instead of the BCL root.
using System.Text.Json;

using RainWorldCompanion.Core.Backups;

namespace RainWorldCompanion.Core.Mods;

/// <summary>
/// What the mods were before the app last changed them. The options file is deliberately outside
/// backup scope, because the game rewrites it whenever it feels like it, so this cannot ride along
/// on a safety snapshot and keeps its own copy instead.
/// </summary>
public sealed class ModStateRestorePoint
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public DateTimeOffset TakenAt { get; set; }

    /// <summary>The list that was on. Null means the file says nothing, which must never be read as
    /// "nothing was on".</summary>
    public ModListSnapshot? Mods { get; set; }

    /// <summary>The loader's own file, kept verbatim. Restoring puts these lines back rather than
    /// working out what they should have been.</summary>
    public List<string> EnabledModsLines { get; set; } = new();

    /// <summary>What the app did that this was taken before, for the window to name it.</summary>
    public string? Because { get; set; }

    public bool UsableForRestore => Mods is { ReadTheEnabledList: true };
}

/// <summary>One slot, overwritten each time mods are applied. A second one would need a management
/// screen, and the ask was a way back to your own list rather than a history of everyone else's.</summary>
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

    /// <summary>Never throws. Null covers no restore point, an unreadable one and a corrupt one
    /// alike, because none of them can be put back.</summary>
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

    /// <exception cref="IOException">The restore point could not be written, which stops the apply:
    /// changing the mods without a way back is the one outcome worth refusing over.</exception>
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
