// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Text.Json;
using RainWorldSaveManager.Core.Backups;
using RainWorldSaveManager.Core.Saves;
using RainWorldSaveManager.Core.Saves.Models;

namespace RainWorldSaveManager.Core.Library;

/// <summary>What one library entry holds.</summary>
public enum LibraryEntryKind
{
    /// <summary>A whole slot file, byte for byte as the game wrote it.</summary>
    WholeSlot,

    /// <summary>One campaign taken out of a slot, with the map discovery that belongs to it.</summary>
    Campaign,
}

/// <summary>
/// What entry.json holds: everything needed to list, describe and check one stored save without
/// opening the save itself.
///
/// <see cref="Sha256"/> is the anchor. Every later check compares the bytes on disk against it
/// rather than against another copy of themselves, which is the same rule the backup manifests
/// follow and the reason a damaged entry cannot certify itself sound.
/// </summary>
public sealed class LibraryManifest
{
    /// <summary>
    /// Two, because an entry can now hold one campaign rather than a whole slot.
    ///
    /// A version one manifest carries no kind at all and reads back as a whole slot, which is what
    /// every one of them is. Going the other way, a version of this app that predates campaign
    /// entries skips the key it does not know and then finds no save.bin, so such an entry lists
    /// with a problem rather than being loaded as something it is not.
    /// </summary>
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Whether this is a whole slot or one campaign out of one.</summary>
    public LibraryEntryKind Kind { get; set; } = LibraryEntryKind.WholeSlot;

    /// <summary>Which slugcat the stored campaign belongs to. Null for a whole slot.</summary>
    public string? CampaignSlugcatId { get; set; }

    public string Name { get; set; } = "";

    public string? Note { get; set; }

    public DateTime CreatedUtc { get; set; }

    /// <summary>
    /// When the save bytes were last replaced, by an update or by undoing one. Null while the entry
    /// still holds what it was first stored with, which is why a row falls back to
    /// <see cref="CreatedUtc"/> rather than showing a blank.
    /// </summary>
    public DateTime? UpdatedUtc { get; set; }

    public string AppVersion { get; set; } = "";

    /// <summary>The file this was stored from, for display. Empty for an unnamed import.</summary>
    public string SourceFileName { get; set; } = "";

    public SaveRealm SourceRealm { get; set; }

    /// <summary>The slot it was stored from, or 0 when an import's file name named no slot.</summary>
    public int SourceSlot { get; set; }

    public long SizeBytes { get; set; }

    public string Sha256 { get; set; } = "";

    /// <summary>
    /// The campaigns in the stored save, parsed once when the entry was written so that selecting
    /// a row costs no disk read. Null when the save could not be parsed at all.
    /// </summary>
    public SlotMetadata? Metadata { get; set; }

    /// <summary>
    /// The slot holding these same bytes, so an update knows which slot to offer and a row can say
    /// where the save went.
    ///
    /// A load sets it, and so does an update, because either way the entry and that slot file are
    /// byte for byte the same afterwards. At most one entry names a given slot: whichever entry
    /// last put its bytes there owns the link, and everyone else's is cleared, so two rows can never
    /// both claim to be in sav.
    /// </summary>
    public SaveRealm? LastLoadedRealm { get; set; }

    public int? LastLoadedSlot { get; set; }

    /// <summary>When the entry and the slot were last made to match.</summary>
    public DateTime? LastLoadedUtc { get; set; }

    /// <summary>
    /// The size and write time of the slot file at the moment the two matched. Comparing these
    /// against the file today says whether the slot has been played since, which is a hint on a row
    /// rather than a check, so a stamp rather than a hash is the right cost.
    /// </summary>
    public long? LastLoadedSizeBytes { get; set; }

    public DateTime? LastLoadedWriteUtc { get; set; }

    /// <summary>Describes save.previous.bin. Null until the first update replaced the save.</summary>
    public string? PreviousSha256 { get; set; }

    public long? PreviousSizeBytes { get; set; }

    public DateTime? PreviousReplacedUtc { get; set; }

    public SlotMetadata? PreviousMetadata { get; set; }

    /// <summary>The slot the last load wrote to, or null when this has never been loaded.</summary>
    public SaveSlotRef? LastLoadedSlotRef =>
        LastLoadedRealm is { } realm && LastLoadedSlot is { } slot
            ? new SaveSlotRef(realm, slot)
            : null;

    /// <summary>
    /// The slot this was taken from, or null for an import whose file name named no slot. A save
    /// that has never been loaded still knows where it came from, which is the slot worth offering
    /// when there is no load on record.
    /// </summary>
    public SaveSlotRef? SourceSlotRef =>
        new SaveSlotRef(SourceRealm, SourceSlot) is { IsRealSlot: true } slot ? slot : null;
}

/// <summary>
/// One stored save on disk: a folder holding the save bytes and the manifest that describes them.
///
/// The manifest is written last, so a folder without one is an entry that did not finish. Such a
/// folder is still listed, because a half-written save is evidence of what happened, but it cannot
/// be loaded, renamed or exported.
///
/// Folder names are timestamps rather than the user's chosen name. That name lives only in the
/// manifest, which keeps reserved names, invalid characters and case collisions out of the
/// filesystem entirely and makes a rename a manifest rewrite rather than a folder move.
/// </summary>
public sealed class LibraryEntry
{
    public const string ManifestFileName = "entry.json";

