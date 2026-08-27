// RainWorldCompanion.Core.System exists in this assembly, so a using written inside the namespace
// body would bind "System" to that namespace instead of the BCL root.
using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Core.Mods;

/// <param name="Read">False means the other fields say nothing. An empty <see cref="EnabledModIds"/>
/// with this true is a real answer: a vanilla install writes no EnabledMods key at all.</param>
/// <param name="EnabledModIds">Mod ids in the order the game wrote them, which is not load order.</param>
/// <param name="LoadOrder">Id to load order position, lower loading earlier. Returned as the game
/// holds it, so it still carries entries for mods that have since been turned off.</param>
public sealed record OptionsRead(
    bool Read,
    string? Problem,
    IReadOnlyList<string> EnabledModIds,
    IReadOnlyDictionary<string, int> LoadOrder,
    string? LastGameVersion)
{
    public static OptionsRead Failed(string problem) => new(
        false,
        problem,
        Array.Empty<string>(),
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
        null);
}

/// <summary>
/// The options file is a save container like the sav files, and its "options" entry holds a flat
/// stream of settings: records end at <c>&lt;optA&gt;</c>, a record splits into key and value at
/// <c>&lt;optB&gt;</c>, a list value splits on <c>&lt;optC&gt;</c>, and a pair inside a list splits
/// at <c>&lt;optD&gt;</c>. This is the only place that says which mods are on: enabledMods.txt in
/// the game folder holds only the mods that carry a DLL, with no ids or versions.
/// </summary>
public static class OptionsFile
{
    /// <summary>The file, which sits in the save folder beside the sav files.</summary>
    public const string FileName = "options";

    public const string ContainerKey = "options";

    public const string EnabledModsKey = "EnabledMods";

    /// <summary>Key of the load order, which outlives the mods it names.</summary>
    public const string ModLoadOrderKey = "ModLoadOrder";

    public const string LastGameVersionKey = "LastGameVersion";

    /// <summary>Internal rather than private because <see cref="OptionsWriter"/> splices the same
    /// stream these split, and the two must never drift apart.</summary>
    internal const string RecordSeparator = "<optA>";

    internal const string KeyValueSeparator = "<optB>";

    internal const string ListSeparator = "<optC>";

    internal const string PairSeparator = "<optD>";

    /// <summary>Never throws: a missing or damaged options file costs the answer, not the caller.</summary>
    public static OptionsRead Read(string? saveRoot)
    {
        if (string.IsNullOrWhiteSpace(saveRoot))
        {
            return OptionsRead.Failed("The save folder is not known, so which mods are on could not be read.");
        }

        string path;
        try
        {
            path = Path.Combine(saveRoot, FileName);
        }
        catch (ArgumentException)
        {
            return OptionsRead.Failed("The save folder path is not usable, so which mods are on could not be read.");
        }

        return ReadFile(path);
    }

    /// <summary>Reads one options file by path. Used to check a copy staged beside the real one
    /// before it replaces it, which is why it takes a file rather than the folder holding it.</summary>
    public static OptionsRead ReadFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return OptionsRead.Failed("No options file path was given.");
        }

        if (!FileExistsSafe(path))
        {
            return OptionsRead.Failed("The save folder holds no options file, so which mods are on could not be read.");
        }

        if (!SaveContainer.TryRead(path, out SaveContainer? container, out string? error) || container is null)
        {
            return OptionsRead.Failed("The options file could not be read: " + error);
        }

        // A damaged hashtable pairs keys with values by index, so the value sitting under "options"
        // may belong to some other key entirely.
        if (container.StructureProblem is not null)
        {
            return OptionsRead.Failed("The options file is damaged: " + container.StructureProblem + ".");
        }

        if (!container.Entries.TryGetValue(ContainerKey, out string? blob) || blob is null)
        {
            return OptionsRead.Failed("The options file holds no settings entry, so which mods are on could not be read.");
        }

        return Parse(blob);
    }

    /// <summary>Splits an already-decoded settings stream. Exposed for tests.</summary>
    internal static OptionsRead Parse(string blob)
    {
        var enabled = new List<string>();
        var loadOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string? gameVersion = null;

        bool sawEnabled = false;
        bool sawLoadOrder = false;
        bool sawGameVersion = false;

        foreach (string record in blob.Split(RecordSeparator, StringSplitOptions.None))
        {
            if (record.Length == 0)
            {
                continue;
            }

            int split = record.IndexOf(KeyValueSeparator, StringComparison.Ordinal);
            if (split < 0)
            {
                continue;
            }

            string key = record[..split];
            string value = record[(split + KeyValueSeparator.Length)..];

            // Keys repeat in this file: InputSetup is written once per player. The first wins.
            switch (key)
            {
                case EnabledModsKey when !sawEnabled:
                    sawEnabled = true;
                    ReadIds(value, enabled);
                    break;

                case ModLoadOrderKey when !sawLoadOrder:
                    sawLoadOrder = true;
                    ReadLoadOrder(value, loadOrder);
                    break;

                case LastGameVersionKey when !sawGameVersion:
                    sawGameVersion = true;
                    gameVersion = value.Trim().Length == 0 ? null : value.Trim();
                    break;
            }
        }

        // No EnabledMods key means a vanilla install: the game omits it rather than writing an empty
        // one, so that is an answer rather than a failure to find one.
        return new OptionsRead(true, null, enabled, loadOrder, gameVersion);
    }

    private static void ReadIds(string value, List<string> into)
    {
        foreach (string raw in value.Split(ListSeparator, StringSplitOptions.None))
        {
            string id = raw.Trim();
            if (id.Length > 0)
            {
                into.Add(id);
            }
        }
    }

    private static void ReadLoadOrder(string value, Dictionary<string, int> into)
    {
        foreach (string raw in value.Split(ListSeparator, StringSplitOptions.None))
        {
            int split = raw.IndexOf(PairSeparator, StringComparison.Ordinal);
            if (split < 0)
            {
                continue;
            }

            string id = raw[..split].Trim();
            string position = raw[(split + PairSeparator.Length)..].Trim();

            // A position that will not parse costs that one mod its place in the order.
            if (id.Length > 0 && int.TryParse(position, out int order))
            {
                into.TryAdd(id, order);
            }
        }
    }

    private static bool FileExistsSafe(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
