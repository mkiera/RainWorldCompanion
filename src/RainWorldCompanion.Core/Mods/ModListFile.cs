// RainWorldCompanion.Core.System exists in this assembly, so a using written inside the namespace
// body would bind "System" to that namespace instead of the BCL root.
using System.Text.Json;
using RainWorldCompanion.Core.Backups;

namespace RainWorldCompanion.Core.Mods;

public static class ModListFile
{
    public const string Extension = ".rwmods";

    private const int CurrentSchemaVersion = 1;
    private const int MaxMods = 4096;
    private const long MaxFileBytes = 4L * 1024 * 1024;

    public static void Write(string path, ModListSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(snapshot);

        var document = new ModListDocument
        {
            SchemaVersion = CurrentSchemaVersion,
            GameVersion = Clean(snapshot.GameVersion),
            Mods = snapshot.Mods.Select(mod => new ModListDocumentEntry
            {
                Id = mod.Id,
                Name = mod.Name,
                Version = mod.Version,
                WorkshopId = mod.WorkshopId,
                LoadOrder = mod.LoadOrder,
            }).ToList(),
        };

        string temporary = path + ".rwc-tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(document, BackupJson.Options));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            Delete(temporary);
        }
    }

    public static ModListSnapshot Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxFileBytes)
        {
            throw new InvalidDataException("The mod list is too large to import.");
        }

        ModListDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<ModListDocument>(stream, BackupJson.Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The file is not a valid Rain World mod list.", ex);
        }

        if (document is null || document.Mods is null)
        {
            throw new InvalidDataException("The file does not contain a Rain World mod list.");
        }

        if (document.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                document.SchemaVersion > CurrentSchemaVersion
                    ? "This mod list was made by a newer version of the app."
                    : "This mod list uses an unsupported format version.");
        }

        if (document.Mods.Count > MaxMods)
        {
            throw new InvalidDataException($"The mod list contains more than {MaxMods} mods.");
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mods = new List<ModEntry>(document.Mods.Count);

        for (int index = 0; index < document.Mods.Count; index++)
        {
            if (document.Mods[index] is not { } item)
            {
                throw new InvalidDataException($"Mod {index + 1} is empty.");
            }

            string id = Clean(item.Id) ?? "";

            if (id.Length == 0)
            {
                throw new InvalidDataException($"Mod {index + 1} has no id.");
            }

            if (!ids.Add(id))
            {
                throw new InvalidDataException($"The mod id \"{id}\" appears more than once.");
            }

            mods.Add(new ModEntry
            {
                Id = id,
                Name = Clean(item.Name) ?? id,
                Version = Clean(item.Version),
                WorkshopId = WorkshopId(item.WorkshopId),
                LoadOrder = item.LoadOrder ?? index,
            });
        }

        return new ModListSnapshot
        {
            GameVersion = Clean(document.GameVersion),
            ReadTheEnabledList = true,
            Mods = mods,
        };
    }

    private static string? Clean(string? value)
    {
        string? cleaned = value?.Trim();
        return cleaned is { Length: > 0 } ? cleaned : null;
    }

    private static string? WorkshopId(string? value)
    {
        string? cleaned = Clean(value);
        return cleaned is not null && cleaned.All(char.IsAsciiDigit) ? cleaned : null;
    }

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

internal sealed class ModListDocument
{
    public int SchemaVersion { get; set; }

    public string? GameVersion { get; set; }

    public List<ModListDocumentEntry>? Mods { get; set; }
}

internal sealed class ModListDocumentEntry
{
    public string? Id { get; set; }

    public string? Name { get; set; }

    public string? Version { get; set; }

    public string? WorkshopId { get; set; }

    public int? LoadOrder { get; set; }
}
