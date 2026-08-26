// Usings sit above the namespace declaration on purpose. RainWorldCompanion.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
namespace RainWorldCompanion.Core.Mods;

/// <summary>
/// One mod as it stood when a snapshot was taken.
///
/// <para>A mutable class with initialisers rather than a record, because these are written into
/// manifest.json and read back by builds that may be older or newer than the one that wrote
/// them. An absent JSON property then lands on its initialiser instead of failing to bind.</para>
///
/// <para><see cref="Origin"/> is text rather than an enum on purpose. An enum serialises through
/// JsonStringEnumConverter, which throws on a value it does not know, and that throw would take
/// down the read of the whole manifest rather than the one field. A build from before some
/// future origin existed has to be able to read a manifest that names it.</para>
/// </summary>
public sealed class ModEntry
{
    /// <summary>Origin for a mod under the game's own mods folder.</summary>
    public const string InstallOrigin = "install";

    /// <summary>Origin for a mod under the Steam workshop content folder.</summary>
    public const string WorkshopOrigin = "workshop";

    /// <summary>The id from modinfo.json, which is what the game's enabled list holds.</summary>
    public string Id { get; set; } = "";

    /// <summary>Display name, falling back to the id when modinfo.json gave none.</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Version from modinfo.json. Null covers two cases that both mean "no version to compare":
    /// the mod ships without one, and the install was never looked at.
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// The workshop item id, which is the folder name under the workshop content folder. Null for
    /// a local mod. This is what a Steam Workshop link is built from, so it is worth recording
    /// even though the id alone identifies the mod.
    /// </summary>
    public string? WorkshopId { get; set; }

    /// <summary>Position in the game's load order, lower loading earlier. Null when unrecorded.</summary>
    public int? LoadOrder { get; set; }

    /// <summary>
    /// <see cref="InstallOrigin"/>, <see cref="WorkshopOrigin"/>, or empty for a mod that the
    /// game had turned on but that was not found anywhere on disk.
    /// </summary>
    public string Origin { get; set; } = "";
}

/// <summary>
/// The mods that were turned on at one moment, with the game version they ran under.
///
/// <para>Stored on a backup manifest and a library entry so a load months later can say how the
/// machine has moved since. The three "did we look" flags matter as much as the list: the game
/// folder is optional throughout this app, so an empty list has to be able to mean "nothing was
/// on" or "we could not tell", and those two must never be shown the same way.</para>
/// </summary>
public sealed class ModListSnapshot
{
    private List<ModEntry> _mods = new();

    /// <summary>The game version, such as v1.11.8. Null when neither source could be read.</summary>
    public string? GameVersion { get; set; }

    /// <summary>
    /// False means <see cref="Mods"/> says nothing at all, rather than saying no mods were on.
    /// </summary>
    public bool ReadTheEnabledList { get; set; }

    /// <summary>
    /// False means names, versions and origins were never resolved, so every entry here is an id
    /// and nothing more.
    /// </summary>
    public bool CheckedTheInstall { get; set; }

    /// <summary>False means workshop mods were not resolved, so no workshop ids were recorded.</summary>
    public bool CheckedTheWorkshop { get; set; }

    /// <summary>Plain sentence naming what could not be read, for the panel and for support.</summary>
    public string? Note { get; set; }

    /// <summary>
    /// The mods that were on, in load order. Never null, including after deserialisation: an
    /// explicit JSON null overwrites a field initialiser, and every reader walks this without a
    /// guard.
    /// </summary>
    public List<ModEntry> Mods
    {
        get => _mods;
        set => _mods = value ?? new List<ModEntry>();
    }
}
