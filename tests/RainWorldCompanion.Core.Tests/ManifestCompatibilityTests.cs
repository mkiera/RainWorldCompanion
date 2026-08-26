using System.Globalization;
using System.Text;
using System.Text.Json;

using RainWorldCompanion.Core.Backups;

namespace RainWorldCompanion.Tests;

/// <summary>
/// schemaVersion 1 backups already on disk are the only copy of those saves, so they still have
/// to list, verify and restore. These build a v1 manifest by hand, in the shape the shipped
/// version wrote, rather than by serialising today's classes.
/// </summary>
public class ManifestCompatibilityTests
{
    private const string V1SnapshotId = "2026-08-01_09-00-00";

    [Fact]
    public void The_current_schema_version_is_two()
    {
        Assert.Equal(2, BackupManifest.CurrentSchemaVersion);
    }

    [Fact]
    public void A_new_backup_is_written_at_the_current_schema_version()
    {
        using var world = new BackupWorld();

        var snapshot = world.Service.CreateBackup("today", null);

        Assert.Equal(BackupManifest.CurrentSchemaVersion, snapshot.Manifest!.SchemaVersion);
    }

    [Fact]
    public void A_version_one_backup_still_lists_as_complete()
    {
        using var world = new BackupWorld();
        WriteVersionOneSnapshot(world);

        var snapshot = Load(world);

        Assert.True(snapshot.IsComplete);
        Assert.Null(snapshot.Problem);
        Assert.Equal(1, snapshot.Manifest!.SchemaVersion);
        Assert.Equal("before the update", snapshot.Label);
        Assert.Equal(BackupKind.Manual, snapshot.Kind);
        Assert.Equal(SaveTree.InScope.Length, snapshot.Manifest.Files.Count);
    }

    [Fact]
    public void A_version_one_backup_lists_beside_a_new_one()
    {
        using var world = new BackupWorld();
        WriteVersionOneSnapshot(world);
        var fresh = world.Service.CreateBackup("today", null);

        var listed = world.Service.ListBackups();

        Assert.Equal(2, listed.Count);
        Assert.Contains(listed, s => s.Id == V1SnapshotId && s.IsComplete);
        Assert.Contains(listed, s => s.Id == fresh.Id && s.IsComplete);
    }

