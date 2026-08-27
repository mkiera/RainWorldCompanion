// Usings sit above the namespace declaration on purpose. RainWorldCompanion.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
namespace RainWorldCompanion.Core.Mods;

/// <summary>
/// Finds the settings inside a mod's settings file that belong to one machine rather than to the
/// game.
///
/// <para>Most of a Remix settings file travels fine. Some of it does not: a camera mod keeps a
/// window size in the same file as its gameplay options, and taking that from somebody else pushes
/// their screen onto yours. Matching on what the keys are named rather than on which mod wrote
/// them means the next mod that does this is caught without anyone noticing it happened.</para>
///
/// <para>A note on a row, never a reason to refuse anything.</para>
/// </summary>
public static class ModConfigNotes
{
    /// <summary>
    /// Words that name something about the machine. Matched against the key alone: a value saying
    /// "resolution" is a value, and a mod is free to have one.
    /// </summary>
    private static readonly string[] MachineWords =
    [
        "resolution",
        "fullscreen",
        "screen",
        "display",
        "monitor",
        "vsync",
        "refreshrate",
        "windowed",
        "dpi",
    ];

    /// <summary>A settings file this size is not the key-and-value kind, so it is not read.</summary>
    private const long MaxBytes = 1024 * 1024;

    /// <summary>
    /// The keys that name something about the machine, in the order they appear. Empty when there
    /// are none and empty when the file cannot be read, because this only ever adds a sentence.
    /// </summary>
    public static IReadOnlyList<string> MachineSpecificKeys(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Array.Empty<string>();
        }

        string[] lines;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxBytes)
            {
                return Array.Empty<string>();
            }

            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Array.Empty<string>();
        }

        var found = new List<string>();

        foreach (string line in lines)
        {
            // The game's own parser: a line starting with # is a comment, and a line that does not
            // split in two is not a setting.
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            string[] parts = trimmed.Split('=', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            string key = parts[0].Trim();
            if (key.Length > 0 && NamesTheMachine(key) && !found.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                found.Add(key);
            }
        }

        return found;
    }

    private static bool NamesTheMachine(string key)
    {
        foreach (string word in MachineWords)
        {
            if (key.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
