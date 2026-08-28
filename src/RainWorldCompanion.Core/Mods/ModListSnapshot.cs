// RainWorldCompanion.Core.System exists in this assembly, so a using written inside the namespace
// body would bind "System" to that namespace instead of the BCL root.
namespace RainWorldCompanion.Core.Mods;

/// <summary>
/// A mutable class with initialisers rather than a record, so an absent JSON property lands on its
/// initialiser instead of failing to bind. <see cref="Origin"/> is text rather than an enum for the
/// same reason: JsonStringEnumConverter throws on a value it does not know, and that would take down
/// the read of the whole manifest rather than the one field.
/// </summary>
public sealed class ModEntry
{
    public const string InstallOrigin = "install";

    public const string WorkshopOrigin = "workshop";

    /// <summary>The id from modinfo.json, which is what the game's enabled list holds.</summary>
    public string Id { get; set; } = "";

    /// <summary>Display name, falling back to the id when modinfo.json gave none.</summary>
    public string Name { get; set; } = "";

    /// <summary>Null covers two cases: the mod ships without a version, and the install was never
    /// looked at.</summary>
    public string? Version { get; set; }

    /// <summary>The folder name under the workshop content folder, which is what a Steam Workshop
    /// link is built from. Null for a local mod.</summary>
    public string? WorkshopId { get; set; }

    /// <summary>The folder the mod sits in, which is not the id: Devourment ships in a folder called
    /// Devourment-mod. This is what enabledMods.txt names a local mod by. Null on a snapshot recorded
    /// before this was kept, and on a mod that was never found on disk.</summary>
    public string? FolderName { get; set; }

    /// <summary>Position in the game's load order, lower loading earlier. Null when unrecorded.</summary>
    public int? LoadOrder { get; set; }

    /// <summary><see cref="InstallOrigin"/>, <see cref="WorkshopOrigin"/>, or empty for a mod the
    /// game had turned on but that was not found anywhere on disk.</summary>
    public string Origin { get; set; } = "";

    /// <summary>
    /// The ids from modinfo.json's requirements array: the mods the game turns on with this one.
    /// Never null, including after deserialisation, for the reason every other collection here is
    /// guarded: an explicit JSON null defeats a plain field initialiser and readers walk this
    /// without a guard. Empty on a snapshot recorded before this was read, which reads the same as
    /// a mod that requires nothing, and that is why nothing warns from its being empty.
    /// </summary>
    public List<string> Requirements
    {
        get => _requirements;
        set => _requirements = value ?? new List<string>();
    }

    private List<string> _requirements = new();
}

/// <summary>The three "did we look" flags matter as much as the list, because an empty list has to
/// be able to mean "nothing was on" or "we could not tell".</summary>
public sealed class ModListSnapshot
{
    private List<ModEntry> _mods = new();

    /// <summary>The game version, such as v1.11.8. Null when neither source could be read.</summary>
    public string? GameVersion { get; set; }

    /// <summary>False means <see cref="Mods"/> says nothing at all, rather than no mods were on.</summary>
    public bool ReadTheEnabledList { get; set; }

    /// <summary>False means names, versions and origins were never resolved, so every entry here is
    /// an id and nothing more.</summary>
    public bool CheckedTheInstall { get; set; }

    public bool CheckedTheWorkshop { get; set; }

    public string? Note { get; set; }

    /// <summary>In load order. Never null, including after deserialisation: an explicit JSON null
    /// overwrites a field initialiser, and every reader walks this without a guard.</summary>
    public List<ModEntry> Mods
    {
        get => _mods;
        set => _mods = value ?? new List<ModEntry>();
    }
}
