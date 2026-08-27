// Usings sit above the namespace declaration on purpose. RainWorldCompanion.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
namespace RainWorldCompanion.Core.Mods;

/// <summary>
/// One mod settings file recorded with a save.
///
/// <para>A mutable class with initialisers rather than a record, and no enums anywhere, for the
/// reasons <see cref="ModEntry"/> gives: these are written into a manifest and read back by builds
/// older and newer than the one that wrote them.</para>
/// </summary>
public sealed class ModConfigFile
{
    /// <summary>
    /// Relative to the save folder, backslash separated, always starting with ModConfigs. Recorded
    /// in that shape rather than trimmed, because it is then the same thing
    /// <see cref="Backups.ManifestFileEntry.RelativePath"/> is: the scope can be asked about it and
    /// a destination can be resolved from it with nothing in between.
    /// </summary>
    public string RelativePath { get; set; } = "";

    /// <summary>
    /// The mod this was attributed to. It is the id from modinfo.json, which is what
    /// <see cref="ModEntry.Id"/> holds and what the game builds the file name from, so a recorded
    /// settings file joins to a recorded mod. Empty when it could not be worked out.
    /// </summary>
    public string ModId { get; set; } = "";

    public long SizeBytes { get; set; }

    public string Sha256 { get; set; } = "";
}

/// <summary>The files one mod owns, which is what a player picks by.</summary>
public sealed record ModConfigGroup(string ModId, IReadOnlyList<ModConfigFile> Files)
{
    public long TotalBytes => Files.Sum(file => file.SizeBytes);
}

/// <summary>
/// The mod settings that were in a save folder when a save was taken from it.
///
/// <para><see cref="ReadTheFolder"/> matters as much as the list, for the reason
/// <see cref="ModListSnapshot.ReadTheEnabledList"/> does: an empty list has to be able to mean
/// "there were none" or "we could not tell", and those must never be shown the same way.</para>
/// </summary>
public sealed class ModConfigSet
{
    private List<ModConfigFile> _files = new();

    /// <summary>False means <see cref="Files"/> says nothing at all, rather than saying there were
    /// no settings.</summary>
    public bool ReadTheFolder { get; set; }

    /// <summary>Plain sentence naming what could not be read or copied, null when nothing was
    /// short.</summary>
    public string? Note { get; set; }

    /// <summary>
    /// Never null, including after deserialisation: an explicit JSON null overwrites a field
    /// initialiser, and every reader walks this without a guard.
    /// </summary>
    public List<ModConfigFile> Files
    {
        get => _files;
        set => _files = value ?? new List<ModConfigFile>();
    }

    /// <summary>
    /// The files grouped by the mod they belong to, in id order. Computed rather than stored: a
    /// second copy of what <see cref="Files"/> already says could only disagree with it.
    /// </summary>
    public IReadOnlyList<ModConfigGroup> ByMod()
        => Files
            .GroupBy(file => file.ModId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ModConfigGroup(
                group.Key,
                group.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();
}
