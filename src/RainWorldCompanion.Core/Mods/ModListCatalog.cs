using System.Text.Json;

using RainWorldCompanion.Core.Backups;

namespace RainWorldCompanion.Core.Mods;

public sealed class ModListCatalogView
{
    public static ModListCatalogView Empty { get; } = new();

    public IReadOnlyList<ModListProfile> Profiles { get; init; } = Array.Empty<ModListProfile>();

    public IReadOnlyList<ModListHistoryEntry> History { get; init; } = Array.Empty<ModListHistoryEntry>();

    public int UnreadableProfileCount { get; init; }

    public int UnreadableHistoryCount { get; init; }

    public int UnreadableEntryCount => UnreadableProfileCount + UnreadableHistoryCount;
}

public sealed class ModListCatalogResult
{
    public ModListCatalogResult(ModListCatalogView view, Guid? entryId = null, string? problem = null, string? warning = null)
    {
        View = view;
        EntryId = entryId;
        Problem = problem;
        Warning = warning;
    }

    public ModListCatalogView View { get; }

    public Guid? EntryId { get; }

    public string? Problem { get; }

    public string? Warning { get; }

    public bool Succeeded => Problem is null;
}

public sealed class ModListProfile
{
    public Guid Id { get; init; }

    public string Name { get; init; } = "";

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public ModListSnapshot Snapshot { get; init; } = new();
}

public sealed class ModListHistoryEntry
{
    public Guid Id { get; init; }

    public DateTimeOffset CapturedAt { get; init; }

    public string Reason { get; init; } = "";

    public ModListSnapshot Snapshot { get; init; } = new();

    public bool IsLegacy { get; init; }
}

public abstract record ModListCatalogCommand;

public sealed record SaveProfile(string Name, ModListSnapshot Snapshot) : ModListCatalogCommand;

public sealed record RenameProfile(Guid Id, string Name) : ModListCatalogCommand;

public sealed record ReplaceProfile(Guid Id, ModListSnapshot Snapshot) : ModListCatalogCommand;

public sealed record DeleteProfile(Guid Id) : ModListCatalogCommand;

public sealed record AppendHistory(ModListSnapshot Snapshot, string Reason) : ModListCatalogCommand;

internal sealed record PruneModListHistory : ModListCatalogCommand;

public sealed class ModListCatalog
{
    public const int HistoryLimit = 10;

    private const int CurrentSchemaVersion = 1;
    private static readonly Guid LegacyHistoryId = Guid.Parse("1a19042b-4040-45cc-b2b3-6692e6c850a6");
    private readonly TimeProvider _timeProvider;

