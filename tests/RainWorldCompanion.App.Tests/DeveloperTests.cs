using RainWorldCompanion.Core.Live;
using RainWorldCompanion.LiveProtocol;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.App.Tests;

public class DeveloperTests
{
    [Fact]
    public void Diagnostics_preserve_player_fields_omit_credentials_and_clear_expired_data()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new LiveSnapshot
        {
            Token = "private-credential", SessionId = "session", Sequence = 7,
            Campaign = "White", Timeline = "Rivulet",
            Players = [new() { Id = "peer:4", Name = "Peer", RoomId = "MS_bittershelter", Dead = null }]
        };
        var diagnostics = new LiveDiagnostics(LiveConnectionStatus.Connected, snapshot, 1234, "discovery",
            2, 8, 7, 0, 1, 1024, now, now, "Accepted", "1.0.2", 1);
        var view = new DeveloperViewModel();
        var live = new LiveSessionViewModel();
        live.AdoptSetup(true, true, "1.0.2", "Ready");
        view.Refresh(diagnostics, live, new Dictionary<string, string>(), now);
        Assert.Contains("MS_bittershelter", view.SnapshotJson);
        Assert.Contains("peer:4", view.SnapshotJson);
        Assert.DoesNotContain("Token", view.SnapshotJson);
        Assert.DoesNotContain("private-credential", view.SnapshotJson);
        var sequence = view.Categories.Single(c => c.Name == "Game session").Values.Single(v => v.Name == "Sequence");
        snapshot.Sequence = 8;
        view.Refresh(diagnostics, live, new Dictionary<string, string>(), now.AddSeconds(1));
        Assert.Equal("8", sequence.Value);
        view.Refresh(diagnostics with { Status = LiveConnectionStatus.Disconnected, Snapshot = null }, live,
            new Dictionary<string, string>(), now.AddSeconds(4));
        Assert.DoesNotContain("MS_bittershelter", view.SnapshotJson);
        Assert.Single(view.Categories.Single(c => c.Name == "Players").Values);
        Assert.Equal("True", view.Categories[0].Values.Single(v => v.Name == "Setup ready").Value);
    }
}
