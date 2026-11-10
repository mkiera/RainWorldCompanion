using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Live;
using RainWorldCompanion.LiveProtocol;
using RWCompanion.Mod;

namespace RainWorldCompanion.Tests;

public sealed class SaveSyncBeforeLoadTests
{
    private static byte[] Save(int cycle) => SyntheticSave.SaveFile(SyntheticSave.SavePayload(slugcat: "White", cycle: cycle));

    private static void WriteOld(TempDirectory live, string name, byte[] bytes, int secondsAgo = 60)
    {
        string path = live.Resolve(name);
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(-secondsAgo));
    }

    [Fact]
    public void The_game_waiting_to_load_gets_the_new_save_without_the_two_check_wait()
    {
        using var world = new World();
        WriteOld(world.Live, "sav3", Save(10), 120);
        WriteOld(world.Live, "online_sav3", Save(10), 120);
        world.Sync.SetEnabled(3, true);
        world.Sync.Check();
        world.Sync.Check();

        // Left the lobby: the online file changed, and the game is about to load sav3.
        WriteOld(world.Live, "online_sav3", Save(14), 30);

        SlotAutoSyncStatus status = world.Sync.SyncNow(3, out bool wrote);

        Assert.True(wrote);
        Assert.Equal(SlotAutoSyncState.Synced, status.State);
        Assert.Equal(File.ReadAllBytes(world.Live.Resolve("online_sav3")), File.ReadAllBytes(world.Live.Resolve("sav3")));
        Assert.Single(world.Backups.ListBackups());
    }

    [Fact]
    public void A_file_written_a_moment_ago_is_not_copied_before_load()
    {
        using var world = new World();
        WriteOld(world.Live, "sav3", Save(10), 120);
        WriteOld(world.Live, "online_sav3", Save(10), 120);
        world.Sync.SetEnabled(3, true);
        world.Sync.Check();
        world.Sync.Check();

        File.WriteAllBytes(world.Live.Resolve("online_sav3"), Save(14));

        SlotAutoSyncStatus status = world.Sync.SyncNow(3, out bool wrote);

        Assert.False(wrote);
        Assert.Equal(SlotAutoSyncState.Waiting, status.State);
        Assert.Equal(Save(10), File.ReadAllBytes(world.Live.Resolve("sav3")));
    }

    [Fact]
    public void A_slot_that_is_off_is_not_synced_before_load()
    {
        using var world = new World();
        WriteOld(world.Live, "sav3", Save(10));
        WriteOld(world.Live, "online_sav3", Save(14), 30);

        SlotAutoSyncStatus status = world.Sync.SyncNow(3, out bool wrote);

        Assert.False(wrote);
        Assert.Equal(SlotAutoSyncState.Off, status.State);
        Assert.Empty(world.Backups.ListBackups());
    }

    [Fact]
    public void A_sync_cancelled_because_the_game_stopped_waiting_writes_nothing()
    {
        using var world = new World();
        WriteOld(world.Live, "sav3", Save(10), 120);
        WriteOld(world.Live, "online_sav3", Save(10), 120);
        world.Sync.SetEnabled(3, true);
        world.Sync.Check();
        world.Sync.Check();
        WriteOld(world.Live, "online_sav3", Save(14), 30);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => world.Sync.SyncNow(3, out _, cancelled.Token));
        Assert.Equal(Save(10), File.ReadAllBytes(world.Live.Resolve("sav3")));
    }

    [Fact]
    public async Task The_game_hook_waits_for_companion_to_sync_the_slot_it_is_about_to_load()
    {
        using var world = new World();
        WriteOld(world.Live, "sav3", Save(10), 120);
        WriteOld(world.Live, "online_sav3", Save(10), 120);
        world.Sync.SetEnabled(3, true);
        world.Sync.Check();
        world.Sync.Check();
        WriteOld(world.Live, "online_sav3", Save(14), 30);

        using var link = new Link(world.Sync);
        await link.ConnectAsync();

        // The game thread is held in LoadProgression until this returns.
        string? message = await Task.Run(() => link.Transport.SyncSave(3, TimeSpan.FromSeconds(6)));

        Assert.NotNull(message);
        Assert.StartsWith("Copied online_sav3 onto sav3", message);
        Assert.Equal(File.ReadAllBytes(world.Live.Resolve("online_sav3")), File.ReadAllBytes(world.Live.Resolve("sav3")));
    }

    [Fact]
    public async Task The_game_hook_does_not_wait_for_a_slot_without_autosync()
    {
        using var world = new World();
        world.Sync.SetEnabled(3, true);
        using var link = new Link(world.Sync);
        await link.ConnectAsync();

        var timer = global::System.Diagnostics.Stopwatch.StartNew();
        string? message = link.Transport.SyncSave(2, TimeSpan.FromSeconds(6));

        Assert.Null(message);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void The_game_hook_does_not_wait_when_companion_is_not_connected()
    {
        var transport = new LiveTransport(Path.Combine(Path.GetTempPath(), "rw-live-none-" + Guid.NewGuid().ToString("N")));
        transport.Publish(new LiveSnapshot { SessionId = "session", CommandVersion = 1 });

        Assert.Null(transport.SyncSave(3, TimeSpan.FromSeconds(6)));
    }

    [Fact]
    public async Task Companion_that_takes_too_long_lets_the_game_load_and_writes_nothing()
    {
        string directory = Path.Combine(Path.GetTempPath(), "rw-live-" + Guid.NewGuid().ToString("N"));
        var cancelledInHandler = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new LiveConnectionServer(directory)
        {
            AutoSyncSlots = () => [3],
            SaveSyncHandler = (_, ct) =>
            {
                ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(20));
                cancelledInHandler.TrySetResult(ct.IsCancellationRequested);
                return new LiveSaveSyncResult { Message = "late" };
            },
        };
        server.Start();
        var transport = new LiveTransport(directory);
        try
        {
            transport.Start();
            await PublishUntilConnected(transport, server);

            string? message = await Task.Run(() => transport.SyncSave(3, TimeSpan.FromSeconds(6)));

            Assert.Equal("Autosync took too long, so the game loaded the save as it was.", message);
            Assert.True(await cancelledInHandler.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            transport.Stop();
            server.Dispose();
            Directory.Delete(directory, true);
        }
    }

    private static async Task PublishUntilConnected(LiveTransport transport, LiveConnectionServer server)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            transport.Publish(new LiveSnapshot { SessionId = "session", CommandVersion = 1 });
            await Task.Delay(100);

            // The first reply carries the autosync slots, which SyncSave needs.
            if (server.Status == LiveConnectionStatus.Connected)
            {
                await Task.Delay(400);
                transport.Publish(new LiveSnapshot { SessionId = "session", CommandVersion = 1 });
                await Task.Delay(400);
                return;
            }
        }

        Assert.Fail("The Game Hook transport did not connect.");
    }

    private sealed class World : IDisposable
    {
        private readonly TempDirectory _backupRoot = new("backups");
        private readonly TempDirectory _state = new("autosync");

        public World()
        {
            Live = new TempDirectory("live");
            Backups = new BackupService(Live.Path, _backupRoot.Path, FakeGameDetector.Running(), "1.0.0-test");
            Sync = new SlotAutoSync(Backups.SlotCopies, Path.Combine(_state.Path, "state.json"));
        }

        public TempDirectory Live { get; }

        public BackupService Backups { get; }

        public SlotAutoSync Sync { get; }

        public void Dispose()
        {
            Live.Dispose();
            _backupRoot.Dispose();
            _state.Dispose();
        }
    }

    private sealed class Link : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "rw-live-" + Guid.NewGuid().ToString("N"));
        private readonly LiveConnectionServer _server;

        public Link(SlotAutoSync sync)
        {
            _server = new LiveConnectionServer(_directory)
            {
                AutoSyncSlots = sync.EnabledSlots,
                SaveSyncHandler = (slot, ct) =>
                {
                    SlotAutoSyncStatus status = sync.SyncNow(slot, out bool wrote, ct);
                    return new LiveSaveSyncResult { Wrote = wrote, Message = status.Text };
                },
            };
            _server.Start();
            Transport = new LiveTransport(_directory);
        }

        public LiveTransport Transport { get; }

        public async Task ConnectAsync()
        {
            Transport.Start();
            await PublishUntilConnected(Transport, _server);
        }

        public void Dispose()
        {
            Transport.Stop();
            _server.Dispose();
            Directory.Delete(_directory, true);
        }
    }
}
