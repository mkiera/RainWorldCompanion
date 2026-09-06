using System.Net;
using System.Net.Sockets;
using System.Text;
using RainWorldCompanion.Core.Live;
using RainWorldCompanion.LiveProtocol;

namespace RainWorldCompanion.Tests;

public sealed class LiveConnectionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Player_life_state_survives_the_connection(bool? dead)
    {
        using var fixture = new ServerFixture();
        using var client = await fixture.Connect();
        await fixture.Send(client, new LiveSnapshot
        {
            SessionId = "life-state", Sequence = 1,
            Players = [new() { Id = "peer", Name = "Peer", Dead = dead }]
        });
        await WaitFor(() => fixture.Server.Status == LiveConnectionStatus.Connected);
        Assert.Equal(dead, fixture.Server.Snapshot!.Players.Single().Dead);
    }

    [Fact]
    public async Task Authenticated_snapshots_preserve_room_casing_and_disconnect_clears_locations()
    {
        using var fixture = new ServerFixture();
        using var client = await fixture.Connect();
        await fixture.Send(client, new LiveSnapshot { SessionId = "one", Sequence = 1, State = "gameplay", Players = [new() { Id = "local:0", RoomId = "MS_bittershelter", Name = "Player 1" }] });
        await WaitFor(() => fixture.Server.Status == LiveConnectionStatus.Connected);
        Assert.Equal("MS_bittershelter", fixture.Server.Snapshot!.Players[0].RoomId);
        Assert.Equal("", fixture.Server.Snapshot.Token);
        var diagnostics = fixture.Server.CaptureDiagnostics();
        Assert.Equal(1, diagnostics.Connections);
        Assert.Equal(1, diagnostics.Accepted);
        Assert.True(diagnostics.Bytes > 0);
        Assert.NotNull(diagnostics.LastAccepted);
        Assert.Equal(ProtocolInfo.Version, diagnostics.ObservedProtocol);
        Assert.Equal("", diagnostics.Snapshot!.Token);
        client.Close();
        await WaitFor(() => fixture.Server.Status == LiveConnectionStatus.Disconnected);
        Assert.Null(fixture.Server.Snapshot);
    }

    [Fact]
    public async Task Wrong_token_is_rejected_then_a_valid_client_can_connect()
    {
        using var fixture = new ServerFixture();
        using (var invalid = await fixture.Connect())
        {
            await fixture.Send(invalid, new LiveSnapshot { Token = "invalid", SessionId = "bad", Sequence = 1 }, false);
            await Task.Delay(100);
            Assert.Null(fixture.Server.Snapshot);
        }
        using var valid = await fixture.Connect();
        await fixture.Send(valid, new LiveSnapshot { SessionId = "good", Sequence = 1 });
        await WaitFor(() => fixture.Server.Status == LiveConnectionStatus.Connected);
    }

    [Fact]
    public async Task Protocol_mismatch_is_reported_without_exposing_locations()
    {
        using var fixture = new ServerFixture();
        using var client = await fixture.Connect();
        await fixture.Send(client, new LiveSnapshot { SessionId = "one", Sequence = 1, ProtocolVersion = 2 });
        await WaitFor(() => fixture.Server.Status == LiveConnectionStatus.Incompatible);
        Assert.Null(fixture.Server.Snapshot);
        Assert.Equal(2, fixture.Server.CaptureDiagnostics().ObservedProtocol);
        Assert.Equal(1, fixture.Server.CaptureDiagnostics().Rejected);
    }

    [Fact]
    public async Task Reconnect_replaces_session_and_older_sequence_does_not_replace_snapshot()
    {
        using var fixture = new ServerFixture();
        using (var first = await fixture.Connect())
        {
            await fixture.Send(first, new LiveSnapshot { SessionId = "one", Sequence = 2, Campaign = "White" });
            await WaitFor(() => fixture.Server.Snapshot?.Sequence == 2);
            await fixture.Send(first, new LiveSnapshot { SessionId = "one", Sequence = 1, Campaign = "Saint" });
            await Task.Delay(100);
            Assert.Equal("White", fixture.Server.Snapshot!.Campaign);
        }
        using var second = await fixture.Connect();
        await fixture.Send(second, new LiveSnapshot { SessionId = "two", Sequence = 1, Campaign = "Yellow" });
        await WaitFor(() => fixture.Server.Snapshot?.SessionId == "two");
        Assert.Equal("Yellow", fixture.Server.Snapshot!.Campaign);
    }

    [Fact]
    public async Task Missing_heartbeat_expires_connection()
    {
        using var fixture = new ServerFixture();
        using var client = await fixture.Connect();
        await fixture.Send(client, new LiveSnapshot { SessionId = "one", Sequence = 1 });
        await WaitFor(() => fixture.Server.Status == LiveConnectionStatus.Connected);
        await WaitFor(() => fixture.Server.Status == LiveConnectionStatus.Disconnected);
        Assert.Null(fixture.Server.Snapshot);
    }

    [Fact]
    public async Task Different_install_is_not_accepted()
    {
        using var fixture = new ServerFixture(() => "C:\\Games\\Rain World");
        using var client = await fixture.Connect();
        await fixture.Send(client, new LiveSnapshot { SessionId = "one", Sequence = 1, GameInstallPath = "C:\\Other\\Rain World" });
        await Task.Delay(100);
        Assert.Null(fixture.Server.Snapshot);
    }

    [Theory]
    [InlineData("null\n")]
    [InlineData("{broken}\n")]
    public async Task Malformed_message_does_not_stop_the_listener(string message)
    {
        using var fixture = new ServerFixture();
        using (var invalid = await fixture.Connect())
            await invalid.GetStream().WriteAsync(Encoding.UTF8.GetBytes(message));
        using var valid = await fixture.Connect();
        await fixture.Send(valid, new LiveSnapshot { SessionId = "valid", Sequence = 1 });
        await WaitFor(() => fixture.Server.Status == LiveConnectionStatus.Connected);
    }

    [Fact]
    public async Task Oversized_message_is_rejected_and_listener_recovers()
    {
        using var fixture = new ServerFixture();
        using (var invalid = await fixture.Connect())
            await invalid.GetStream().WriteAsync(Encoding.UTF8.GetBytes(new string('x', ProtocolInfo.MaximumMessageLength + 1) + "\n"));
        using var valid = await fixture.Connect();
        await fixture.Send(valid, new LiveSnapshot { SessionId = "valid", Sequence = 1 });
        await WaitFor(() => fixture.Server.Status == LiveConnectionStatus.Connected);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(condition(), "Expected live connection state was not reached.");
    }

    private sealed class ServerFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "rw-live-" + Guid.NewGuid().ToString("N"));
        internal LiveConnectionServer Server { get; }
        private readonly LiveDiscovery _discovery;
        internal ServerFixture(Func<string?>? install = null)
        {
            Server = new LiveConnectionServer(_directory, install);
            Server.Start();
            _discovery = LiveJson.Deserialize<LiveDiscovery>(File.ReadAllText(Path.Combine(_directory, "endpoint.json")));
        }
        internal async Task<TcpClient> Connect()
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, _discovery.Port);
            return client;
        }
        internal async Task Send(TcpClient client, LiveSnapshot snapshot, bool authenticate = true)
        {
            if (authenticate) snapshot.Token = _discovery.Token;
            var bytes = Encoding.UTF8.GetBytes(LiveJson.Serialize(snapshot) + "\n");
            await client.GetStream().WriteAsync(bytes);
        }
        public void Dispose()
        {
            Server.Dispose();
            Directory.Delete(_directory, true);
        }
    }
}
