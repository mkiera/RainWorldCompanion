// Usings sit above the namespace declaration on purpose. RainWorldCompanion.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
namespace RainWorldCompanion.Core.Mods;

/// <summary>
/// How one mod's recorded settings stand against the ones in the save folder now. Not persisted
/// anywhere, so unlike everything else in this folder it is free to be an enum.
/// </summary>
public enum ModConfigMatch
{
    /// <summary>The folder could not be read, or a hash is missing, so no claim is made.</summary>
    Unknown,

    /// <summary>Nothing here for this mod, so taking these adds settings rather than replacing any.</summary>
    New,

    /// <summary>Byte for byte what is already there. Taking these changes nothing.</summary>
    Same,

    /// <summary>Present and different, so taking these replaces what is there.</summary>
    Different,
}

public static class ModConfigMatching
{
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Compared by digest rather than by size or time. A settings file is rewritten whole every
    /// time the game closes its options menu, so its timestamp moves without a value changing, and
    /// two different tunings of the same mod are routinely the same length.
    /// </summary>
    public static ModConfigMatch For(ModConfigGroup recorded, ModConfigSet? live)
    {
        ArgumentNullException.ThrowIfNull(recorded);

        if (live is not { ReadTheFolder: true })
        {
            return ModConfigMatch.Unknown;
        }

        var here = new Dictionary<string, string>(Paths);

        foreach (var file in live.Files)
        {
            if (Paths.Equals(file.ModId, recorded.ModId))
            {
                here[file.RelativePath] = file.Sha256;
            }
        }

        if (here.Count == 0)
        {
            return ModConfigMatch.New;
        }

        // A file on one side and not the other is a difference, so the counts have to agree before
        // the digests are worth comparing.
        if (here.Count != recorded.Files.Count)
        {
            return ModConfigMatch.Different;
        }

        foreach (var file in recorded.Files)
        {
            // An unhashed side cannot be called the same as anything. This is what a snapshot
            // written before digests were recorded reads back as.
            if (file.Sha256.Length == 0)
            {
                return ModConfigMatch.Unknown;
            }

            if (!here.TryGetValue(file.RelativePath, out var mine) || mine.Length == 0)
            {
                return here.ContainsKey(file.RelativePath)
                    ? ModConfigMatch.Unknown
                    : ModConfigMatch.Different;
            }

            if (!Paths.Equals(mine, file.Sha256))
            {
                return ModConfigMatch.Different;
            }
        }

        return ModConfigMatch.Same;
    }

    /// <summary>The phrase a row wears. Empty for <see cref="ModConfigMatch.Unknown"/>, which is a
    /// state to say nothing about rather than one to label.</summary>
    public static string Describe(ModConfigMatch match) => match switch
    {
        ModConfigMatch.Same => "Same as yours",
        ModConfigMatch.New => "New to you",
        ModConfigMatch.Different => "Different from yours",
        _ => "",
    };
}
