using System.Text.Json;
using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Saves;

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
    public void Repeated_changes_in_one_cycle_replace_the_recent_entry()
    {
        using var live = new TempDirectory("live-history-save");
        using var historyRoot = new TempDirectory("live-history-store");
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero));
        WriteSlot(live, "Yellow", 56, food: 3);
        var history = new LiveSaveHistory(live.Path, historyRoot.Path, "test", time);
        history.Observe();

        WriteSlot(live, "Yellow", 56, food: 4);
        history.Observe();
        Assert.NotNull(history.Observe().Captured);

        time.Advance(TimeSpan.FromMinutes(1));
        WriteSlot(live, "Yellow", 56, food: 5);
        history.Observe();
        LiveHistoryEntry newest = Assert.IsType<LiveHistoryEntry>(history.Observe().Captured);

        LiveHistoryEntry retained = Assert.Single(history.Read().Entries);
        Assert.Equal(newest.Snapshot.Id, retained.Snapshot.Id);
        Assert.Equal(56, Assert.Single(retained.Campaigns).Cycle);
    }

    [Fact]
    public void Reading_history_cleans_up_same_cycle_entries_from_an_older_build()
    {
        using var live = new TempDirectory("live-history-save");
        using var historyRoot = new TempDirectory("live-history-store");
        WriteSlot(live, "Yellow", 56, food: 4);
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

        Assert.Equal(newer.Id, retained.Snapshot.Id);
        Assert.False(Directory.Exists(older.DirectoryPath));
        Assert.False(File.Exists(Path.Combine(historyRoot.Path, older.Id + ".live-history.json")));
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
    public void A_recent_capture_can_be_kept_as_an_ordinary_backup()
    {
        using var live = new TempDirectory("live-history-save");
        using var historyRoot = new TempDirectory("live-history-store");
        using var backupRoot = new TempDirectory("live-history-backups");
        WriteSlot(live, "Yellow", 40);
        var history = new LiveSaveHistory(live.Path, historyRoot.Path, "test");
        history.Observe();
        WriteSlot(live, "Yellow", 41);
        history.Observe();
        LiveHistoryEntry entry = Assert.IsType<LiveHistoryEntry>(history.Observe().Captured);
        var backups = new BackupService(live.Path, backupRoot.Path, FakeGameDetector.NotRunning(), "test");

        BackupSnapshot kept = history.KeepAsBackup(entry, backups);

        Assert.True(kept.IsComplete, kept.Problem);
        Assert.Equal(BackupKind.Manual, kept.Kind);
        Assert.Contains("Monk", kept.Label, StringComparison.Ordinal);
        Assert.Single(backups.ListBackups());
        Assert.Single(history.Read().Entries);
        SnapshotLayout.AssertBytesEqual(
            File.ReadAllBytes(Path.Combine(entry.Snapshot.DirectoryPath, "sav2")),
            File.ReadAllBytes(Path.Combine(kept.DirectoryPath, "sav2")),
            "kept sav2");
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