    public const string SaveFileName = "save.bin";

    /// <summary>What an entry holding one campaign keeps it in, in place of save.bin.</summary>
    public const string CampaignFileName = "campaign.bin";

    /// <summary>What an update moves the save it is replacing to. One generation, no more.</summary>
    public const string PreviousSaveFileName = "save.previous.bin";

    public const string PreviousCampaignFileName = "campaign.previous.bin";

    /// <summary>Claims an entry folder while it is being written.</summary>
    internal const string ClaimFileName = ".creating";

    private LibraryEntry(string directoryPath, LibraryManifest? manifest, string? problem, DateTime createdUtc)
    {
        DirectoryPath = directoryPath;
        Manifest = manifest;
        Problem = problem;
        CreatedUtc = createdUtc;
        Id = Path.GetFileName(directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    /// <summary>The folder name, which is what identifies an entry across a refresh.</summary>
    public string Id { get; }

    public string DirectoryPath { get; }

    public LibraryManifest? Manifest { get; }

    /// <summary>Why the entry is unusable, or null when it is fine.</summary>
    public string? Problem { get; }

    public bool IsComplete => Manifest is not null;

    public DateTime CreatedUtc { get; }

    /// <summary>
    /// When the bytes in this entry were last written, which is the time a row shows. An entry that
    /// has never been updated still holds what it was stored with, so that is its own store time.
    /// </summary>
    public DateTime ModifiedUtc => Manifest?.UpdatedUtc ?? CreatedUtc;

    /// <summary>Whether an update has replaced the bytes this was first stored with.</summary>
    public bool WasUpdated => Manifest?.UpdatedUtc is not null;

    /// <summary>The user's name for it, falling back to the folder name for a broken entry.</summary>
    public string Name
    {
        get
        {
            var name = Manifest?.Name;
            return string.IsNullOrWhiteSpace(name) ? Id : name.Trim();
        }
    }

    /// <summary>True when this entry holds one campaign rather than a whole slot.</summary>
    public bool IsCampaign => Manifest?.Kind == LibraryEntryKind.Campaign;

    public string ManifestPath => Path.Combine(DirectoryPath, ManifestFileName);

    public string SavePath => Path.Combine(DirectoryPath, SaveFileName);

    public string CampaignPath => Path.Combine(DirectoryPath, CampaignFileName);

    /// <summary>
    /// The file this entry is about, whichever of the two it holds. Everything that hashes, copies,
    /// exports or verifies an entry goes through this rather than through the kind.
    /// </summary>
    public string ContentPath => IsCampaign ? CampaignPath : SavePath;

    public string ContentFileName => IsCampaign ? CampaignFileName : SaveFileName;

    public string PreviousSavePath => Path.Combine(DirectoryPath, PreviousSaveFileName);

    public string PreviousContentPath => Path.Combine(
        DirectoryPath,
        IsCampaign ? PreviousCampaignFileName : PreviousSaveFileName);

    /// <summary>Whether an update can be undone, which needs both the file and its recorded hash.</summary>
    public bool HasPrevious =>
        Manifest?.PreviousSha256 is { Length: > 0 } && File.Exists(PreviousContentPath);

    /// <summary>
    /// Reads one entry folder. Never throws: a folder that cannot be read comes back with a
    /// Problem, because a library with one bad entry in it still has to list the rest.
    /// </summary>
    public static LibraryEntry Load(string directoryPath)
    {
        var full = Path.GetFullPath(directoryPath);
        var created = ReadCreatedTime(full);

        var manifestPath = Path.Combine(full, ManifestFileName);

        if (!File.Exists(manifestPath))
        {
            return new LibraryEntry(full, null, "entry.json is missing, so this save did not finish being stored", created);
        }

        LibraryManifest? manifest;
        try
        {
            var json = File.ReadAllText(manifestPath);
            manifest = JsonSerializer.Deserialize<LibraryManifest>(json, BackupJson.Options);
        }
        catch (Exception ex)
        {
            return new LibraryEntry(full, null, "entry.json could not be read: " + ex.Message, created);
        }

        if (manifest is null)
        {
            return new LibraryEntry(full, null, "entry.json is empty", created);
        }

        string contentFileName = manifest.Kind == LibraryEntryKind.Campaign ? CampaignFileName : SaveFileName;

        if (!File.Exists(Path.Combine(full, contentFileName)))
        {
            return new LibraryEntry(full, null, contentFileName + " is missing", created);
        }

        return new LibraryEntry(
            full,
            manifest,
            null,
            manifest.CreatedUtc == default ? created : manifest.CreatedUtc);
    }

    private static DateTime ReadCreatedTime(string directoryPath)
    {
        try
        {
            return Directory.Exists(directoryPath)
                ? Directory.GetCreationTimeUtc(directoryPath)
                : DateTime.UtcNow;
        }
        catch (Exception)
        {
            return DateTime.UtcNow;
        }
    }
}