    public ModListCatalog(string? root = null, TimeProvider? timeProvider = null)
    {
        Root = string.IsNullOrWhiteSpace(root) ? ModStateStore.DefaultFolder : Path.GetFullPath(root.Trim());
        ProfilesRoot = Path.Combine(Root, "profiles");
        HistoryRoot = Path.Combine(Root, "history");
        PreviousPath = Path.Combine(Root, ModStateStore.FileName);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string Root { get; }

    public string ProfilesRoot { get; }

    public string HistoryRoot { get; }

    public string PreviousPath { get; }

    public ModListCatalogView Read()
    {
        CatalogRead read = ReadCatalog();
        return read.View;
    }

    public ModListCatalogResult Execute(ModListCatalogCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        return command switch
        {
            SaveProfile save => Save(save),
            RenameProfile rename => Rename(rename),
            ReplaceProfile replace => Replace(replace),
            DeleteProfile delete => Delete(delete),
            AppendHistory append => Append(append),
            PruneModListHistory => Prune(),
            _ => new ModListCatalogResult(Read(), problem: "This mod-list action is not supported."),
        };
    }

    private ModListCatalogResult Save(SaveProfile command)
    {
        string? problem = ValidateName(command.Name, null);
        if (problem is not null)
        {
            return Failed(problem);
        }

        if (!TryCopySnapshot(command.Snapshot, out ModListSnapshot snapshot, out problem))
        {
            return Failed(problem!);
        }

        if (!TryMigrate(out string? warning, out problem))
        {
            return Failed(problem!);
        }

        Guid id = Guid.NewGuid();
        DateTimeOffset now = _timeProvider.GetUtcNow();
        var profile = new ModListProfile
        {
            Id = id,
            Name = command.Name.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
            Snapshot = snapshot,
        };

        if (!TryWrite(ProfilePath(id), ToDocument(profile), out problem))
        {
            return Failed(problem!);
        }

        return Success(id, warning);
    }

    private ModListCatalogResult Rename(RenameProfile command)
    {
        CatalogRead read = ReadCatalog();
        ModListProfile? existing = read.View.Profiles.FirstOrDefault(profile => profile.Id == command.Id);
        if (existing is null)
        {
            return new ModListCatalogResult(read.View, problem: "That saved list no longer exists.");
        }

        string? problem = ValidateName(command.Name, command.Id, read.View);
        if (problem is not null)
        {
            return new ModListCatalogResult(read.View, problem: problem);
        }

        if (!TryMigrate(out string? warning, out problem))
        {
            return Failed(problem!);
        }

        var changed = new ModListProfile
        {
            Id = existing.Id,
            Name = command.Name.Trim(),
            CreatedAt = existing.CreatedAt,
            UpdatedAt = _timeProvider.GetUtcNow(),
            Snapshot = existing.Snapshot,
        };

        if (!TryWrite(ProfilePath(changed.Id), ToDocument(changed), out problem))
        {
            return Failed(problem!);
        }

        return Success(changed.Id, warning);
    }

    private ModListCatalogResult Replace(ReplaceProfile command)
    {
        CatalogRead read = ReadCatalog();
        ModListProfile? existing = read.View.Profiles.FirstOrDefault(profile => profile.Id == command.Id);
        if (existing is null)
        {
            return new ModListCatalogResult(read.View, problem: "That saved list no longer exists.");
        }

        if (!TryCopySnapshot(command.Snapshot, out ModListSnapshot snapshot, out string? problem))
        {
            return new ModListCatalogResult(read.View, problem: problem);
        }

        if (!TryMigrate(out string? warning, out problem))
        {
            return Failed(problem!);
        }

        var changed = new ModListProfile
        {
            Id = existing.Id,
            Name = existing.Name,
            CreatedAt = existing.CreatedAt,
            UpdatedAt = _timeProvider.GetUtcNow(),
            Snapshot = snapshot,
        };

        if (!TryWrite(ProfilePath(changed.Id), ToDocument(changed), out problem))
        {
            return Failed(problem!);
        }

        return Success(changed.Id, warning);
    }

    private ModListCatalogResult Delete(DeleteProfile command)
    {
        CatalogRead read = ReadCatalog();
        if (read.View.Profiles.All(profile => profile.Id != command.Id))
        {
            return new ModListCatalogResult(read.View, problem: "That saved list no longer exists.");
        }

        if (!TryMigrate(out string? warning, out string? problem))
        {
            return Failed(problem!);
        }

        try
        {
            File.Delete(ProfilePath(command.Id));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failed("The saved list could not be deleted: " + ex.Message);
        }

        return Success(command.Id, warning);
    }

    private ModListCatalogResult Append(AppendHistory command)
    {
        if (!TryCopySnapshot(command.Snapshot, out ModListSnapshot snapshot, out string? problem))
        {
            return Failed(problem!);
        }

        string reason = command.Reason?.Trim() ?? "";
        if (reason.Length == 0)
        {
            return Failed("A recovery entry needs a reason.");
        }

        if (!TryMigrate(out string? warning, out problem))
        {
            return Failed(problem!);
        }

        Guid id = Guid.NewGuid();
        DateTimeOffset capturedAt = _timeProvider.GetUtcNow();
        DateTimeOffset? newest = ReadCatalog(includeLegacy: false).View.History.FirstOrDefault()?.CapturedAt;
        if (newest >= capturedAt)
        {
            capturedAt = newest.Value.AddTicks(1);
        }

        var entry = new ModListHistoryEntry
        {
            Id = id,
            CapturedAt = capturedAt,
            Reason = reason,
            Snapshot = snapshot,
        };

        if (!TryWrite(HistoryPath(capturedAt, id), ToDocument(entry), out problem))
        {
            return Failed(problem!);
        }

        return Success(id, warning);
    }

    private ModListCatalogResult Prune()
    {
        if (!TryMigrate(out string? warning, out string? problem))
        {
            return Failed(problem!);
        }

        CatalogRead read = ReadCatalog();
        foreach (HistoryFile entry in read.ValidHistoryFiles.Skip(HistoryLimit))
        {
            try
            {
                File.Delete(entry.Path!);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new ModListCatalogResult(Read(), problem: "Old recovery entries could not be removed: " + ex.Message);
            }
        }

        return Success(null, warning);
    }

    private ModListCatalogResult Success(Guid? id, string? warning)
    {
        return new ModListCatalogResult(Read(), id, warning: warning);
    }

    private ModListCatalogResult Failed(string problem)
    {
        return new ModListCatalogResult(Read(), problem: problem);
    }

    private string? ValidateName(string name, Guid? exceptId, ModListCatalogView? view = null)
    {
        string trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            return "Saved list names need at least one character.";
        }

        if (trimmed.Length > 80)
        {
            return "Saved list names can be at most 80 characters.";
        }

        view ??= Read();
        if (view.Profiles.Any(profile => profile.Id != exceptId && string.Equals(profile.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return "A saved list already uses that name.";
        }

        return null;
    }

    private bool TryMigrate(out string? warning, out string? problem)
    {
        warning = null;
        problem = null;

        LegacyRead legacy = ReadLegacy();
        if (!legacy.Exists)
        {
            return true;
        }

        if (legacy.Entry is null)
        {
            warning = "The older previous-list file could not be read.";
            return true;
        }

        CatalogRead read = ReadCatalog(includeLegacy: false);
        bool present = read.ValidHistoryFiles.Any(entry => entry.Entry.Id == LegacyHistoryId);
        if (!present && !TryWrite(HistoryPath(legacy.Entry.CapturedAt, LegacyHistoryId), ToDocument(legacy.Entry), out problem))
        {
            return false;
        }

        if (!present && !ReadCatalog(includeLegacy: false).ValidHistoryFiles.Any(entry => entry.Entry.Id == LegacyHistoryId))
        {
            problem = "The previous mod list could not be verified after migration.";
            return false;
        }

        try
        {
            File.Delete(PreviousPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warning = "The older previous-list file will be cleaned up later: " + ex.Message;
        }

        return true;
    }

    private CatalogRead ReadCatalog(bool includeLegacy = true)
    {
        var profiles = new List<ModListProfile>();
        var history = new List<HistoryFile>();
        int unreadableProfiles = ReadProfiles(profiles);
        int unreadableHistory = ReadHistory(history);

        if (includeLegacy)
        {
            LegacyRead legacy = ReadLegacy();
            if (legacy.Exists && legacy.Entry is null)
            {
                unreadableHistory++;
            }
            else if (legacy.Entry is not null && history.All(entry => entry.Entry.Id != LegacyHistoryId))
            {
                history.Add(new HistoryFile(legacy.Entry, null));
            }
        }

        HistoryFile[] orderedHistory = history
            .OrderByDescending(entry => entry.Entry.CapturedAt)
            .ThenBy(entry => entry.Entry.Id)
            .ToArray();
        ModListProfile[] orderedProfiles = profiles
            .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(profile => profile.Id)
            .ToArray();

        return new CatalogRead(
            new ModListCatalogView
            {
                Profiles = orderedProfiles,
                History = orderedHistory.Select(entry => entry.Entry).ToArray(),
                UnreadableProfileCount = unreadableProfiles,
                UnreadableHistoryCount = unreadableHistory,
            },
            orderedHistory.Where(entry => entry.Path is not null).ToArray());
    }

    private int ReadProfiles(List<ModListProfile> profiles)
    {
        if (!Directory.Exists(ProfilesRoot))
        {
            return 0;
        }

        int unreadable = 0;
        try
        {
            foreach (string path in Directory.EnumerateFiles(ProfilesRoot, "*.json", SearchOption.TopDirectoryOnly))
            {
                if (TryReadProfile(path, out ModListProfile? profile))
                {
                    profiles.Add(profile!);
                }
                else
                {
                    unreadable++;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            unreadable++;
        }

        return unreadable;
    }

    private int ReadHistory(List<HistoryFile> history)
    {
        if (!Directory.Exists(HistoryRoot))
        {
            return 0;
        }

        int unreadable = 0;
        try
        {
            foreach (string path in Directory.EnumerateFiles(HistoryRoot, "*.json", SearchOption.TopDirectoryOnly))
            {
                if (TryReadHistory(path, out ModListHistoryEntry? entry))
                {
                    history.Add(new HistoryFile(entry!, path));
                }
                else
                {
                    unreadable++;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            unreadable++;
        }

        return unreadable;
    }

    private LegacyRead ReadLegacy()
    {
        if (!File.Exists(PreviousPath))
        {
            return new LegacyRead(false, null);
        }

        try
        {
            ModStateRestorePoint? point = JsonSerializer.Deserialize<ModStateRestorePoint>(File.ReadAllText(PreviousPath), BackupJson.Options);
            if (point is null || point.SchemaVersion > ModStateRestorePoint.CurrentSchemaVersion || point.Mods is null || !point.UsableForRestore || !TryCopySnapshot(point.Mods, out ModListSnapshot snapshot, out _))
            {
                return new LegacyRead(true, null);
            }

            return new LegacyRead(true, new ModListHistoryEntry
            {
                Id = LegacyHistoryId,
                CapturedAt = point.TakenAt,
                Reason = string.IsNullOrWhiteSpace(point.Because) ? "Previous mod list" : point.Because.Trim(),
                Snapshot = snapshot,
                IsLegacy = true,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new LegacyRead(true, null);
        }
    }

    private bool TryReadProfile(string path, out ModListProfile? profile)
    {
        profile = null;
        try
        {
            CatalogProfileDocument? document = JsonSerializer.Deserialize<CatalogProfileDocument>(File.ReadAllText(path), BackupJson.Options);
            if (document is null || document.SchemaVersion != CurrentSchemaVersion || !Guid.TryParse(document.Id, out Guid id) || !TryDocumentSnapshot(document.GameVersion, document.Mods, out ModListSnapshot snapshot))
            {
                return false;
            }

            string name = document.Name?.Trim() ?? "";
            if (name.Length is < 1 or > 80 || document.CreatedAt == default || document.UpdatedAt == default)
            {
                return false;
            }

            profile = new ModListProfile
            {
                Id = id,
                Name = name,
                CreatedAt = document.CreatedAt,
                UpdatedAt = document.UpdatedAt,
                Snapshot = snapshot,
            };
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return false;
        }
    }

    private bool TryReadHistory(string path, out ModListHistoryEntry? entry)
    {
        entry = null;
        try
        {
            CatalogHistoryDocument? document = JsonSerializer.Deserialize<CatalogHistoryDocument>(File.ReadAllText(path), BackupJson.Options);
            if (document is null || document.SchemaVersion != CurrentSchemaVersion || !Guid.TryParse(document.Id, out Guid id) || document.CapturedAt == default || string.IsNullOrWhiteSpace(document.Reason) || !TryDocumentSnapshot(document.GameVersion, document.Mods, out ModListSnapshot snapshot))
            {
                return false;
            }

            entry = new ModListHistoryEntry
            {
                Id = id,
                CapturedAt = document.CapturedAt,
                Reason = document.Reason.Trim(),
                Snapshot = snapshot,
            };
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return false;
        }
    }

    private bool TryWrite<T>(string path, T value, out string? problem)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory))
            {
                problem = "The mod-list folder is invalid.";
                return false;
            }

            Directory.CreateDirectory(directory);
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, BackupJson.Options));
            File.Move(temporary, path, overwrite: true);
            problem = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            problem = "The mod-list catalog could not be saved: " + ex.Message;
            return false;
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static bool TryCopySnapshot(ModListSnapshot? source, out ModListSnapshot snapshot, out string? problem)
    {
        snapshot = new ModListSnapshot();
        problem = null;
        if (source is null)
        {
            problem = "The mod list is missing.";
            return false;
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mods = new List<ModEntry>(source.Mods.Count);
        for (int index = 0; index < source.Mods.Count; index++)
        {
            ModEntry? mod = source.Mods[index];
            string id = mod?.Id?.Trim() ?? "";
            if (id.Length == 0)
            {
                problem = $"Mod {index + 1} has no id.";
                return false;
            }

            if (!ids.Add(id))
            {
                problem = $"The mod id \"{id}\" appears more than once.";
                return false;
            }

            mods.Add(new ModEntry
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(mod!.Name) ? id : mod.Name.Trim(),
                Version = Clean(mod.Version),
                WorkshopId = Clean(mod.WorkshopId),
                LoadOrder = mod.LoadOrder ?? index,
            });
        }

        snapshot = new ModListSnapshot
        {
            GameVersion = Clean(source.GameVersion),
            ReadTheEnabledList = true,
            Mods = mods,
        };
        return true;
    }

    private static bool TryDocumentSnapshot(string? gameVersion, List<CatalogModEntry>? mods, out ModListSnapshot snapshot)
    {
        snapshot = new ModListSnapshot();
        if (mods is null)
        {
            return false;
        }

        return TryCopySnapshot(new ModListSnapshot
        {
            GameVersion = gameVersion,
            ReadTheEnabledList = true,
            Mods = mods.Select((mod, index) => new ModEntry
            {
                Id = mod?.Id ?? "",
                Name = mod?.Name ?? "",
                Version = mod?.Version,
                WorkshopId = mod?.WorkshopId,
                LoadOrder = mod?.LoadOrder ?? index,
            }).ToList(),
        }, out snapshot, out _);
    }

    private static CatalogProfileDocument ToDocument(ModListProfile profile)
    {
        return new CatalogProfileDocument
        {
            SchemaVersion = CurrentSchemaVersion,
            Id = profile.Id.ToString(),
            Name = profile.Name,
            CreatedAt = profile.CreatedAt,
            UpdatedAt = profile.UpdatedAt,
            GameVersion = profile.Snapshot.GameVersion,
            Mods = ToDocumentMods(profile.Snapshot),
        };
    }

    private static CatalogHistoryDocument ToDocument(ModListHistoryEntry entry)
    {
        return new CatalogHistoryDocument
        {
            SchemaVersion = CurrentSchemaVersion,
            Id = entry.Id.ToString(),
            CapturedAt = entry.CapturedAt,
            Reason = entry.Reason,
            GameVersion = entry.Snapshot.GameVersion,
            Mods = ToDocumentMods(entry.Snapshot),
        };
    }

    private static List<CatalogModEntry> ToDocumentMods(ModListSnapshot snapshot)
    {
        return snapshot.Mods.Select((mod, index) => new CatalogModEntry
        {
            Id = mod.Id,
            Name = mod.Name,
            Version = mod.Version,
            WorkshopId = mod.WorkshopId,
            LoadOrder = mod.LoadOrder ?? index,
        }).ToList();
    }

    private string ProfilePath(Guid id) => Path.Combine(ProfilesRoot, id.ToString("D") + ".json");

    private string HistoryPath(DateTimeOffset capturedAt, Guid id) => Path.Combine(
        HistoryRoot,
        capturedAt.UtcDateTime.ToString("yyyyMMddHHmmssfffffff", global::System.Globalization.CultureInfo.InvariantCulture) + "-" + id.ToString("D") + ".json");

    private static string? Clean(string? value)
    {
        string? cleaned = value?.Trim();
        return cleaned is { Length: > 0 } ? cleaned : null;
    }

    private sealed record CatalogRead(ModListCatalogView View, IReadOnlyList<HistoryFile> ValidHistoryFiles);

    private sealed record HistoryFile(ModListHistoryEntry Entry, string? Path);

    private sealed record LegacyRead(bool Exists, ModListHistoryEntry? Entry);
}

internal sealed class CatalogProfileDocument
{
    public int SchemaVersion { get; set; }

    public string? Id { get; set; }

    public string? Name { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public string? GameVersion { get; set; }

    public List<CatalogModEntry>? Mods { get; set; }
}

internal sealed class CatalogHistoryDocument
{
    public int SchemaVersion { get; set; }

    public string? Id { get; set; }

    public DateTimeOffset CapturedAt { get; set; }

    public string? Reason { get; set; }

    public string? GameVersion { get; set; }

    public List<CatalogModEntry>? Mods { get; set; }
}

internal sealed class CatalogModEntry
{
    public string? Id { get; set; }

    public string? Name { get; set; }

    public string? Version { get; set; }

    public string? WorkshopId { get; set; }

    public int? LoadOrder { get; set; }
}
