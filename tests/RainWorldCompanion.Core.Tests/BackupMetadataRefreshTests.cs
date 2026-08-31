using System.IO;
using System.Text.Json;
using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Tests;

public class BackupMetadataRefreshTests
{
    [Fact]
    public void A_fresh_snapshot_is_stamped_with_the_extractor_version()
    {
        using var world = new BackupWorld();

        var snapshot = world.Service.CreateBackup("first", null);

        Assert.Equal(SaveMetadataExtractor.Version, snapshot.Manifest!.MetadataVersion);
    }

    [Fact]
    public void A_snapshot_taken_under_an_older_reading_is_parsed_again_when_listed()
    {
        using var world = new BackupWorld();
        var snapshot = world.Service.CreateBackup("first", null);
        int slotCount = snapshot.Manifest!.Slots.Count;

        MakeStale(snapshot);

        var listed = Assert.Single(world.Service.ListBackups());

        Assert.Equal(SaveMetadataExtractor.Version, listed.Manifest!.MetadataVersion);
        Assert.Equal(slotCount, listed.Manifest.Slots.Count);
        Assert.All(listed.Manifest.Slots, slot => Assert.Null(slot.ParseError));
    }

    [Fact]
    public void The_reparse_is_written_back_so_it_happens_once()
    {
        using var world = new BackupWorld();
        var snapshot = world.Service.CreateBackup("first", null);

        MakeStale(snapshot);
        world.Service.ListBackups();

        var manifestPath = Path.Combine(snapshot.DirectoryPath, BackupSnapshot.ManifestFileName);
        var before = File.ReadAllBytes(manifestPath);

        world.Service.ListBackups();

        Assert.Equal(before, File.ReadAllBytes(manifestPath));
    }

    [Fact]
    public void A_current_manifest_is_not_rewritten_by_a_listing()
    {
        using var world = new BackupWorld();
        var snapshot = world.Service.CreateBackup("first", null);

        var manifestPath = Path.Combine(snapshot.DirectoryPath, BackupSnapshot.ManifestFileName);
        var before = File.ReadAllBytes(manifestPath);

        world.Service.ListBackups();

        Assert.Equal(before, File.ReadAllBytes(manifestPath));
    }

    [Fact]
    public void A_manifest_from_a_later_schema_is_left_alone()
    {
        using var world = new BackupWorld();
        var snapshot = world.Service.CreateBackup("first", null);

        MakeStale(snapshot, schemaVersion: BackupManifest.CurrentSchemaVersion + 1);
        var manifestPath = Path.Combine(snapshot.DirectoryPath, BackupSnapshot.ManifestFileName);
        var before = File.ReadAllBytes(manifestPath);

        var listed = Assert.Single(world.Service.ListBackups());

        Assert.Equal(0, listed.Manifest!.MetadataVersion);
        Assert.Equal(before, File.ReadAllBytes(manifestPath));
    }

    [Fact]
    public void What_the_snapshot_records_about_itself_survives_the_reparse()
    {
        using var world = new BackupWorld();
        var snapshot = world.Service.CreateBackup("first", "a note");

        MakeStale(snapshot);

        var listed = Assert.Single(world.Service.ListBackups());
        var manifest = listed.Manifest!;

        Assert.Equal("first", manifest.Label);
        Assert.Equal("a note", manifest.Note);
        Assert.Equal(snapshot.Manifest!.Files.Count, manifest.Files.Count);
        Assert.Equal(snapshot.Manifest.ScopeVersion, manifest.ScopeVersion);
    }

    // An older stamp over slots that read less out of the same bytes.
    private static void MakeStale(BackupSnapshot snapshot, int? schemaVersion = null)
    {
        var manifestPath = Path.Combine(snapshot.DirectoryPath, BackupSnapshot.ManifestFileName);
        var manifest = JsonSerializer.Deserialize<BackupManifest>(
            File.ReadAllText(manifestPath), BackupJson.Options)!;

        manifest.MetadataVersion = 0;
        manifest.SchemaVersion = schemaVersion ?? manifest.SchemaVersion;
        manifest.Slots.Clear();

        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, BackupJson.Options));
    }
}
