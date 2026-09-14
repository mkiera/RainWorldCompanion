using System.Diagnostics;
using System.IO;
using RainWorldCompanion.Core.Live;

namespace RainWorldCompanion.ViewModels;

internal sealed class LogStreamingController(
    LogStreamingCoordinator coordinator,
    Func<string, Task>? persistDestination = null) : ILogStreamingController
{
    public LogStreamingUiState Snapshot()
    {
        var snapshot = coordinator.Snapshot();
        return new()
        {
            ObservedAt = snapshot.ObservedAt,
            IsSteamLobby = snapshot.IsSteamLobby,
            ReceiverAdvertised = snapshot.ReceiverAdvertised,
            DeepTraceEnabled = snapshot.DeepTraceEnabled,
            CaptureState = snapshot.CaptureMode switch
            {
                LogStreamingCaptureMode.Capturing => LogStreamingCaptureState.Capturing,
                LogStreamingCaptureMode.Paused => LogStreamingCaptureState.Paused,
                _ => LogStreamingCaptureState.Stopped
            },
            CaptureFolder = snapshot.CaptureFolder,
            CaptureDestination = snapshot.DestinationRoot,
            Message = snapshot.Message,
            Peers = snapshot.Peers.Select(peer => new LogStreamingPeerUiState
            {
                Id = peer.SteamId,
                DisplayName = peer.DisplayName,
                IsHost = peer.IsHost,
                CanReceiveMyLogs = peer.CanReceive,
                IsReceivingMyLogs = peer.IsReceiving,
                ReceiverDeepTraceEnabled = peer.ReceiverDeepTraceEnabled,
                Incoming = Direction(peer.Incoming),
                Outgoing = Direction(peer.Outgoing)
            }).ToArray(),
            Lines = snapshot.Lines.Select(line => new LogStreamingLineUiState
            {
                Sequence = line.Sequence,
                Timestamp = line.Timestamp,
                SenderId = line.SenderId,
                SenderName = line.SenderName,
                FileName = line.FileId,
                Text = line.Text
            }).ToArray(),
            Samples = snapshot.Samples.Select(sample => new LogStreamingChartUiSample(
                sample.Time, sample.BytesPerSecond, sample.BacklogBytes,
                sample.AcknowledgementAge?.TotalSeconds)).ToArray()
        };
    }

    public Task SetReceiverAvailabilityAsync(bool available)
        => Task.Run(() => coordinator.SetReceiverAvailability(available));

    public Task SetCaptureStateAsync(LogStreamingCaptureState state)
        => Task.Run(() => coordinator.SetCaptureMode(state switch
        {
            LogStreamingCaptureState.Capturing => LogStreamingCaptureMode.Capturing,
            LogStreamingCaptureState.Paused => LogStreamingCaptureMode.Paused,
            _ => LogStreamingCaptureMode.Stopped
        }));

    public Task SetDeepTraceEnabledAsync(bool enabled)
        => Task.Run(() => coordinator.SetDeepTraceEnabled(enabled));

    public Task PrepareSharingAsync(IReadOnlyList<string> receiverIds)
        => Task.Run(() => coordinator.PrepareSharing(receiverIds));

    public Task RevokeSharingAsync(IReadOnlyList<string> receiverIds)
        => Task.Run(() => coordinator.RevokeSharing(receiverIds));

    public Task RevokeAllSharingAsync()
        => Task.Run(coordinator.RevokeAllSharing);

    public Task OpenCaptureFolderAsync()
    {
        string folder = coordinator.Snapshot().CaptureFolder;
        if (folder.Length == 0 || !Directory.Exists(folder))
            throw new DirectoryNotFoundException("No streamed-log capture folder is available yet.");
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    public async Task SetCaptureDestinationAsync(string path)
    {
        string previous = coordinator.Snapshot().DestinationRoot;
        await Task.Run(() => coordinator.SetDestinationRoot(path));
        if (persistDestination is null) return;
        try
        {
            await persistDestination(coordinator.Snapshot().DestinationRoot);
        }
        catch
        {
            await Task.Run(() => coordinator.SetDestinationRoot(previous));
            throw;
        }
    }

    public Task MarkEventAsync(string note)
        => Task.Run(() => coordinator.MarkEvent(string.IsNullOrWhiteSpace(note) ? "Manual event marker" : note));

    private static LogStreamingDirectionUiState Direction(LogStreamingDirectionSnapshot direction) => new()
    {
        State = direction.State switch
        {
            LogStreamingPeerMode.Unsupported => LogStreamingPeerState.Unsupported,
            LogStreamingPeerMode.Ready => LogStreamingPeerState.Ready,
            LogStreamingPeerMode.Streaming => LogStreamingPeerState.Streaming,
            LogStreamingPeerMode.Reconnecting => LogStreamingPeerState.Reconnecting,
            LogStreamingPeerMode.ReceiverPaused => LogStreamingPeerState.ReceiverPaused,
            LogStreamingPeerMode.StorageLimited => LogStreamingPeerState.StorageLimited,
            LogStreamingPeerMode.Disconnected => LogStreamingPeerState.Disconnected,
            _ => LogStreamingPeerState.NotSharing
        },
        Detail = direction.Detail,
        ThroughputBytesPerSecond = direction.BytesPerSecond,
        AcknowledgedBytes = direction.AcknowledgedBytes,
        BacklogBytes = direction.BacklogBytes,
        AcknowledgementAge = direction.AcknowledgementAge,
        ReconnectCount = direction.ReconnectCount,
        LogSession = direction.LogSession
    };
}
