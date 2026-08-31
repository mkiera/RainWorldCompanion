using System.IO;
using System.Text.Json;
using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Library;
using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Tests;

public class LibraryMetadataRefreshTests
{
    private static readonly SaveSlotRef LocalTwo = new(SaveRealm.Local, 2);

    [Fact]
    public void A_freshly_stored_entry_is_stamped_with_the_extractor_version()
    {
        using var world = new LibraryWorld();

        var slot = world.Library.StoreSlot(LocalTwo, "whole slot", null);
        var campaign = world.Library.StoreCampaign(LocalTwo, "White", "one campaign", null);

        Assert.Equal(SaveMetadataExtractor.Version, slot.Manifest!.MetadataVersion);
        Assert.Equal(SaveMetadataExtractor.Version, campaign.Manifest!.MetadataVersion);
    }

    [Fact]
    public void An_entry_stored_under_an_older_reading_is_parsed_again_when_listed()
    {
        using var world = new LibraryWorld();
        var entry = world.Library.StoreCampaign(LocalTwo, "White", "old reading", null);

        MakeStale(entry);
        Assert.Empty(world.Reload(entry).Manifest!.Metadata!.Campaigns);

        var listed = Assert.Single(world.Library.ListEntries());

        Assert.Equal(SaveMetadataExtractor.Version, listed.Manifest!.MetadataVersion);
        Assert.Equal("White", Assert.Single(listed.Manifest.Metadata!.Campaigns).SlugcatId);
    }

    [Fact]
    public void The_reparse_is_written_back_so_it_happens_once()
    {
        using var world = new LibraryWorld();
        var entry = world.Library.StoreSlot(LocalTwo, "whole slot", null);

        MakeStale(entry);
        world.Library.ListEntries();

        var reloaded = world.Reload(entry);
        Assert.Equal(SaveMetadataExtractor.Version, reloaded.Manifest!.MetadataVersion);
        Assert.NotEmpty(reloaded.Manifest.Metadata!.Campaigns);
    }

    [Fact]
    public void A_current_manifest_is_not_rewritten_by_a_listing()
    {
        using var world = new LibraryWorld();
        var entry = world.Library.StoreSlot(LocalTwo, "whole slot", null);

        var manifestPath = Path.Combine(entry.DirectoryPath, LibraryEntry.ManifestFileName);
        var before = File.ReadAllBytes(manifestPath);

        world.Library.ListEntries();

        Assert.Equal(before, File.ReadAllBytes(manifestPath));
    }

    [Fact]
    public void An_entry_whose_bytes_are_gone_is_still_listed_under_its_old_reading()
    {
        using var world = new LibraryWorld();
        var entry = world.Library.StoreCampaign(LocalTwo, "White", "orphaned", null);

        MakeStale(entry);
        File.Delete(entry.CampaignPath);

        var listed = Assert.Single(world.Library.ListEntries());

        Assert.False(listed.IsComplete);
        Assert.Contains(LibraryEntry.CampaignFileName, listed.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void The_previous_save_kept_for_undo_is_reparsed_alongside_the_current_one()
    {
        using var world = new LibraryWorld();
        var entry = world.Library.StoreSlot(LocalTwo, "whole slot", null);
        world.PlayASlot(LocalTwo, "CYCLENUM", "99");
        entry = world.Library.UpdateEntry(entry, LocalTwo);

        MakeStale(entry);

        var listed = Assert.Single(world.Library.ListEntries());

        Assert.NotEmpty(listed.Manifest!.Metadata!.Campaigns);
        Assert.NotEmpty(listed.Manifest.PreviousMetadata!.Campaigns);
    }

    // An older stamp over metadata that read less out of the same bytes.
    private static void MakeStale(LibraryEntry entry)
    {
        var manifestPath = Path.Combine(entry.DirectoryPath, LibraryEntry.ManifestFileName);
        var manifest = JsonSerializer.Deserialize<LibraryManifest>(
            File.ReadAllText(manifestPath), BackupJson.Options)!;

        manifest.MetadataVersion = 0;
        manifest.Metadata = SaveMetadataExtractor.FromPayload(
            "", manifest.Metadata!.FileName, manifest.SourceSlot, manifest.SourceRealm);

        if (manifest.PreviousMetadata is not null)
        {
            manifest.PreviousMetadata = manifest.Metadata;
        }

        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, BackupJson.Options));
    }
}
