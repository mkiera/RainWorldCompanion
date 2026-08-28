// RainWorldCompanion.Core.System exists in this assembly, so a using written inside the namespace
// body would bind "System" to that namespace instead of the BCL root.
using System.Text.Json;
using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.Core.Mods;
using RainWorldCompanion.Core.Saves.Models;

namespace RainWorldCompanion.Core.Library;

public enum LibraryEntryKind
{
    /// <summary>A whole slot file, byte for byte as the game wrote it.</summary>
    WholeSlot,

    /// <summary>One campaign taken out of a slot, with the map discovery that belongs to it.</summary>
    Campaign,
}

/// <summary>Every check compares the bytes on disk against <see cref="Sha256"/> rather than against
/// another copy of themselves, so a damaged entry cannot certify itself sound.</summary>
public sealed class LibraryManifest
{
    /// <summary>A version one manifest carries no kind and reads back as a whole slot, which is what
    /// every one of them is.</summary>
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public LibraryEntryKind Kind { get; set; } = LibraryEntryKind.WholeSlot;

    /// <summary>Null for a whole slot.</summary>
    public string? CampaignSlugcatId { get; set; }

    public string Name { get; set; } = "";

    public string? Note { get; set; }

    public DateTime CreatedUtc { get; set; }

    /// <summary>When the save bytes were last replaced, by an update or by undoing one. Null while
    /// the entry still holds what it was first stored with.</summary>
    public DateTime? UpdatedUtc { get; set; }

    public string AppVersion { get; set; } = "";

    /// <summary>The file this was stored from, for display. Empty for an unnamed import.</summary>
    public string SourceFileName { get; set; } = "";

    public SaveRealm SourceRealm { get; set; }

    /// <summary>The slot it was stored from, or 0 when an import's file name named no slot.</summary>
    public int SourceSlot { get; set; }

    public long SizeBytes { get; set; }

    public string Sha256 { get; set; } = "";

    /// <summary>Parsed once when the entry was written, so selecting a row costs no disk read. Null
    /// when the save could not be parsed at all.</summary>
    public SlotMetadata? Metadata { get; set; }

    /// <summary>The slot holding these same bytes. At most one entry names a given slot: whichever
    /// entry last put its bytes there owns the link and everyone else's is cleared, so two rows can
    /// never both claim to be in sav.</summary>
    public SaveRealm? LastLoadedRealm { get; set; }

    public int? LastLoadedSlot { get; set; }

    public DateTime? LastLoadedUtc { get; set; }

    /// <summary>The size and write time of the slot file at the moment the two matched. Comparing
    /// them against the file today is a hint on a row rather than a check, hence a stamp not a hash.</summary>
    public long? LastLoadedSizeBytes { get; set; }

    public DateTime? LastLoadedWriteUtc { get; set; }

    /// <summary>Describes save.previous.bin. Null until the first update replaced the save.</summary>
    public string? PreviousSha256 { get; set; }

    public long? PreviousSizeBytes { get; set; }

    public DateTime? PreviousReplacedUtc { get; set; }

    public SlotMetadata? PreviousMetadata { get; set; }

    /// <summary>The mods that were on when these bytes were taken, and the game version they ran
    /// under. Null when nothing was recorded, which covers entries stored before this existed and
    /// bare files that arrived with nothing but their bytes. Null never means "no mods".</summary>
    public ModListSnapshot? Mods { get; set; }

    /// <summary>Follows <see cref="PreviousMetadata"/> exactly: set on update, put back on undo,
    /// cleared on import.</summary>
    public ModListSnapshot? PreviousMods { get; set; }

    /// <summary>The mod settings that were in the save folder when these bytes were taken, kept in
    /// the entry's configs folder. Null when nothing was recorded, which covers entries stored
    /// before this existed and bare files that arrived with nothing but their bytes.</summary>
    public ModConfigSet? Configs { get; set; }

    /// <summary>Follows <see cref="PreviousMods"/> exactly.</summary>
    public ModConfigSet? PreviousConfigs { get; set; }

    public SaveSlotRef? LastLoadedSlotRef =>
        LastLoadedRealm is { } realm && LastLoadedSlot is { } slot
            ? new SaveSlotRef(realm, slot)
            : null;

    /// <summary>Null for an import whose file name named no slot.</summary>
    public SaveSlotRef? SourceSlotRef =>
        new SaveSlotRef(SourceRealm, SourceSlot) is { IsRealSlot: true } slot ? slot : null;
}

/// <summary>
/// One stored save on disk. The manifest is written last, so a folder without one is an entry that
/// did not finish: still listed, but not loadable. Folder names are timestamps and the user's name
/// lives only in the manifest, which keeps reserved names and case collisions off the filesystem and
/// makes a rename a manifest rewrite rather than a folder move.
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

    /// <summary>Holds the mod settings, laid out under it the way they sit under ModConfigs.</summary>
    public const string ConfigsFolderName = "configs";

    public const string PreviousConfigsFolderName = "configs.previous";

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

    /// <summary>When the bytes in this entry were last written, which is the time a row shows.</summary>
    public DateTime ModifiedUtc => Manifest?.UpdatedUtc ?? CreatedUtc;

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

    public bool IsCampaign => Manifest?.Kind == LibraryEntryKind.Campaign;

    public string ManifestPath => Path.Combine(DirectoryPath, ManifestFileName);

    public string SavePath => Path.Combine(DirectoryPath, SaveFileName);

    public string CampaignPath => Path.Combine(DirectoryPath, CampaignFileName);

    /// <summary>Everything that hashes, copies, exports or verifies an entry goes through this.</summary>
    public string ContentPath => IsCampaign ? CampaignPath : SavePath;

    public string ContentFileName => IsCampaign ? CampaignFileName : SaveFileName;

    public string PreviousSavePath => Path.Combine(DirectoryPath, PreviousSaveFileName);

    public string PreviousContentPath => Path.Combine(
        DirectoryPath,
        IsCampaign ? PreviousCampaignFileName : PreviousSaveFileName);

    /// <summary>Whether an update can be undone, which needs both the file and its recorded hash.</summary>
    public bool HasPrevious =>
        Manifest?.PreviousSha256 is { Length: > 0 } && File.Exists(PreviousContentPath);

    public string ConfigsPath => Path.Combine(DirectoryPath, ConfigsFolderName);

    public string PreviousConfigsPath => Path.Combine(DirectoryPath, PreviousConfigsFolderName);

    public bool HasConfigs => Manifest?.Configs is { Files.Count: > 0 };

    /// <summary>Never throws: a folder that cannot be read comes back with a Problem set.</summary>
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
