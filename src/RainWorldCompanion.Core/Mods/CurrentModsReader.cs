// RainWorldCompanion.Core.System exists in this assembly, so a using written inside the namespace
// body would bind "System" to that namespace instead of the BCL root.
using System.Text.Json;

namespace RainWorldCompanion.Core.Mods;

/// <param name="Installed">Every mod found on disk, turned on or not. This is what tells "turned
/// off" apart from "not installed" when a recorded list is compared against the machine later.</param>
public sealed record CurrentMods(ModListSnapshot Enabled, IReadOnlyList<ModEntry> Installed)
{
    /// <summary>What to use when nothing could be read at all.</summary>
    public static CurrentMods NothingRead(string? note) => new(
        new ModListSnapshot { Note = note },
        Array.Empty<ModEntry>());

    // The enabled list counts too: a mod the game has turned on but that was not found on disk is
    // still one the player has.
    public bool Has(string modId) =>
        Installed.Concat(Enabled.Mods).Any(m => string.Equals(m.Id, modId, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Three sources, each of which can be absent on its own: the options file says what is on, the
/// mods and workshop folders say what is installed and at what version, and GameVersion.txt says
/// which game they are for. Every answer here is allowed to be "did not look".
/// </summary>
public static class CurrentModsReader
{
    /// <summary>A folder under mods that is not a mod. The game skips it by name, so this does too.</summary>
    public const string SkippedInstallFolder = "versioning";

    /// <summary>Rain World on Steam, which is the folder the workshop keeps its items under.</summary>
    public const string SteamAppId = "312520";

    private const string ModsRelativePath = @"RainWorld_Data\StreamingAssets\mods";
    private const string GameVersionRelativePath = @"RainWorld_Data\StreamingAssets\GameVersion.txt";
    private const string ModInfoFileName = "modinfo.json";
    private const string SteamAppsFolder = "steamapps";

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Never throws: each missing path costs an answer rather than the caller.</summary>
    public static CurrentMods Read(string? saveRoot, string? gameInstallPath)
    {
        OptionsRead options = OptionsFile.Read(saveRoot);

        List<ModEntry> installed = new();
        bool checkedInstall = ReadInstallFolder(gameInstallPath, installed);
        bool checkedWorkshop = ReadWorkshopFolder(gameInstallPath, installed);

        var byId = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (ModEntry mod in installed)
        {
            // A mod present in both places keeps its install copy, because that is the one the
            // game loads, but takes the workshop id from the other so a Steam link still exists.
            if (byId.TryGetValue(mod.Id, out ModEntry? seen))
            {
                seen.WorkshopId ??= mod.WorkshopId;
                continue;
            }

            byId[mod.Id] = mod;
        }

        var snapshot = new ModListSnapshot
        {
            ReadTheEnabledList = options.Read,
            CheckedTheInstall = checkedInstall,
            CheckedTheWorkshop = checkedWorkshop,
            GameVersion = options.LastGameVersion ?? ReadGameVersion(gameInstallPath),
            Note = BuildNote(options, checkedInstall),
            Mods = BuildEnabled(options, byId),
        };

        return new CurrentMods(snapshot, byId.Values.OrderBy(mod => mod.Id, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>Found by walking up from the install to its steamapps folder rather than assuming
    /// the default Steam root, because an app's workshop content lives in the same library the app
    /// does. Null for an install not laid out the way Steam lays one out.</summary>
    internal static string? WorkshopContentPath(string? gameInstallPath)
    {
        if (string.IsNullOrWhiteSpace(gameInstallPath))
        {
            return null;
        }

        try
        {
            DirectoryInfo? folder = new DirectoryInfo(gameInstallPath);
            while (folder is not null)
            {
                if (string.Equals(folder.Name, SteamAppsFolder, StringComparison.OrdinalIgnoreCase))
                {
                    return Path.Combine(folder.FullName, "workshop", "content", SteamAppId);
                }

                folder = folder.Parent;
            }
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (PathTooLongException)
        {
            return null;
        }

        return null;
    }

    /// <summary>The enabled ids resolved against what is installed, in load order.</summary>
    private static List<ModEntry> BuildEnabled(OptionsRead options, Dictionary<string, ModEntry> installed)
    {
        var enabled = new List<ModEntry>();

        foreach (string id in options.EnabledModIds)
        {
            int? order = options.LoadOrder.TryGetValue(id, out int position) ? position : null;

            // A mod the game has on but that is nowhere on disk is recorded as its id and nothing else.
            ModEntry found = installed.TryGetValue(id, out ModEntry? match)
                ? new ModEntry
                {
                    Id = match.Id,
                    Name = match.Name,
                    Version = match.Version,
                    FolderName = match.FolderName,
                    WorkshopId = match.WorkshopId,
                    Origin = match.Origin,
                }
                : new ModEntry { Id = id, Name = id };

            found.LoadOrder = order;
            enabled.Add(found);
        }

        // Load order first, because that is the order the game works in. A mod with no recorded
        // position goes last rather than to the front, which is where a null would sort.
        return enabled
            .OrderBy(mod => mod.LoadOrder is null)
            .ThenBy(mod => mod.LoadOrder ?? 0)
            .ThenBy(mod => mod.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? BuildNote(OptionsRead options, bool checkedInstall)
    {
        if (!options.Read)
        {
            return options.Problem;
        }

        if (!checkedInstall)
        {
            return "The game folder was not read, so the mods are listed by id with no names or versions.";
        }

        return null;
    }

    /// <summary>True when the mods folder was there and could be listed.</summary>
    private static bool ReadInstallFolder(string? gameInstallPath, List<ModEntry> into)
    {
        string? mods = SafeCombine(gameInstallPath, ModsRelativePath);
        if (mods is null)
        {
            return false;
        }

        string[]? folders = ListDirectories(mods);
        if (folders is null)
        {
            return false;
        }

        foreach (string folder in folders)
        {
            string name = Path.GetFileName(folder);
            if (string.Equals(name, SkippedInstallFolder, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            into.Add(ReadMod(folder, name, ModEntry.InstallOrigin, workshopId: null));
        }

        return true;
    }

    /// <summary>True when the workshop content folder was there and could be listed.</summary>
    private static bool ReadWorkshopFolder(string? gameInstallPath, List<ModEntry> into)
    {
        string? content = WorkshopContentPath(gameInstallPath);
        if (content is null)
        {
            return false;
        }

        string[]? folders = ListDirectories(content);
        if (folders is null)
        {
            return false;
        }

        foreach (string folder in folders)
        {
            // The folder name is the workshop item id, which is the only place it is recorded.
            string workshopId = Path.GetFileName(folder);
            into.Add(ReadMod(folder, workshopId, ModEntry.WorkshopOrigin, workshopId));
        }

        return true;
    }

    /// <summary>A missing or unreadable modinfo.json leaves the folder name standing as both id and
    /// name, which is what the game falls back to as well.</summary>
    private static ModEntry ReadMod(string folder, string folderName, string origin, string? workshopId)
    {
        var mod = new ModEntry
        {
            Id = folderName,
            Name = folderName,
            FolderName = folderName,
            WorkshopId = workshopId,
            Origin = origin,
        };

        string? infoPath = SafeCombine(folder, ModInfoFileName);
        if (infoPath is null)
        {
            return mod;
        }

        string? text = ReadTextSafe(infoPath);
        if (text is null)
        {
            return mod;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(text, JsonOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return mod;
            }

            mod.Id = ReadString(document.RootElement, "id") ?? folderName;
            mod.Name = ReadString(document.RootElement, "name") ?? mod.Id;
            mod.Version = ReadString(document.RootElement, "version");
            mod.Requirements = ReadStringArray(document.RootElement, "requirements");
        }
        catch (JsonException)
        {
            // The game's own reader is more forgiving, and real mods rely on it: Push to Meow's Rain
            // Meadow add-on ships a modinfo.json with a comma missing and the game still loads it.
            ReadLoosely(text, folderName, mod);
        }

        return mod;
    }

    /// <summary>All three fields are plain strings at the top level of a flat object, so taking the
    /// quoted text after each key gets the same answer a parser would.</summary>
    private static void ReadLoosely(string text, string folderName, ModEntry mod)
    {
        mod.Id = LooseString(text, "id") ?? folderName;
        mod.Name = LooseString(text, "name") ?? mod.Id;
        mod.Version = LooseString(text, "version");
        mod.Requirements = LooseStringArray(text, "requirements");
    }

    private static string? LooseString(string text, string key)
    {
        int at = text.IndexOf('"' + key + '"', StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return null;
        }

        int colon = text.IndexOf(':', at + key.Length + 2);
        if (colon < 0)
        {
            return null;
        }

        int open = text.IndexOf('"', colon + 1);
        if (open < 0)
        {
            return null;
        }

        int close = text.IndexOf('"', open + 1);
        if (close < 0)
        {
            return null;
        }

        string value = text[(open + 1)..close].Trim();
        return value.Length == 0 ? null : value;
    }

    /// <summary>Empty for a missing key, a null, or anything that is not an array of strings.</summary>
    private static List<string> ReadStringArray(JsonElement root, string name)
    {
        var values = new List<string>();

        if (!root.TryGetProperty(name, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
        {
            return values;
        }

        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text
                && text.Trim() is { Length: > 0 } trimmed)
            {
                values.Add(trimmed);
            }
        }

        return values;
    }

    /// <summary>
    /// The requirements array off a file the strict parser refused. Read between the first bracket
    /// after the key and the one that closes it, which is enough because these hold plain strings
    /// and never a nested array.
    /// </summary>
    private static List<string> LooseStringArray(string text, string key)
    {
        var values = new List<string>();

        int at = text.IndexOf('"' + key + '"', StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return values;
        }

        int open = text.IndexOf('[', at + key.Length + 2);
        if (open < 0)
        {
            return values;
        }

        int close = text.IndexOf(']', open + 1);
        if (close < 0)
        {
            return values;
        }

        int cursor = open + 1;
        while (cursor < close)
        {
            int quote = text.IndexOf('"', cursor);
            if (quote < 0 || quote > close)
            {
                break;
            }

            int end = text.IndexOf('"', quote + 1);
            if (end < 0 || end > close)
            {
                break;
            }

            if (text[(quote + 1)..end].Trim() is { Length: > 0 } value)
            {
                values.Add(value);
            }

            cursor = end + 1;
        }

        return values;
    }

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    private static string? ReadGameVersion(string? gameInstallPath)
    {
        string? path = SafeCombine(gameInstallPath, GameVersionRelativePath);
        if (path is null)
        {
            return null;
        }

        string? text = ReadTextSafe(path);
        return text is null || text.Trim().Length == 0 ? null : text.Trim();
    }

    private static string[]? ListDirectories(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetDirectories(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReadTextSafe(string path)
    {
        try
        {
            // ReadAllText strips the BOM that some modinfo.json files carry.
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? SafeCombine(string? first, string second)
    {
        if (string.IsNullOrWhiteSpace(first))
        {
            return null;
        }

        try
        {
            return Path.Combine(first, second);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
