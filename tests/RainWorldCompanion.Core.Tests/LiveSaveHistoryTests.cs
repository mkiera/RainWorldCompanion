using System.Text.Json;
using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Editing;
using RainWorldCompanion.Core.Library;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.Core.Saves.Models;

namespace RainWorldCompanion.Tests;

public class LiveSaveHistoryTests
{
    [Fact]
    public void A_stable_campaign_change_is_captured_but_the_initial_state_is_not()
    {
        using var live = new TempDirectory("live-history-save");
        using var historyRoot = new TempDirectory("live-history-store");
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero));
        WriteSlot(live, "White", 10);
        var history = new LiveSaveHistory(live.Path, historyRoot.Path, "test", time);

        Assert.Null(history.Observe().Captured);

        WriteSlot(live, "White", 11);
        Assert.Null(history.Observe().Captured);
        LiveHistoryEntry captured = Assert.IsType<LiveHistoryEntry>(history.Observe().Captured);

        LiveHistoryCampaign campaign = Assert.Single(captured.Campaigns);
        Assert.Equal("White", campaign.SlugcatId);
        Assert.Equal(11, campaign.Cycle);
        ManifestFileEntry file = Assert.Single(captured.Snapshot.Manifest!.Files);
        Assert.Equal(LibraryEntry.CampaignFileName, file.RelativePath);
        SlotMetadata slot = Assert.Single(captured.Snapshot.Manifest.Slots);
        Assert.Single(slot.Campaigns);
        Assert.False(File.Exists(Path.Combine(captured.Snapshot.DirectoryPath, "sav2")));
        Assert.Single(history.Read().Entries);
    }

    [Fact]
    public void Each_campaign_keeps_its_newest_five_captures()
    {
        using var live = new TempDirectory("live-history-save");
        using var historyRoot = new TempDirectory("live-history-store");
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero));
        WriteSlot(live, "White", 10);
        var history = new LiveSaveHistory(live.Path, historyRoot.Path, "test", time);
        history.Observe();

        for (int cycle = 11; cycle <= 16; cycle++)
        {
            time.Advance(TimeSpan.FromMinutes(1));
            WriteSlot(live, "White", cycle);
            history.Observe();
            Assert.NotNull(history.Observe().Captured);
        }

        LiveHistoryView view = history.Read();
        Assert.Equal(5, view.Entries.Count);
        Assert.Equal(
            new[] { 16, 15, 14, 13, 12 },
            view.Entries.Select(entry => Assert.Single(entry.Campaigns).Cycle.GetValueOrDefault()));
    }

    [Fact]
    public void Repeated_changes_in_one_cycle_keep_the_first_recent_entry()
    {
        using var live = new TempDirectory("live-history-save");
        using var historyRoot = new TempDirectory("live-history-store");
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero));
        WriteSlot(live, "Yellow", 56, food: 3);
        var history = new LiveSaveHistory(live.Path, historyRoot.Path, "test", time);
        history.Observe();

        WriteSlot(live, "Yellow", 56, food: 4);
        history.Observe();
        LiveHistoryEntry first = Assert.IsType<LiveHistoryEntry>(history.Observe().Captured);
        byte[] firstCampaign = File.ReadAllBytes(
            Path.Combine(first.Snapshot.DirectoryPath, LibraryEntry.CampaignFileName));

        time.Advance(TimeSpan.FromMinutes(1));
        WriteSlot(live, "Yellow", 56, food: 5);
        history.Observe();
        Assert.Null(history.Observe().Captured);

        LiveHistoryEntry retained = Assert.Single(history.Read().Entries);
        Assert.Equal(first.Snapshot.Id, retained.Snapshot.Id);
        Assert.Equal(56, Assert.Single(retained.Campaigns).Cycle);
        SnapshotLayout.AssertBytesEqual(
            firstCampaign,
            File.ReadAllBytes(Path.Combine(retained.Snapshot.DirectoryPath, LibraryEntry.CampaignFileName)),
            "oldest same-cycle campaign");
        Assert.False(firstCampaign.SequenceEqual(ReadCampaignBytes(live, "sav2", "Yellow")));
    }

    [Fact]
    public void Reading_history_cleans_up_same_cycle_entries_from_an_older_build()
    {
        using var live = new TempDirectory("live-history-save");
        using var historyRoot = new TempDirectory("live-history-store");
        WriteSlot(live, "Yellow", 56, food: 4);
        byte[] olderCampaign = ReadCampaignBytes(live, "sav2", "Yellow");
        var snapshotter = new BackupService(
            live.Path,
            historyRoot.Path,
            FakeGameDetector.NotRunning(),
            "test");
        BackupSnapshot older = snapshotter.CreateBackup("Recent live save", null);
        WriteHistoryManifest(historyRoot, older, new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero));

        WriteSlot(live, "Yellow", 56, food: 5);
        BackupSnapshot newer = snapshotter.CreateBackup("Recent live save", null);
        WriteHistoryManifest(historyRoot, newer, new DateTimeOffset(2026, 9, 17, 12, 1, 0, TimeSpan.Zero));
        var history = new LiveSaveHistory(live.Path, historyRoot.Path, "test");

        LiveHistoryEntry retained = Assert.Single(history.Read().Entries);

        Assert.Equal(new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero), retained.CapturedUtc);
        Assert.True(File.Exists(Path.Combine(retained.Snapshot.DirectoryPath, LibraryEntry.CampaignFileName)));
        SnapshotLayout.AssertBytesEqual(
            olderCampaign,
            File.ReadAllBytes(Path.Combine(retained.Snapshot.DirectoryPath, LibraryEntry.CampaignFileName)),
            "migrated oldest campaign");
        Assert.False(Directory.Exists(older.DirectoryPath));
        Assert.False(Directory.Exists(newer.DirectoryPath));
        Assert.False(File.Exists(Path.Combine(historyRoot.Path, older.Id + ".live-history.json")));
        Assert.False(File.Exists(Path.Combine(historyRoot.Path, newer.Id + ".live-history.json")));
    }

    [Fact]
    public void Retention_is_independent_for_each_campaign()
    {
        using var live = new TempDirectory("live-history-save");
        using var historyRoot = new TempDirectory("live-history-store");
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero));
        WriteSlot(live, "White", 10, "sav2");
        WriteSlot(live, "Yellow", 40, "sav3");
        var history = new LiveSaveHistory(live.Path, historyRoot.Path, "test", time);
        history.Observe();

        WriteSlot(live, "Yellow", 41, "sav3");
        history.Observe();
        Assert.NotNull(history.Observe().Captured);

        for (int cycle = 11; cycle <= 16; cycle++)
        {
            time.Advance(TimeSpan.FromMinutes(1));
            WriteSlot(live, "White", cycle, "sav2");
            history.Observe();
            Assert.NotNull(history.Observe().Captured);
        }

        LiveHistoryView view = history.Read();
        Assert.Equal(5, view.Entries.Count(entry => entry.Campaigns.Any(campaign => campaign.SlugcatId == "White")));
        Assert.Single(view.Entries, entry => entry.Campaigns.Any(campaign => campaign.SlugcatId == "Yellow"));
        Assert.Equal(6, view.Entries.Count);
    }

    [Fact]
    public void A_recent_capture_can_be_kept_as_a_campaign_library_save()
    {
        using var live = new TempDirectory("live-history-save");
        using var historyRoot = new TempDirectory("live-history-store");
        using var backupRoot = new TempDirectory("live-history-backups");
        using var libraryRoot = new TempDirectory("live-history-library");
        WriteSlot(live, "Yellow", 40);
        var history = new LiveSaveHistory(live.Path, historyRoot.Path, "test");
        history.Observe();
        WriteSlot(live, "Yellow", 41);
        history.Observe();
        LiveHistoryEntry entry = Assert.IsType<LiveHistoryEntry>(history.Observe().Captured);
        var backups = new BackupService(live.Path, backupRoot.Path, FakeGameDetector.NotRunning(), "test");
        var library = new SaveLibrary(backups, libraryRoot.Path, FakeGameDetector.NotRunning(), "test");

        LibraryEntry kept = history.KeepInLibrary(entry, library);

        Assert.True(kept.IsComplete, kept.Problem);
        Assert.True(kept.IsCampaign);
        Assert.Contains("Monk", kept.Name, StringComparison.Ordinal);
        Assert.Single(library.ListEntries());
        Assert.Empty(backups.ListBackups());
        Assert.Single(history.Read().Entries);
        SnapshotLayout.AssertBytesEqual(
            File.ReadAllBytes(Path.Combine(entry.Snapshot.DirectoryPath, LibraryEntry.CampaignFileName)),
            File.ReadAllBytes(kept.ContentPath),
            "kept campaign");
    }

    [Fact]
    public void Restoring_a_recent_campaign_leaves_other_campaigns_and_slots_untouched()
    {
        using var live = new TempDirectory("live-history-save");
        using var historyRoot = new TempDirectory("live-history-store");
        using var backupRoot = new TempDirectory("live-history-backups");
        WriteCampaigns(live, "sav2", ("Yellow", 56, 3), ("White", 20, 4));
        WriteSlot(live, "Red", 8, "sav3", food: 2);
        var history = new LiveSaveHistory(live.Path, historyRoot.Path, "test");
        history.Observe();

        WriteCampaigns(live, "sav2", ("Yellow", 56, 4), ("White", 20, 4));
        history.Observe();
        LiveHistoryEntry entry = Assert.IsType<LiveHistoryEntry>(history.Observe().Captured);

        WriteCampaigns(live, "sav2", ("Yellow", 56, 5), ("White", 20, 8));
        byte[] whiteBefore = ReadCampaignBytes(live, "sav2", "White");
        byte[] slotThreeBefore = File.ReadAllBytes(Path.Combine(live.Path, "sav3"));
        var backups = new BackupService(live.Path, backupRoot.Path, FakeGameDetector.NotRunning(), "test");

        SaveWriteResult result = history.Restore(entry, backups);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        SnapshotLayout.AssertBytesEqual(
            File.ReadAllBytes(Path.Combine(entry.Snapshot.DirectoryPath, LibraryEntry.CampaignFileName)),
            ReadCampaignBytes(live, "sav2", "Yellow"),
            "restored Monk campaign");
        SnapshotLayout.AssertBytesEqual(whiteBefore, ReadCampaignBytes(live, "sav2", "White"), "other campaign");
        SnapshotLayout.AssertBytesEqual(slotThreeBefore, File.ReadAllBytes(Path.Combine(live.Path, "sav3")), "other slot");
        Assert.Single(backups.ListBackups());
        Assert.Equal(BackupKind.PreRestoreSafety, backups.ListBackups()[0].Kind);
    }

    [Fact]
    public void A_corrupt_entry_does_not_block_the_next_capture()
    {
        using var live = new TempDirectory("live-history-save");
        using var historyRoot = new TempDirectory("live-history-store");
        WriteSlot(live, "White", 10);
        var history = new LiveSaveHistory(live.Path, historyRoot.Path, "test");
        history.Observe();
        WriteSlot(live, "White", 11);
        history.Observe();
        LiveHistoryEntry first = Assert.IsType<LiveHistoryEntry>(history.Observe().Captured);
        File.WriteAllText(
            Path.Combine(historyRoot.Path, first.Snapshot.Id + ".live-history.json"),
            "not json");

        WriteSlot(live, "White", 12);
        history.Observe();
        LiveHistoryObservation observation = history.Observe();

        Assert.NotNull(observation.Captured);
        Assert.NotEmpty(observation.Warnings);
        LiveHistoryView view = history.Read();
        Assert.Single(view.Entries);
        Assert.Equal(12, Assert.Single(view.Entries[0].Campaigns).Cycle);
        Assert.NotEmpty(view.Warnings);
    }

    private static void WriteSlot(
        TempDirectory directory,
        string slugcat,
        int cycle,
        string fileName = "sav2",
        int food = 3)
    {
        string body = string.Join(SyntheticSave.FieldSeparator, new[]
        {
            "SAV STATE NUMBER" + SyntheticSave.ValueSeparator + slugcat,
            "TIMELINE" + SyntheticSave.ValueSeparator + slugcat,
            "CYCLENUM" + SyntheticSave.ValueSeparator + cycle,
            "FOOD" + SyntheticSave.ValueSeparator + food,
        });
        string payload = SyntheticSave.Progression(new[] { ("SAVE STATE", body), ("MISCPROG", "stays") });
        directory.WriteBytes(fileName, SyntheticSave.SaveFile(payload));
    }

    private static void WriteCampaigns(
        TempDirectory directory,
        string fileName,
        params (string Slugcat, int Cycle, int Food)[] campaigns)
    {
        var records = new List<(string Header, string Body)>();
        foreach ((string slugcat, int cycle, int food) in campaigns)
        {
            string body = string.Join(SyntheticSave.FieldSeparator, new[]
            {
                "SAV STATE NUMBER" + SyntheticSave.ValueSeparator + slugcat,
                "TIMELINE" + SyntheticSave.ValueSeparator + slugcat,
                "CYCLENUM" + SyntheticSave.ValueSeparator + cycle,
                "FOOD" + SyntheticSave.ValueSeparator + food,
            });
            records.Add(("SAVE STATE", body));
        }

        records.Add(("MISCPROG", "stays"));
        directory.WriteBytes(fileName, SyntheticSave.SaveFile(SyntheticSave.Progression(records)));
    }

    private static byte[] ReadCampaignBytes(TempDirectory directory, string fileName, string slugcat)
        => CampaignFile.ToBytes(
            CampaignFile.ReadFrom(Path.Combine(directory.Path, fileName), slugcat)
            ?? throw new InvalidDataException(fileName + " has no " + slugcat + " campaign."));

    private static void WriteHistoryManifest(
        TempDirectory historyRoot,
        BackupSnapshot snapshot,
        DateTimeOffset capturedUtc)
    {
        var manifest = new LiveHistoryManifest
        {
            CapturedUtc = capturedUtc,
            Campaigns = [new LiveHistoryCampaign(SaveRealm.Local, 2, "Yellow", 56)],
        };
        File.WriteAllText(
            Path.Combine(historyRoot.Path, snapshot.Id + ".live-history.json"),
            JsonSerializer.Serialize(manifest, BackupJson.Options));
    }

    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now += amount;
    }
}
