using System.Net;
using System.Net.Sockets;
using System.Text;
using RainWorldCompanion.Core.Live;
using RainWorldCompanion.LiveProtocol;

namespace RainWorldCompanion.Tests;

public sealed class LogBridgeTests
{
    [Fact]
    public async Task Authenticated_exchange_relays_bounded_packets_and_advertisement()
    {
        LogBridgeUpstream? accepted = null;
        using var server = new LogBridgeServer(message =>
        {
            accepted = message;
            return new()
            {
                Advertisement = new()
                {
                    Available = true,
                    DeepTraceEnabled = true,
                    CaptureId = "capture",
                    CaptureToken = "secret"
                },
                OutgoingPackets = [new() { PeerSteamId = "76561198000000002", Payload = [4, 5, 6] }]
            };
        });
        var endpoint = server.Start();
        using var client = await Connect(endpoint.Port);
        await Send(client, new()
        {
            Token = endpoint.Token,
            Sequence = 1,
            GameSessionId = "game",
            Lobby = new()
            {
                IsConnected = true,
                IsSteam = true,
                LobbyId = "lobby",
                LocalSteamId = "76561198000000001",
                LocalDisplayName = "Sender",
                LocalIsHost = true,
                Peers = [new() { SteamId = "76561198000000002", DisplayName = "Receiver" }]
            },
            ReceivedPackets = [new() { PeerSteamId = "76561198000000002", Payload = [1, 2, 3] }]
        });
        using var reader = new StreamReader(client.GetStream(), Encoding.UTF8, false, 4096, true);
        var reply = LiveJson.Deserialize<LogBridgeDownstream>((await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))!);
        Assert.Equal(endpoint.Token, reply.Token);
        Assert.True(reply.Advertisement.Available);
        Assert.True(reply.Advertisement.DeepTraceEnabled);
        Assert.Equal([4, 5, 6], reply.OutgoingPackets.Single().Payload);
        Assert.NotNull(accepted);
        Assert.Equal("", accepted.Token);
        Assert.Equal("Sender", accepted.Lobby.LocalDisplayName);
        Assert.True(accepted.Lobby.LocalIsHost);
        Assert.Equal([1, 2, 3], accepted.ReceivedPackets.Single().Payload);
        Assert.Equal(LogBridgeStatus.Connected, server.CaptureDiagnostics().Status);
    }

    [Fact]
    public void Maximum_bridge_batch_fits_the_bounded_line_protocol()
    {
        var upstream = new LogBridgeUpstream
        {
            Token = new string('\u4e00', 128),
            GameInstallPath = "C:\\" + new string('\u4e00', 4093),
            GameSessionId = new string('s', 64),
            Sequence = long.MaxValue,
            Lobby = new()
            {
                IsConnected = true,
                IsSteam = true,
                LobbyId = new string('l', 64),
                LocalSteamId = new string('1', 32),
                LocalDisplayName = new string('\u4e00', 128),
                LocalIsHost = true,
                Peers = Enumerable.Range(0, 64).Select(index => new LogLobbyPeer
                {
                    SteamId = index.ToString().PadLeft(32, '1'),
                    DisplayName = new string('\u4e00', 128),
                    SupportsLogStreaming = true,
                    ProtocolVersion = ProtocolInfo.LogStreamingVersion,
                    ReceiverAvailable = true,
                    DeepTraceEnabled = true,
                    CaptureId = new string('\u4e00', 64),
                    CaptureToken = new string('\u4e00', 128),
                    LastSeenUtcTicks = long.MaxValue
                }).ToArray()
            },
            ReceivedPackets = Enumerable.Range(0, ProtocolInfo.MaximumLogPacketsPerBridgeExchange)
                .Select(_ => new LogRelayPacket
                {
                    PeerSteamId = new string('2', 32),
                    Payload = new byte[ProtocolInfo.MaximumLogPacketLength]
                }).ToArray()
        };
        var downstream = new LogBridgeDownstream
        {
            Token = new string('\u4e00', 128),
            Advertisement = new()
            {
                Available = true,
                CaptureActive = true,
                DeepTraceEnabled = true,
                CaptureId = new string('\u4e00', 64),
                CaptureToken = new string('\u4e00', 128)
            },
            OutgoingPackets = Enumerable.Range(0, ProtocolInfo.MaximumLogPacketsPerBridgeExchange)
                .Select(_ => new LogRelayPacket
                {
                    PeerSteamId = new string('2', 32),
                    Payload = new byte[ProtocolInfo.MaximumLogPacketLength]
                }).ToArray()
        };

        int upstreamLength = Encoding.UTF8.GetByteCount(LiveJson.Serialize(upstream));
        int downstreamLength = Encoding.UTF8.GetByteCount(LiveJson.Serialize(downstream));

        Assert.Equal(512 * 1024, ProtocolInfo.MaximumLogBridgeMessageLength);
        Assert.Equal(6, ProtocolInfo.MaximumLogPacketsPerBridgeExchange);
        Assert.True(upstreamLength <= ProtocolInfo.MaximumLogBridgeMessageLength,
            $"A valid maximum upstream bridge batch serialized to {upstreamLength:N0} bytes.");
        Assert.True(downstreamLength <= ProtocolInfo.MaximumLogBridgeMessageLength,
            $"A valid maximum downstream bridge batch serialized to {downstreamLength:N0} bytes.");
    }

    [Fact]
    public void Log_protocol_serialization_omits_unused_fields()
    {
        string bridge = LiveJson.Serialize(new LogBridgeUpstream());
        string network = LiveJson.Serialize(new LogStreamNetworkMessage());

        Assert.DoesNotContain("\"ModVersion\"", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("\"LocalName\"", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("\"LobbyPeerId\"", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("\"SentUtcTicks\"", network, StringComparison.Ordinal);
    }

    [Fact]
    public void Live_mod_inventory_round_trips_code_identity_without_paths()
    {
        var snapshot = new LiveSnapshot
        {
            ActiveModsTruncated = true,
            ActiveMods =
            [
                new()
                {
                    Id = "example.mod",
                    DisplayName = "Example mod",
                    Version = "2.0.0",
                    CodeFingerprint = new string('a', 64),
                    FingerprintStatus = "complete"
                }
            ]
        };

        string json = LiveJson.Serialize(snapshot);
        var restored = LiveJson.Deserialize<LiveSnapshot>(json);

        Assert.True(restored.ActiveModsTruncated);
        var mod = Assert.Single(restored.ActiveMods);
        Assert.Equal("example.mod", mod.Id);
        Assert.Equal("Example mod", mod.DisplayName);
        Assert.Equal("2.0.0", mod.Version);
        Assert.Equal(new string('a', 64), mod.CodeFingerprint);
        Assert.Equal("complete", mod.FingerprintStatus);
        Assert.DoesNotContain("ModRoot", json, StringComparison.Ordinal);
        Assert.DoesNotContain("TargetedRoot", json, StringComparison.Ordinal);
        Assert.DoesNotContain("NewestRoot", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Older_live_snapshot_without_mod_inventory_defaults_to_an_empty_inventory()
    {
        var snapshot = LiveJson.Deserialize<LiveSnapshot>("{\"SessionId\":\"legacy\",\"Sequence\":1}");

        Assert.Empty(snapshot.ActiveMods);
        Assert.False(snapshot.ActiveModsTruncated);
    }

    [Fact]
    public void Maximum_active_mod_inventory_leaves_room_in_the_live_message_budget()
    {
        var snapshot = new LiveSnapshot
        {
            SessionId = "session",
            Sequence = long.MaxValue,
            ActiveMods = Enumerable.Range(0, ProtocolInfo.MaximumActiveMods).Select(_ => new LiveModInfo
            {
                Id = new string('\u4e00', ProtocolInfo.MaximumModIdLength),
                DisplayName = new string('\u4e00', ProtocolInfo.MaximumModDisplayNameLength),
                Version = new string('\u4e00', ProtocolInfo.MaximumModVersionLength),
                CodeFingerprint = new string('f', 64),
                FingerprintStatus = "complete"
            }).ToArray()
        };

        int length = Encoding.UTF8.GetByteCount(LiveJson.Serialize(snapshot));

        Assert.True(length <= ProtocolInfo.MaximumMessageLength * 3 / 4,
            $"A maximum active-mod inventory used {length:N0} of {ProtocolInfo.MaximumMessageLength:N0} bytes.");
    }

    [Fact]
    public async Task Deep_trace_preference_without_an_available_receiver_is_rejected_on_both_bridge_directions()
    {
        int exchanges = 0;
        using var server = new LogBridgeServer(_ =>
        {
            exchanges++;
            return new() { Advertisement = new() { DeepTraceEnabled = true } };
        });
        var endpoint = server.Start();

        using (var invalidPeer = await Connect(endpoint.Port))
        {
            await Send(invalidPeer, new()
            {
                Token = endpoint.Token,
                Sequence = 1,
                GameSessionId = "game",
                Lobby = new()
                {
                    Peers =
                    [
                        new()
                        {
                            SteamId = "76561198000000002",
                            DisplayName = "Receiver",
                            DeepTraceEnabled = true
                        }
                    ]
                }
            });
            await WaitUntil(
                () => server.CaptureDiagnostics().LastEvent.Contains("rejected", StringComparison.Ordinal),
                () => server.CaptureDiagnostics().LastEvent);
        }
        Assert.Equal(0, exchanges);

        using var invalidReply = await Connect(endpoint.Port);
        await Send(invalidReply, new()
        {
            Token = endpoint.Token,
            Sequence = 1,
            GameSessionId = "game",
            Lobby = new()
        });
        await WaitUntil(
            () => exchanges == 1 && server.CaptureDiagnostics().LastEvent.Contains("rejected", StringComparison.Ordinal),
            () => server.CaptureDiagnostics().LastEvent);
    }

    [Fact]
    public async Task Wrong_install_is_rejected_without_invoking_the_coordinator()
    {
        bool invoked = false;
        using var server = new LogBridgeServer(message => { invoked = true; return new(); }, () => "C:\\Games\\Rain World");
        var endpoint = server.Start();
        using var client = await Connect(endpoint.Port);
        await Send(client, new()
        {
            Token = endpoint.Token,
            Sequence = 1,
            GameInstallPath = "C:\\Other\\Rain World",
            GameSessionId = "game",
            Lobby = new()
        });
        await Task.Delay(100);
        Assert.False(invoked);
        Assert.Null(server.CaptureDiagnostics().Lobby);
    }

    [Fact]
    public async Task Null_bridge_fields_are_rejected_and_a_later_valid_connection_still_works()
    {
        int exchanges = 0;
        using var server = new LogBridgeServer(_ => { exchanges++; return new(); });
        var endpoint = server.Start();

        using (var missingLobby = await Connect(endpoint.Port))
        {
            await Send(missingLobby, new()
            {
                Token = endpoint.Token,
                Sequence = 1,
                GameSessionId = "game",
                Lobby = null!
            });
            await WaitUntil(
                () => server.CaptureDiagnostics().LastEvent.Contains("rejected", StringComparison.Ordinal),
                () => server.CaptureDiagnostics().LastEvent);
        }
        using var valid = await Connect(endpoint.Port);
        await Send(valid, new()
        {
            Token = endpoint.Token,
            Sequence = 1,
            GameSessionId = "game",
            Lobby = new()
            {
                IsConnected = true,
                IsSteam = true,
                LobbyId = "lobby",
                LocalSteamId = "76561198000000001"
            }
        });
        var reply = LiveJson.Deserialize<LogBridgeDownstream>((await ReadLine(valid.GetStream()))!);
        Assert.Equal(endpoint.Token, reply.Token);
        Assert.Equal(1, exchanges);
        Assert.Equal(LogBridgeStatus.Connected, server.CaptureDiagnostics().Status);
    }

    private static async Task<TcpClient> Connect(int port)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        return client;
    }

    private static Task Send(TcpClient client, LogBridgeUpstream message)
        => client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(LiveJson.Serialize(message) + "\n")).AsTask();

    private static async Task<string?> ReadLine(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true);
        return await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task WaitUntil(Func<bool> condition, Func<string> diagnostic)
    {
        for (int attempt = 0; attempt < 100 && !condition(); attempt++)
            await Task.Delay(20);
        Assert.True(condition(), "The log bridge did not reach the expected state. Last event: " + diagnostic());
    }
}
