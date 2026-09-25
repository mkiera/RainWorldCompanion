using System.IO;
using RainWorldCompanion.LiveProtocol;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.App.Tests;

public class MainLiveActionTests
{
    [Fact]
    public async Task Successful_action_records_request_and_completion_without_replacing_the_result()
    {
        var result = new LiveCommandResult { Success = true, Message = "Arrived." };
        List<RecordedAction> records = [];

        LiveCommandResult actual = await MainViewModel.RecordLiveActionAsync(
            () => Task.FromResult(result),
            (phase, action, player, room, region, success, message) =>
                records.Add(new(phase, action, player, room, region, success, message)),
            "teleport-self",
            "local:0",
            "SU_A43",
            "SU");

        Assert.Same(result, actual);
        RecordedAction[] expected = [
            new("requested", "teleport-self", "local:0", "SU_A43", "SU", null, null),
            new("completed", "teleport-self", "local:0", "SU_A43", "SU", true, "Arrived.")
        ];
        Assert.Equal(expected, records);
    }

    [Fact]
    public async Task Rejected_action_and_thrown_action_are_both_recorded_as_failures()
    {
        List<RecordedAction> records = [];
        var rejected = new LiveCommandResult { Message = "Player declined." };

        LiveCommandResult actual = await MainViewModel.RecordLiveActionAsync(
            () => Task.FromResult(rejected),
            Record,
            "teleport-player",
            "remote",
            "SU_A07",
            "SU");

        Assert.Same(rejected, actual);
        Assert.Equal("failed", records[1].Phase);
        Assert.False(records[1].Success);
        Assert.Equal("Player declined.", records[1].Message);

        var failure = new InvalidOperationException("Connection vanished.");
        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MainViewModel.RecordLiveActionAsync(
                () => Task.FromException<LiveCommandResult>(failure),
                Record,
                "recover",
                "local:0",
                null,
                null));

        Assert.Same(failure, thrown);
        Assert.Equal("failed", records[^1].Phase);
        Assert.False(records[^1].Success);
        Assert.Equal("Connection vanished.", records[^1].Message);

        void Record(string phase, string action, string? player, string? room, string? region, bool? success, string? message) =>
            records.Add(new(phase, action, player, room, region, success, message));
    }

    [Fact]
    public async Task Diagnostic_failure_does_not_block_or_change_the_live_action()
    {
        int calls = 0;
        var result = new LiveCommandResult { Success = true, Message = "Recovered." };

        LiveCommandResult actual = await MainViewModel.RecordLiveActionAsync(
            () =>
            {
                calls++;
                return Task.FromResult(result);
            },
            (_, _, _, _, _, _, _) => throw new IOException("Diagnostics unavailable."),
            "recover",
            "local:0",
            "SU_A43",
            "SU");

        Assert.Equal(1, calls);
        Assert.Same(result, actual);
    }

    private sealed record RecordedAction(
        string Phase,
        string Action,
        string? Player,
        string? Room,
        string? Region,
        bool? Success,
        string? Message);
}
