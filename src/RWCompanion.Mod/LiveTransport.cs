using System.Net;
using System.Net.Sockets;
using System.Text;
using RainWorldCompanion.LiveProtocol;

namespace RWCompanion.Mod;

internal sealed class LiveTransport
{
    private readonly object _sync = new();
    private LiveSnapshot? _latest;
    private long _publishedAt;
    private CancellationTokenSource? _stop;
    private TcpClient? _client;

    internal void Publish(LiveSnapshot snapshot)
    {
        lock (_sync)
        {
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
                var discovery = LiveJson.Deserialize<LiveDiscovery>(File.ReadAllText(Path.Combine(ProtocolInfo.DiscoveryDirectory, "endpoint.json")));
                using var client = new TcpClient();
                _client = client;
                var connect = client.ConnectAsync(IPAddress.Loopback, discovery.Port);
                if (await Task.WhenAny(connect, Task.Delay(2000, stop)) != connect) continue;
                await connect;
                using var registration = stop.Register(client.Close);
                using var writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
                long lastSequence = -1;
                while (!stop.IsCancellationRequested)
                {
                    LiveSnapshot? snapshot;
                    lock (_sync) snapshot = (System.Diagnostics.Stopwatch.GetTimestamp() - _publishedAt) / (double)System.Diagnostics.Stopwatch.Frequency < 2 ? _latest : null;
                    if (snapshot != null && snapshot.Sequence != lastSequence)
                    {
                        snapshot.Token = discovery.Token;
                        string json = LiveJson.Serialize(snapshot);
                        if (json.Length <= ProtocolInfo.MaximumMessageLength)
                        {
                            var write = writer.WriteLineAsync(json);
                            if (await Task.WhenAny(write, Task.Delay(2000, stop)) != write) break;
                            await write;
                            lastSequence = snapshot.Sequence;
                        }
                    }
                    await Task.Delay(200, stop);
                }
            }
            catch (Exception exception) when (exception is IOException || exception is SocketException || exception is UnauthorizedAccessException || exception is OperationCanceledException || exception is System.Runtime.Serialization.SerializationException || exception is ArgumentException || exception is ObjectDisposedException) { }
            try { await Task.Delay(1000, stop); }
            catch (OperationCanceledException) { }
        }
    }
}
