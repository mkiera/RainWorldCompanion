using System.Text.Json;
using System.Text.Json.Serialization;

using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Mods;

namespace RainWorldCompanion.Tests;

/// <summary>
/// The mod list was added without moving the schema version, so builds on either side of the
/// change have to read each other's files. These tests prove both directions.
/// </summary>
public class ModListCompatibilityTests
{
    /// <summary>
    /// The manifest shape before mod lists existed. Deserialising a current file into this is
    /// what an older build does.
    /// </summary>
    private sealed class ManifestBeforeModLists
    {
        public int SchemaVersion { get; set; }

        public int ScopeVersion { get; set; }

        public string AppVersion { get; set; } = "";

        public DateTime CreatedUtc { get; set; }

        public string? Label { get; set; }

        public string? Note { get; set; }

        public BackupKind Kind { get; set; }

        public List<ManifestFileEntry> Files { get; set; } = new();

        public List<string> SkippedLinks { get; set; } = new();
    }

    private static CurrentMods Mods() => ModLists.Current(
        "v1.11.8",
        ModLists.Mod("devourment", "0.1.11-ea"),
        ModLists.Mod("MapOptions", "2.3.3", workshopId: "2923374705"));

    [Fact]
    public void Recording_a_mod_list_does_not_move_the_schema_version()
    {
        using var world = new BackupWorld(modListSource: Mods);

        BackupSnapshot snapshot = world.Service.CreateBackup("today", null);

        Assert.Equal(2, snapshot.Manifest!.SchemaVersion);
        Assert.NotNull(snapshot.Manifest.Mods);
    }

    /// <summary>
    /// Unknown members are skipped, and a skipped subtree is never bound, so nothing inside the
    /// mod list block can throw here.
    /// </summary>
    [Fact]
    public void A_build_that_predates_mod_lists_reads_a_manifest_that_has_one()
    {
        using var world = new BackupWorld(modListSource: Mods);
        BackupSnapshot snapshot = world.Service.CreateBackup("today", "a note");

        var old = JsonSerializer.Deserialize<ManifestBeforeModLists>(
            File.ReadAllText(snapshot.ManifestPath),
            BackupJson.Options);

        Assert.NotNull(old);
        Assert.Equal(2, old!.SchemaVersion);
        Assert.Equal("today", old.Label);
        Assert.Equal("a note", old.Note);
        Assert.Equal(BackupKind.Manual, old.Kind);
        Assert.NotEmpty(old.Files);
    }

    /// <summary>
    /// A pre-existing snapshot carries no mods block at all, which must read as null, not an
    /// empty list.
    /// </summary>
    [Fact]
    public void A_manifest_without_a_mod_list_reads_as_nothing_recorded()
    {
        BackupManifest manifest = Read("{ \"schemaVersion\": 2, \"appVersion\": \"1.0.0\", \"kind\": \"Manual\" }");

        Assert.Null(manifest.Mods);
    }

    [Fact]
    public void An_explicit_null_mod_list_reads_as_nothing_recorded()
    {
        Assert.Null(Read("{ \"schemaVersion\": 2, \"mods\": null }").Mods);
    }

    /// <summary>
    /// An explicit null inside the block overwrites the field initialiser, and every reader walks
    /// this list without a guard.
    /// </summary>
    [Fact]
    public void An_explicit_null_list_of_mods_reads_as_an_empty_list()
    {
        BackupManifest manifest = Read(
            "{ \"schemaVersion\": 2, \"mods\": { \"readTheEnabledList\": true, \"mods\": null } }");

        Assert.NotNull(manifest.Mods);
        Assert.Empty(manifest.Mods!.Mods);
    }

    /// <summary>
    /// This is the case the enum ban is for: an unknown property is skipped, but an unknown enum
    /// value would throw and take the whole manifest with it.
    /// </summary>
    [Fact]
    public void A_mod_list_from_a_later_build_still_reads()
    {
        BackupManifest manifest = Read(
            "{ \"schemaVersion\": 3, \"mods\": { \"gameVersion\": \"v1.12.0\", \"readTheEnabledList\": true, " +
            "\"somethingAddedLater\": { \"nested\": [1, 2, 3] }, " +
            "\"mods\": [ { \"id\": \"future\", \"origin\": \"somewhereNew\", \"alsoNew\": true } ] } }");

        ModEntry mod = Assert.Single(manifest.Mods!.Mods);

        Assert.Equal("future", mod.Id);
        Assert.Equal("somewhereNew", mod.Origin);
        Assert.Equal("v1.12.0", manifest.Mods.GameVersion);
    }

    [Fact]
    public void The_recorded_mod_list_holds_only_stored_fields()
    {
        using var world = new BackupWorld(modListSource: Mods);
        BackupSnapshot snapshot = world.Service.CreateBackup("today", null);

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(snapshot.ManifestPath));
        JsonElement mods = document.RootElement.GetProperty("mods");

        Assert.Equal(
            new[] { "gameVersion", "readTheEnabledList", "checkedTheInstall", "checkedTheWorkshop", "note", "mods" },
            mods.EnumerateObject().Select(property => property.Name));

        foreach (JsonElement mod in mods.GetProperty("mods").EnumerateArray())
        {
            // folderName joined the record so a local mod can be turned back on by the folder the
            // loader names it by, which is not its id: Devourment ships in Devourment-mod.
            // requirements joined it so turning a mod on can turn on what it needs.
            Assert.Equal(
                new[] { "id", "name", "version", "workshopId", "folderName", "loadOrder", "origin", "requirements" },
                mod.EnumerateObject().Select(property => property.Name));
        }
    }

    private static BackupManifest Read(string json)
        => JsonSerializer.Deserialize<BackupManifest>(json, BackupJson.Options)!;
}