    [Fact]
    public void A_version_one_backup_still_verifies()
    {
        using var world = new BackupWorld();
        WriteVersionOneSnapshot(world);

        var result = world.Service.Verify(Load(world));

        Assert.True(result.Ok, string.Join("; ", result.Problems));
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void A_version_one_backup_still_plans_a_restore()
    {
        using var world = new BackupWorld();
        WriteVersionOneSnapshot(world);

        var plan = world.Service.PlanRestore(Load(world));

        Assert.Equal(SaveTree.InScope.Length, plan.Unchanged.Count);
        Assert.Empty(plan.Added);
        Assert.Empty(plan.Overwritten);
        Assert.Empty(plan.Deleted);
    }

    [Fact]
    public void A_version_one_backup_plans_the_changes_a_diverged_save_folder_needs()
    {
        using var world = new BackupWorld();
        WriteVersionOneSnapshot(world);
        File.Delete(world.Live.Resolve("sav3"));
        world.Live.WriteText(@"ModConfigs\devourment.txt", "PredatorMode<optB>false");
        world.Live.WriteText("exp2", "an in-scope file the backup never held");

        var plan = world.Service.PlanRestore(Load(world));

        Assert.Contains("sav3", plan.Added);
        Assert.Contains(@"ModConfigs\devourment.txt", plan.Overwritten);
        Assert.Contains("exp2", plan.Deleted);
    }

    [Fact]
    public void A_version_one_backup_still_restores()
    {
        using var world = new BackupWorld();
        WriteVersionOneSnapshot(world);
        var expected = world.Live.ReadTree();
        File.Delete(world.Live.Resolve("sav3"));
        world.Live.WriteText(@"ModConfigs\devourment.txt", "PredatorMode<optB>false");

        var result = world.Service.RestoreBackup(Load(world));

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.True(result.LiveFolderModified);
        Assert.Equal(expected.Count, world.Live.ReadTree().Count);
        Assert.Equal(expected["sav3"], world.Live.ReadBytes("sav3"));
        Assert.Equal(expected[@"ModConfigs\devourment.txt"], world.Live.ReadBytes(@"ModConfigs\devourment.txt"));
    }

    [Fact]
    public void A_version_one_slot_keeps_the_campaign_detail_it_recorded()
    {
        using var world = new BackupWorld();
        WriteVersionOneSnapshot(world);

        var slot = Assert.Single(Load(world).Manifest!.Slots, s => s.Slot == 2);

        Assert.Equal("sav2", slot.FileName);
        Assert.True(slot.ChecksumValid);
        Assert.Null(slot.ParseError);

        var campaign = Assert.Single(slot.Campaigns);
        Assert.Equal("White", campaign.SlugcatId);
        Assert.Equal(17, campaign.CycleNum);
        Assert.Equal(3, campaign.Food);
        Assert.Equal("SU_S04", campaign.DenPos);
        Assert.Equal("8840", campaign.Seed);
        Assert.Equal(0, campaign.DevourmentStateCount);
        Assert.False(campaign.HasGlow);
    }

    [Fact]
    public void A_version_one_campaign_still_gets_a_display_name()
    {
        using var world = new BackupWorld();
        WriteVersionOneSnapshot(world);

        var campaign = Assert.Single(Assert.Single(Load(world).Manifest!.Slots, s => s.Slot == 2).Campaigns);

        // DisplayName is computed from the id rather than stored, so a manifest written before
        // it existed still shows the in-game name.
        Assert.Equal("Survivor", campaign.DisplayName);
    }

    [Fact]
    public void The_fields_a_version_one_campaign_never_held_come_back_empty_rather_than_null()
    {
        using var world = new BackupWorld();
        WriteVersionOneSnapshot(world);

        var campaign = Assert.Single(Assert.Single(Load(world).Manifest!.Slots, s => s.Slot == 2).Campaigns);

        CampaignDetailTests.AssertCollectionsAreEmptyNotNull(campaign);
        Assert.Null(campaign.Karma);
        Assert.Null(campaign.KarmaCap);
        Assert.Null(campaign.ReinforcedKarma);
        Assert.Null(campaign.Deaths);
        Assert.Null(campaign.Survives);
        Assert.Null(campaign.Quits);
        Assert.Null(campaign.TotalFoodEaten);
        Assert.Null(campaign.PlayTime);
        Assert.Null(campaign.CyclesThisVersion);
        Assert.Null(campaign.Timeline);
        Assert.Null(campaign.LastDenPos);
        Assert.False(campaign.HasTheMark);
        Assert.False(campaign.Ascended);
        Assert.False(campaign.HasRobo);
        Assert.False(campaign.JustBeatGame);
        Assert.False(campaign.RedsDeathStored);
        Assert.False(campaign.RedExtraCycles);
        Assert.False(campaign.EffectiveRedsDeath);
        Assert.Equal(0, campaign.TotalKills);
    }

    [Fact]
    public void A_version_one_slot_that_recorded_a_parse_error_keeps_it()
    {
        using var world = new BackupWorld();
        WriteVersionOneSnapshot(world);

        var slot = Assert.Single(Load(world).Manifest!.Slots, s => s.Slot == 3);

        Assert.Equal("file not found", slot.ParseError);
        Assert.NotNull(slot.Campaigns);
        Assert.Empty(slot.Campaigns);
    }

    [Fact]
    public void A_version_one_manifest_with_no_slots_at_all_still_lists_and_verifies()
    {
        using var world = new BackupWorld();
        WriteVersionOneSnapshot(world, includeSlots: false);

        var snapshot = Load(world);

        Assert.True(snapshot.IsComplete);
        Assert.NotNull(snapshot.Manifest!.Slots);
        Assert.Empty(snapshot.Manifest.Slots);
        Assert.NotNull(snapshot.Manifest.SkippedLinks);
        Assert.True(world.Service.Verify(snapshot).Ok);
    }

    [Fact]
    public void The_manifest_does_not_record_the_values_it_computes()
    {
        using var world = new BackupWorld();
        var snapshot = world.Service.CreateBackup("today", null);

        using var document = JsonDocument.Parse(File.ReadAllText(snapshot.ManifestPath));

        var campaigns = document.RootElement.GetProperty("slots").EnumerateArray()
            .SelectMany(slot => slot.GetProperty("campaigns").EnumerateArray())
            .ToList();

        Assert.NotEmpty(campaigns);

        // DisplayName and TotalKills are computed, not stored, so they never appear here. A kill
        // entry's own displayName is different: it is read off the save and does belong in the file.
        foreach (var campaign in campaigns)
        {
            Assert.False(campaign.TryGetProperty("displayName", out _));
            Assert.False(campaign.TryGetProperty("totalKills", out _));

            // displayCycleNum and effectiveRedsDeath are also derived, from the cycle number and
            // flags the file already records.
            Assert.False(campaign.TryGetProperty("displayCycleNum", out _));
            Assert.False(campaign.TryGetProperty("effectiveRedsDeath", out _));

            // A passage's goal is read from its own name and stored progress, so recording it
            // would freeze a requirement that a later game version can change.
            if (campaign.TryGetProperty("passages", out var passages))
            {
                foreach (var passage in passages.EnumerateArray())
                {
                    Assert.False(passage.TryGetProperty("goal", out _));
                }
            }
        }
    }

    [Fact]
    public void A_campaign_still_reports_its_display_name_and_kill_total_after_a_round_trip()
    {
        using var world = new BackupWorld();
        var snapshot = world.Service.CreateBackup("today", null);

        var reloaded = BackupSnapshot.Load(snapshot.DirectoryPath);
        var campaign = reloaded.Manifest!.Slots
            .SelectMany(slot => slot.Campaigns)
            .First(c => c.SlugcatId == "White");

        Assert.Equal("Survivor", campaign.DisplayName);
        Assert.Equal(campaign.Kills.Sum(kill => kill.Count), campaign.TotalKills);
    }

    [Fact]
    public void A_manifest_that_stores_null_for_a_collection_reads_it_back_as_empty()
    {
        using var world = new BackupWorld();
        WriteNullCollectionSnapshot(world);

        var manifest = Load(world).Manifest!;

        // An explicit JSON null overwrites a field initialiser, so without normalising on the
        // way in these come back null and every caller that walks them throws.
        Assert.NotNull(manifest.Files);
        Assert.NotNull(manifest.Slots);
        Assert.NotNull(manifest.SkippedLinks);
        Assert.Empty(manifest.SkippedLinks);

        var slot = Assert.Single(manifest.Slots);
        Assert.NotNull(slot.Campaigns);

        var campaign = Assert.Single(slot.Campaigns);
        CampaignDetailTests.AssertCollectionsAreEmptyNotNull(campaign);
        Assert.Equal(0, campaign.TotalKills);
    }

    [Fact]
    public void A_slot_that_stores_null_for_its_campaigns_reads_back_as_empty()
    {
        using var world = new BackupWorld();
        WriteNullCollectionSnapshot(world, nullCampaigns: true);

        var slot = Assert.Single(Load(world).Manifest!.Slots);

        Assert.NotNull(slot.Campaigns);
        Assert.Empty(slot.Campaigns);
        Assert.Equal("Slot 1: empty", slot.Describe());
    }

    [Fact]
    public void A_manifest_holding_nulls_still_verifies_and_restores()
    {
        using var world = new BackupWorld();
        WriteNullCollectionSnapshot(world);
        var expected = world.Live.ReadTree();
        File.Delete(world.Live.Resolve("sav3"));

        Assert.True(world.Service.Verify(Load(world)).Ok);

        var result = world.Service.RestoreBackup(Load(world));

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal(expected["sav3"], world.Live.ReadBytes("sav3"));
    }

    private static BackupSnapshot Load(BackupWorld world)
        => world.Service.ListBackups().Single(s => s.Id == V1SnapshotId);

    /// <summary>
    /// Nothing this app writes looks like this, but a hand-edited file or another tool's output
    /// does, and the model promises the UI these collections are never null.
    /// </summary>
    private static void WriteNullCollectionSnapshot(BackupWorld world, bool nullCampaigns = false)
    {
        var files = new List<string>();

        foreach (var relative in SaveTree.InScope)
        {
            var source = world.Live.Resolve(relative);
            var copied = world.BackupRoot.CopyFrom(source, V1SnapshotId + "\\" + relative);

            files.Add(
                "{ \"relativePath\": " + Quote(relative) +
                ", \"sizeBytes\": " + new FileInfo(copied).Length.ToString(CultureInfo.InvariantCulture) +
                ", \"sha256\": " + Quote(Hashing.ComputeFileSha256(copied)) +
                ", \"lastWriteUtc\": " + Quote(Iso(File.GetLastWriteTimeUtc(copied))) + " }");
        }

        var campaigns = nullCampaigns
            ? "null"
            : "[ { \"slugcatId\": \"White\", \"cycleNum\": 17, \"kills\": null, \"echoes\": null, " +
              "\"unlockedGates\": null, \"passages\": null, \"devourmentStates\": null, " +
              "\"swallowedItems\": null, \"heldItems\": null } ]";

        var json = new StringBuilder();
        json.Append("{\n");
        json.Append("  \"schemaVersion\": 2,\n");
        json.Append("  \"appVersion\": \"1.0.0-test\",\n");
        json.Append("  \"createdUtc\": \"2026-08-01T09:00:00Z\",\n");
        json.Append("  \"label\": \"hand edited\",\n");
        json.Append("  \"kind\": \"Manual\",\n");
        json.Append("  \"files\": [\n    ").Append(string.Join(",\n    ", files)).Append("\n  ],\n");
        json.Append("  \"slots\": [ { \"slot\": 1, \"fileName\": \"sav\", \"campaigns\": ")
            .Append(campaigns).Append(" } ],\n");
        json.Append("  \"skippedLinks\": null\n");
        json.Append("}\n");

        world.BackupRoot.WriteText(V1SnapshotId + @"\manifest.json", json.ToString());
    }

    /// <summary>Writes the manifest.json the shipped version 1 wrote: only the seven per-campaign fields it knew.</summary>
    private static void WriteVersionOneSnapshot(BackupWorld world, bool includeSlots = true)
    {
        var files = new List<string>();

        foreach (var relative in SaveTree.InScope)
        {
            var source = world.Live.Resolve(relative);
            var copied = world.BackupRoot.CopyFrom(source, V1SnapshotId + "\\" + relative);

            files.Add(
                "{ \"relativePath\": " + Quote(relative) +
                ", \"sizeBytes\": " + new FileInfo(copied).Length.ToString(CultureInfo.InvariantCulture) +
                ", \"sha256\": " + Quote(Hashing.ComputeFileSha256(copied)) +
                ", \"lastWriteUtc\": " + Quote(Iso(File.GetLastWriteTimeUtc(copied))) + " }");
        }

        var json = new StringBuilder();
        json.Append("{\n");
        json.Append("  \"schemaVersion\": 1,\n");
        json.Append("  \"appVersion\": \"0.9.0\",\n");
        json.Append("  \"createdUtc\": \"2026-08-01T09:00:00Z\",\n");
        json.Append("  \"label\": \"before the update\",\n");
        json.Append("  \"note\": \"written by the shipped version\",\n");
        json.Append("  \"kind\": \"Manual\",\n");
        json.Append("  \"files\": [\n    ").Append(string.Join(",\n    ", files)).Append("\n  ],\n");

        if (includeSlots)
        {
            json.Append("  \"slots\": [\n");
            json.Append("    { \"slot\": 1, \"fileName\": \"sav\", \"checksumValid\": true, \"parseError\": null, \"campaigns\": [\n");
            json.Append("      { \"slugcatId\": \"White\", \"cycleNum\": 17, \"food\": 3, \"denPos\": \"SU_S04\", \"seed\": \"8840\", \"devourmentStateCount\": 0, \"hasGlow\": false } ] },\n");
            json.Append("    { \"slot\": 2, \"fileName\": \"sav2\", \"checksumValid\": true, \"parseError\": null, \"campaigns\": [\n");
            json.Append("      { \"slugcatId\": \"White\", \"cycleNum\": 17, \"food\": 3, \"denPos\": \"SU_S04\", \"seed\": \"8840\", \"devourmentStateCount\": 0, \"hasGlow\": false } ] },\n");
            json.Append("    { \"slot\": 3, \"fileName\": \"sav3\", \"checksumValid\": null, \"parseError\": \"file not found\", \"campaigns\": [] }\n");
            json.Append("  ],\n");
        }

        json.Append("  \"skippedLinks\": []\n");
        json.Append("}\n");

        world.BackupRoot.WriteText(V1SnapshotId + @"\manifest.json", json.ToString());
    }

    private static string Iso(DateTime value)
        => value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    private static string Quote(string value)
        => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
