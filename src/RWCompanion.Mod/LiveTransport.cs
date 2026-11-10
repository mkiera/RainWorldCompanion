using System.Net;
using System.Net.Sockets;
using System.Text;
using RainWorldCompanion.LiveProtocol;

namespace RWCompanion.Mod;

internal sealed class LiveTransport
{
    private readonly object _sync = new();
    private readonly string _discoveryDirectory;
    private LiveSnapshot? _latest;
    private long _publishedAt;
    private CancellationTokenSource? _stop;
    private TcpClient? _client;
    private LiveCommand? _command;
    private long _sequence;
    private int[]? _autoSyncSlots;
    private long _repliedAt;
    private SaveSyncWait? _saveSync;

    private sealed class SaveSyncWait
    {
        internal readonly LiveSaveSyncRequest Request;
        internal readonly ManualResetEventSlim Done = new(false);
        internal bool Sent;
        internal string? Message;

        internal SaveSyncWait(LiveSaveSyncRequest request) => Request = request;

        internal void Finish(string message)
        {
            Message = message;
            Done.Set();
        }
    }

    private static double SecondsSince(long timestamp)
        => (System.Diagnostics.Stopwatch.GetTimestamp() - timestamp) / (double)System.Diagnostics.Stopwatch.Frequency;

    // Blocks the game thread, so it only waits when Companion answered recently and has autosync on for the slot.
    internal string? SyncSave(int slot, TimeSpan wait)
    {
        SaveSyncWait request;
        lock (_sync)
        {
            if (_latest == null || _saveSync != null || _autoSyncSlots == null || Array.IndexOf(_autoSyncSlots, slot) < 0 || SecondsSince(_repliedAt) > 3)
                return null;
            request = new SaveSyncWait(new LiveSaveSyncRequest { Id = Guid.NewGuid().ToString("N"), Slot = slot });
            _saveSync = request;
        }
        try
        {
            return request.Done.Wait(wait) ? request.Message : "Companion did not answer in time, so the save loaded as it was.";
        }
        finally
        {
            lock (_sync) if (_saveSync == request) _saveSync = null;
        }
    }

    internal LiveTransport(string? discoveryDirectory = null) => _discoveryDirectory = discoveryDirectory ?? ProtocolInfo.DiscoveryDirectory;

    internal LiveCommand? TakeCommand()
    {
        lock (_sync) { var command = _command; _command = null; return command; }
    }

    internal void Publish(LiveSnapshot snapshot)
    {
        lock (_sync)
        {
            snapshot.Sequence = ++_sequence;
            _latest = snapshot;
            _publishedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        }
    }

    internal void Start()
    {
        if (_stop != null) return;
        _stop = new CancellationTokenSource();
        var token = _stop.Token;
        _ = Task.Run(() => RunAsync(token));
    }

    internal void Stop()
    {
        _stop?.Cancel();
        _client?.Close();
        _stop = null;
    }

    private async Task RunAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var discovery = LiveJson.Deserialize<LiveDiscovery>(File.ReadAllText(Path.Combine(_discoveryDirectory, "endpoint.json")));
                using var client = new TcpClient();
                _client = client;
                var connect = client.ConnectAsync(IPAddress.Loopback, discovery.Port);
                if (await Task.WhenAny(connect, Task.Delay(2000, stop)) != connect) continue;
                await connect;
                using var registration = stop.Register(client.Close);
                using var writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
                using var reader = new StreamReader(client.GetStream(), Encoding.UTF8);
                long lastSequence = -1;
                while (!stop.IsCancellationRequested)
                {
                    LiveSnapshot? snapshot;
                    SaveSyncWait? saveSync = null;
                    string json = "";
                    lock (_sync)
                    {
                        snapshot = SecondsSince(_publishedAt) < 2 ? _latest : null;
                        // The game thread is waiting in SyncSave and publishes nothing, so the last
                        // snapshot is sent again with the request and a new sequence.
                        if (_saveSync is { Sent: false } pending && _latest != null)
                        {
                            saveSync = pending;
                            pending.Sent = true;
                            snapshot = _latest;
                            snapshot.Sequence = ++_sequence;
                        }
                        if (snapshot != null && snapshot.Sequence != lastSequence)
                        {
                            snapshot.Token = discovery.Token;
                            snapshot.SaveSync = saveSync?.Request;
                            json = LiveJson.SerializeLiveSnapshot(snapshot);
                            snapshot.SaveSync = null;
                        }
                    }
                    if (snapshot != null && snapshot.Sequence != lastSequence)
                    {
                        var write = writer.WriteLineAsync(json);
                        if (await Task.WhenAny(write, Task.Delay(2000, stop)) != write) break;
                        await write;
                        lastSequence = snapshot.Sequence;
                        var read = ReadReply(reader);
                        if (await Task.WhenAny(read, Task.Delay(saveSync != null ? 6000 : 3000, stop)) != read) { saveSync?.Finish("The connection to Companion stopped, so the save loaded as it was."); break; }
                        var reply = LiveJson.Deserialize<LiveCommandReply>(await read);
                        if (reply.Token != discovery.Token) { saveSync?.Finish("Companion's reply was rejected, so the save loaded as it was."); break; }
                        lock (_sync)
                        {
                            _command = reply.Command ?? _command;
                            _autoSyncSlots = reply.AutoSyncSlots;
                            _repliedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                        }
                        if (saveSync != null)
                            saveSync.Finish(reply.SaveSyncResult is { } result && result.Id == saveSync.Request.Id
                                ? result.Message
                                : "Companion is too old to sync before load, so the save loaded as it was.");
                    }
                    await Task.Delay(200, stop);
                }
            }
            catch (Exception exception) when (exception is IOException || exception is SocketException || exception is UnauthorizedAccessException || exception is OperationCanceledException || exception is System.Runtime.Serialization.SerializationException || exception is ArgumentException || exception is ObjectDisposedException) { }
            try { await Task.Delay(1000, stop); }
            catch (OperationCanceledException) { }
        }
    }

    private static async Task<string> ReadReply(StreamReader reader)
    {
        var text = new StringBuilder();
        var character = new char[1];
        while (await reader.ReadAsync(character, 0, 1) != 0)
        {
            if (character[0] == '\n') return text.ToString();
            if (text.Length >= 4096) throw new IOException("Command exceeds the size limit.");
            text.Append(character[0]);
        }
        throw new IOException("Command connection closed.");
    }
}
