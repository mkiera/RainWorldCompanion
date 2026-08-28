using System.Text.Json;

using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Library;
using RainWorldCompanion.Core.Mods;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.Core.Saves.Models;

namespace RainWorldCompanion.Tests;

/// <summary>
/// Mod settings were added to an entry without moving the schema version, so builds on either side
/// of the change have to read each other's manifests. These tests prove both directions.
/// </summary>
public class ModConfigCompatibilityTests
{
    private static readonly SaveSlotRef Slot1 = new(SaveRealm.Local, 1);

    /// <summary>
    /// The manifest shape before settings were kept. Deserialising a current file into this is what
    /// an older build does.
    /// </summary>
    private sealed class ManifestBeforeConfigs
    {
        public int SchemaVersion { get; set; }

        public LibraryEntryKind Kind { get; set; }

        public string? CampaignSlugcatId { get; set; }

        public string Name { get; set; } = "";

        public string? Note { get; set; }

        public DateTime CreatedUtc { get; set; }

        public DateTime? UpdatedUtc { get; set; }

        public string AppVersion { get; set; } = "";

        public string SourceFileName { get; set; } = "";

        public SaveRealm SourceRealm { get; set; }

        public int SourceSlot { get; set; }

        public long SizeBytes { get; set; }

        public string Sha256 { get; set; } = "";

        public SlotMetadata? Metadata { get; set; }

        public ModListSnapshot? Mods { get; set; }
    }

    [Fact]
    public void Keeping_mod_settings_does_not_move_the_schema_version()
    {
        using var world = new LibraryWorld();

        LibraryEntry entry = world.Library.StoreSlot(Slot1, "a save", null);

        Assert.Equal(2, entry.Manifest!.SchemaVersion);
        Assert.NotNull(entry.Manifest.Configs);
    }

    /// <summary>
    /// Unknown members are skipped, and a skipped subtree is never bound, so nothing inside the
    /// settings block can throw here.
    /// </summary>
    [Fact]
    public void A_build_that_predates_mod_settings_reads_an_entry_that_has_them()
    {
        using var world = new LibraryWorld();
        LibraryEntry entry = world.Library.StoreSlot(Slot1, "a save", "a note");

        var old = JsonSerializer.Deserialize<ManifestBeforeConfigs>(
            File.ReadAllText(entry.ManifestPath),
            BackupJson.Options);

        Assert.NotNull(old);
        Assert.Equal(2, old!.SchemaVersion);
        Assert.Equal("a save", old.Name);
        Assert.Equal("a note", old.Note);
        Assert.Equal(LibraryEntryKind.WholeSlot, old.Kind);
        Assert.NotEmpty(old.Sha256);
    }

    /// <summary>
    /// An entry stored before this existed carries no configs block at all, which must read as null
    /// rather than as a folder that was looked at and found empty.
    /// </summary>
    [Fact]
    public void A_manifest_without_a_settings_block_reads_as_nothing_recorded()
    {
        LibraryManifest manifest = Read("{ \"schemaVersion\": 2, \"name\": \"a save\" }");

        Assert.Null(manifest.Configs);
        Assert.Null(manifest.PreviousConfigs);
    }

    [Fact]
    public void An_explicit_null_settings_block_reads_as_nothing_recorded()
    {
        Assert.Null(Read("{ \"schemaVersion\": 2, \"configs\": null }").Configs);
    }

    /// <summary>
    /// An explicit null inside the block overwrites the field initialiser, and every reader walks
    /// this list without a guard.
    /// </summary>
    [Fact]
    public void An_explicit_null_list_of_files_reads_as_an_empty_list()
    {
        LibraryManifest manifest = Read(
            "{ \"schemaVersion\": 2, \"configs\": { \"readTheFolder\": true, \"files\": null } }");

        Assert.NotNull(manifest.Configs);
        Assert.Empty(manifest.Configs!.Files);
    }

    /// <summary>
    /// This is the case the enum ban is for: an unknown property is skipped, but an unknown enum
    /// value would throw and take the whole manifest with it.
    /// </summary>
    [Fact]
    public void A_settings_block_from_a_later_build_still_reads()
    {
        LibraryManifest manifest = Read(
            "{ \"schemaVersion\": 3, \"configs\": { \"readTheFolder\": true, " +
            "\"somethingAddedLater\": { \"nested\": [1, 2, 3] }, " +
            "\"files\": [ { \"relativePath\": \"ModConfigs\\\\future.txt\", \"modId\": \"future\", " +
            "\"format\": \"somethingNew\", \"alsoNew\": true } ] } }");

        ModConfigFile file = Assert.Single(manifest.Configs!.Files);

        Assert.Equal(@"ModConfigs\future.txt", file.RelativePath);
        Assert.Equal("future", file.ModId);
    }

    [Fact]
    public void The_recorded_settings_hold_only_stored_fields()
    {
        using var world = new LibraryWorld();
        LibraryEntry entry = world.Library.StoreSlot(Slot1, "a save", null);

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(entry.ManifestPath));
        JsonElement configs = document.RootElement.GetProperty("configs");

        Assert.Equal(
            new[] { "readTheFolder", "note", "files" },
            configs.EnumerateObject().Select(property => property.Name));

        foreach (JsonElement file in configs.GetProperty("files").EnumerateArray())
        {
            Assert.Equal(
                new[] { "relativePath", "modId", "sizeBytes", "sha256" },
                file.EnumerateObject().Select(property => property.Name));
        }
    }

    private static LibraryManifest Read(string json)
        => JsonSerializer.Deserialize<LibraryManifest>(json, BackupJson.Options)!;
}
