using RainWorldCompanion.Core.Backups;

namespace RainWorldCompanion.Tests;

public class SlotAutoSyncTests
{
    private static byte[] Save(int cycle, string slugcat = "White")
        => SyntheticSave.SaveFile(SyntheticSave.SavePayload(slugcat: slugcat, cycle: cycle));

    [Fact]
    public void A_new_online_save_reaches_the_local_file_while_the_game_runs()
    {
        using var world = new SyncWorld();
        world.Write("sav3", Save(10));
        world.Write("online_sav3", Save(10));
        world.Sync.SetEnabled(3, true);
        world.Settle();

        world.Write("online_sav3", Save(11));
        world.Settle();

        Assert.Equal(world.Read("online_sav3"), world.Read("sav3"));
        Assert.Equal(SlotAutoSyncState.Synced, world.Sync.StatusFor(3).State);
    }

    [Fact]
    public void A_new_local_save_reaches_the_online_file()
    {
        using var world = new SyncWorld();
        world.Write("sav3", Save(10));
        world.Write("online_sav3", Save(10));
        world.Sync.SetEnabled(3, true);
        world.Settle();

        world.Write("sav3", Save(12));
        world.Settle();

        Assert.Equal(world.Read("sav3"), world.Read("online_sav3"));
    }

    [Fact]
    public void Every_sync_takes_a_backup_holding_the_file_it_overwrote()
    {
        using var world = new SyncWorld();
        byte[] before = Save(10);
        world.Write("sav3", before);
        world.Write("online_sav3", before);
        world.Sync.SetEnabled(3, true);
        world.Settle();

        world.Write("online_sav3", Save(11));
        world.Settle();

        BackupSnapshot backup = Assert.Single(world.Backups.ListBackups());
        Assert.Equal(BackupManifest.AutoSyncOrigin, backup.Manifest!.Origin);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(backup.DirectoryPath, "sav3")));
    }

    [Fact]
    public void A_slot_that_is_off_is_left_alone()
    {
        using var world = new SyncWorld();
        world.Write("sav3", Save(10));
        world.Write("online_sav3", Save(14));
        world.Settle();

        Assert.NotEqual(world.Read("sav3"), world.Read("online_sav3"));
        Assert.Empty(world.Backups.ListBackups());
    }

    [Fact]
    public void Turning_a_slot_on_copies_the_newer_file_onto_the_older_one()
    {
        using var world = new SyncWorld();
        world.Write("sav3", Save(10));
        world.Write("online_sav3", Save(14));
        File.SetLastWriteTimeUtc(world.Live.Resolve("sav3"), DateTime.UtcNow.AddHours(-1));
        world.Sync.SetEnabled(3, true);
        world.Settle();

        Assert.Equal(world.Read("online_sav3"), world.Read("sav3"));
    }

    [Fact]
    public void Both_files_changed_since_the_last_sync_is_a_conflict_and_nothing_is_written()
    {
        using var world = new SyncWorld();
        world.Write("sav3", Save(10));
        world.Write("online_sav3", Save(10));
        world.Sync.SetEnabled(3, true);
        world.Settle();

        byte[] local = Save(11);
        byte[] online = Save(12);
        world.Write("sav3", local);
        world.Write("online_sav3", online);
        world.Settle();

        Assert.Equal(local, world.Read("sav3"));
        Assert.Equal(online, world.Read("online_sav3"));
        Assert.Equal(SlotAutoSyncState.Conflict, world.Sync.StatusFor(3).State);
    }

    /// <summary>The game loaded sav3 before autosync updated it and then saved, so sav3 changed
    /// but is behind online_sav3. Copying it would undo the online session.</summary>
    [Fact]
    public void An_older_save_is_not_copied_over_a_newer_one()
    {
        using var world = new SyncWorld();
        world.Write("sav3", Save(10));
        world.Write("online_sav3", Save(10));
        world.Sync.SetEnabled(3, true);
        world.Settle();

        world.Write("online_sav3", Save(15));
        world.Settle();
        world.Write("sav3", Save(11));
        world.Settle();

        Assert.Equal(SlotAutoSyncState.Conflict, world.Sync.StatusFor(3).State);
        Assert.Equal(Save(15), world.Read("online_sav3"));
    }

    [Fact]
    public void A_campaign_only_the_target_has_is_not_overwritten()
    {
        using var world = new SyncWorld();
        world.Write("sav3", Save(10, "Yellow"));
        world.Write("online_sav3", Save(4, "White"));
        world.Sync.SetEnabled(3, true);
        world.Settle();

        Assert.Equal(SlotAutoSyncState.Conflict, world.Sync.StatusFor(3).State);
        Assert.NotEqual(world.Read("sav3"), world.Read("online_sav3"));
    }

    [Fact]
    public void A_file_still_being_written_is_not_copied()
    {
        using var world = new SyncWorld();
        world.Write("sav3", Save(10));
        world.Write("online_sav3", Save(10));
        world.Sync.SetEnabled(3, true);
        world.Settle();

        world.Write("online_sav3", Save(11));
        world.Sync.Check();

        Assert.Equal(Save(10), world.Read("sav3"));
    }

    [Fact]
    public void The_setting_and_the_last_sync_survive_a_restart()
    {
        using var world = new SyncWorld();
        world.Write("sav3", Save(10));
        world.Write("online_sav3", Save(10));
        world.Sync.SetEnabled(3, true);
        world.Settle();

        var reopened = new SlotAutoSync(world.Backups.SlotCopies, world.StatePath);
        Assert.True(reopened.IsEnabled(3));
        Assert.False(reopened.IsEnabled(2));

        // Written while the app was closed, and older on disk than sav3. With the last sync
        // remembered it still wins, because it is the only file that changed.
        world.Write("online_sav3", Save(11));
        File.SetLastWriteTimeUtc(world.Live.Resolve("online_sav3"), DateTime.UtcNow.AddHours(-1));
        reopened.Check();
        reopened.Check();

        Assert.Equal(world.Read("online_sav3"), world.Read("sav3"));
    }

    [Fact]
    public void A_sync_turned_off_while_its_backup_was_taken_writes_nothing()
    {
        using var world = new SyncWorld();
        byte[] local = Save(10);
        world.Write("sav3", local);
        world.Write("online_sav3", Save(11));
        string sourceHash = Hashing.ComputeFileSha256(world.Live.Resolve("online_sav3"));
        string targetHash = Hashing.ComputeFileSha256(world.Live.Resolve("sav3"));

        SlotCopyResult result = world.Backups.SlotCopies.SyncSlot(
            new(Core.Saves.SaveRealm.Online, 3),
            new(Core.Saves.SaveRealm.Local, 3),
            sourceHash,
            targetHash,
            stillWanted: () => false);

        Assert.False(result.Success);
        Assert.Equal(local, world.Read("sav3"));
    }

    [Theory]
    [InlineData("a", "b", "a", SlotAutoSync.SyncDirection.OnlineToLocal)]
    [InlineData("b", "a", "a", SlotAutoSync.SyncDirection.LocalToOnline)]
    [InlineData("b", "c", "a", SlotAutoSync.SyncDirection.Conflict)]
    [InlineData(null, "a", "a", SlotAutoSync.SyncDirection.SourceMissing)]
    [InlineData(null, "b", "a", SlotAutoSync.SyncDirection.Conflict)]
    [InlineData("a", null, "a", SlotAutoSync.SyncDirection.SourceMissing)]
    [InlineData("a", "a", "b", SlotAutoSync.SyncDirection.None)]
    public void The_file_that_changed_since_the_last_sync_wins(
        string? local, string? online, string last, SlotAutoSync.SyncDirection expected)
        => Assert.Equal(expected, SlotAutoSync.Decide(local, online, last, DateTime.UtcNow, DateTime.UtcNow));

    private sealed class SyncWorld : IDisposable
    {
        private readonly TempDirectory _backupRoot = new("backups");
        private readonly TempDirectory _state = new("autosync");

        public SyncWorld()
        {
            Live = new TempDirectory("live");
            Backups = new BackupService(Live.Path, _backupRoot.Path, FakeGameDetector.Running(), "1.0.0-test");
            StatePath = Path.Combine(_state.Path, "state.json");
            Sync = new SlotAutoSync(Backups.SlotCopies, StatePath);
        }

        public TempDirectory Live { get; }

        public BackupService Backups { get; }

        public string StatePath { get; }

        public SlotAutoSync Sync { get; }

        public void Write(string name, byte[] bytes)
        {
            string path = Live.Resolve(name);
            DateTime previous = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            File.WriteAllBytes(path, bytes);

            // A write inside the file system's timestamp resolution would look like no write.
            if (File.GetLastWriteTimeUtc(path) <= previous)
            {
                File.SetLastWriteTimeUtc(path, previous.AddSeconds(1));
            }
        }

        public byte[] Read(string name) => File.ReadAllBytes(Live.Resolve(name));

        /// <summary>Enough checks for a changed file to settle, be copied, and the copy to settle.</summary>
        public void Settle()
        {
            for (int i = 0; i < 4; i++)
            {
                Sync.Check();
            }
        }

        public void Dispose()
        {
            Live.Dispose();
            _backupRoot.Dispose();
            _state.Dispose();
        }
    }
}
