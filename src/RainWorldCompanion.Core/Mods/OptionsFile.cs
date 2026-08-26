// Usings sit above the namespace declaration on purpose. RainWorldCompanion.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Core.Mods;

/// <summary>
/// What one read of the game's options file found about mods.
/// </summary>
/// <param name="Read">
/// False means the three fields below say nothing. An empty <see cref="EnabledModIds"/> with this
/// true is a real answer: a vanilla install writes no EnabledMods key at all.
/// </param>
/// <param name="Problem">Plain sentence naming why not, null when the read worked.</param>
/// <param name="EnabledModIds">Mod ids in the order the game wrote them, which is not load order.</param>
/// <param name="LoadOrder">
/// Id to load order position, lower loading earlier. Returned as the game holds it, which means
/// it still carries entries for mods that have since been turned off. Callers that want the load
/// order of what is on filter this against <see cref="EnabledModIds"/>.
/// </param>
/// <param name="LastGameVersion">
/// The game version the options file was last written under. Null when absent or blank.
/// </param>
public sealed record OptionsRead(
    bool Read,
    string? Problem,
    IReadOnlyList<string> EnabledModIds,
    IReadOnlyDictionary<string, int> LoadOrder,
    string? LastGameVersion)
{
    /// <summary>What to use when nothing could be read.</summary>
    public static OptionsRead Failed(string problem) => new(
        false,
        problem,
        Array.Empty<string>(),
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
        null);
}

/// <summary>
/// Reads which mods the game has turned on, from the options file in the save folder.
///
/// <para>The options file is a save container like the sav files, so the outer layer comes from
/// <see cref="SaveContainer"/>. Its "options" entry holds a flat stream of settings in a grammar
/// of its own: records end at <c>&lt;optA&gt;</c>, a record splits into key and value at
/// <c>&lt;optB&gt;</c>, a list value splits on <c>&lt;optC&gt;</c>, and a pair inside a list
/// splits at <c>&lt;optD&gt;</c>.</para>
///
/// <para>This is the only place that says which mods are on. The enabledMods.txt file in the game
/// folder looks like an answer but holds only the mods that carry a DLL, so the six that ship
/// with the game are missing from it, and it records no ids or versions.</para>
///
/// <para>Nothing here is written. The app never edits the game's own files.</para>
/// </summary>
public static class OptionsFile
{
    /// <summary>The file, which sits in the save folder beside the sav files.</summary>
    public const string FileName = "options";

    /// <summary>The container entry holding the settings stream.</summary>
    public const string ContainerKey = "options";

    /// <summary>Key of the list of mods that are turned on.</summary>
    public const string EnabledModsKey = "EnabledMods";

    /// <summary>Key of the load order, which outlives the mods it names.</summary>
    public const string ModLoadOrderKey = "ModLoadOrder";

    /// <summary>Key of the game version the file was last written under.</summary>
    public const string LastGameVersionKey = "LastGameVersion";

    private const string RecordSeparator = "<optA>";
    private const string KeyValueSeparator = "<optB>";
    private const string ListSeparator = "<optC>";
    private const string PairSeparator = "<optD>";

    /// <summary>
    /// Never throws. A missing or damaged options file costs the answer rather than the caller.
    /// </summary>
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

        if (!FileExistsSafe(path))
        {
            return OptionsRead.Failed("The save folder holds no options file, so which mods are on could not be read.");
        }

        if (!SaveContainer.TryRead(path, out SaveContainer? container, out string? error) || container is null)
        {
            return OptionsRead.Failed("The options file could not be read: " + error);
        }

        // A damaged hashtable pairs keys with values by index, so the value sitting under
        // "options" may belong to some other key entirely. Reading mods out of that would
        // invent an answer rather than fail to find one.
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

            // Keys repeat in this file: InputSetup is written once per player. The first
            // occurrence wins so a later one cannot quietly replace what was already read.
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

        // No EnabledMods key means a vanilla install: the game omits the key rather than
        // writing an empty one. That is an answer, not a failure to find one.
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

            // A position that will not parse costs that one mod its place in the order. The
            // rest of the list is still worth having.
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
