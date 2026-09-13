using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RainWorldCompanion.ViewModels;
using RainWorldCompanion.Views;

namespace RainWorldCompanion.App.Tests;

public class LogStreamingViewModelTests
{
    [Fact]
    public void Disclosure_distinguishes_the_three_game_logs_from_diagnostics_and_explains_deep_trace_consent()
    {
        Assert.Equal(
            ["consoleLog.txt", "exceptionLog.txt", "BepInEx/LogOutput.log"],
            LogStreamingViewModel.SharedLogNames);
        Assert.All(LogStreamingViewModel.SharedLogNames,
            name => Assert.Contains(name, LogStreamingViewModel.DisclosureText));
        Assert.Contains("structured diagnostic event timeline", LogStreamingViewModel.DisclosureText);
        Assert.Contains("active mod IDs, names, stated versions, and SHA-256 fingerprints", LogStreamingViewModel.DisclosureText);
        Assert.Contains("identify an exact or private mod build", LogStreamingViewModel.DisclosureText);
        Assert.Contains("Mod paths, configurations, and file contents are not sent", LogStreamingViewModel.DisclosureText);
        Assert.Contains("switch Deep trace on or off later without another approval", LogStreamingViewModel.DisclosureText);
        Assert.Contains("player positions, inputs, and game performance", LogStreamingViewModel.DisclosureText);
        Assert.Contains("does not include chat or arbitrary files", LogStreamingViewModel.DisclosureText);
        Assert.Contains("Switching Deep trace off leaves the three game logs and diagnostic events streaming",
            LogStreamingViewModel.DisclosureText);
        Assert.DoesNotContain("Companion/events.jsonl", LogStreamingViewModel.SharedLogNames);
        Assert.DoesNotContain("Companion/deep-trace.jsonl", LogStreamingViewModel.SharedLogNames);
        var view = new LogStreamingViewModel();
        Assert.Equal(
            ["", "consoleLog.txt", "exceptionLog.txt", "BepInEx/LogOutput.log", "Companion/events.jsonl", "Companion/deep-trace.jsonl"],
            view.FileFilters.Select(filter => filter.Id));
    }

    [Fact]
    public async Task Receiver_and_capture_commands_send_distinct_intents()
    {
        var controller = new FakeLogStreamingController(Snapshot(advertised: false));
        var view = new LogStreamingViewModel(controller);
        view.Refresh();
        Assert.False(view.ToggleDeepTraceCommand.CanExecute(null));

        await view.ToggleReceiverAdvertisementCommand.ExecuteAsync(null);
        Assert.Equal([true], controller.ReceiverAvailability);
        Assert.True(view.ToggleDeepTraceCommand.CanExecute(null));

        await view.StartCaptureCommand.ExecuteAsync(null);
        Assert.Equal(LogStreamingCaptureState.Capturing, controller.CaptureStates[^1]);

        await view.PauseCaptureCommand.ExecuteAsync(null);
        Assert.Equal(LogStreamingCaptureState.Paused, controller.CaptureStates[^1]);

        await view.StopCaptureCommand.ExecuteAsync(null);
        Assert.Equal(LogStreamingCaptureState.Stopped, controller.CaptureStates[^1]);
    }

    [Fact]
    public async Task Receiver_can_switch_deep_trace_during_capture_without_changing_consent_or_normal_capture()
    {
        var controller = new FakeLogStreamingController(Snapshot(
            advertised: true, captureState: LogStreamingCaptureState.Capturing));
        var view = new LogStreamingViewModel(controller);
        view.Refresh();
        Assert.True(view.ToggleDeepTraceCommand.CanExecute(null));

        await view.ToggleDeepTraceCommand.ExecuteAsync(null);

        Assert.Equal([true], controller.DeepTraceStates);
        Assert.True(view.DeepTraceEnabled);
        Assert.Equal(LogStreamingCaptureState.Capturing, view.CaptureState);
        Assert.Empty(controller.Prepared);
        Assert.Empty(controller.Revoked);

        await view.ToggleDeepTraceCommand.ExecuteAsync(null);

        Assert.Equal([true, false], controller.DeepTraceStates);
        Assert.False(view.DeepTraceEnabled);
        Assert.Equal(LogStreamingCaptureState.Capturing, view.CaptureState);
        Assert.Contains("Normal logs and diagnostic events continue", view.DeepTraceStatusText);
    }

    [Fact]
    public async Task Prepare_sharing_includes_every_selected_compatible_receiver()
    {
        var controller = new FakeLogStreamingController(Snapshot(peers:
        [
            Peer("one", canReceive: true),
            Peer("two", canReceive: true, host: true),
            Peer("old", canReceive: false, state: LogStreamingPeerState.Unsupported)
        ]));
        var view = new LogStreamingViewModel(controller);
        view.Refresh();
        view.Peers.Single(peer => peer.Id == "one").IsSelectedReceiver = true;
        view.Peers.Single(peer => peer.Id == "two").IsSelectedReceiver = true;
        view.Peers.Single(peer => peer.Id == "old").IsSelectedReceiver = true;

        await view.PrepareSharingCommand.ExecuteAsync(null);

        Assert.Equal(["one", "two"], controller.Prepared.Single());
        Assert.Equal("HOST", view.Peers.Single(peer => peer.Id == "two").RoleText);
        Assert.Equal("CLIENT", view.Peers.Single(peer => peer.Id == "one").RoleText);
    }

    [Fact]
    public void Receiver_choices_show_authenticated_ids_when_display_names_match()
    {
        var first = Peer("76561198000000001", canReceive: true) with { DisplayName = "Same name" };
        var second = Peer("76561198000000002", canReceive: true) with { DisplayName = "Same name" };
        var view = new LogStreamingViewModel(new FakeLogStreamingController(Snapshot(peers: [first, second])));

        view.Refresh();

        Assert.Equal(2, view.ReceiverChoices.Count);
        Assert.All(view.ReceiverChoices, peer => Assert.Equal("Same name", peer.DisplayName));
        Assert.Equal(
            [
                "Steam ID 76561198000000001 (duplicate display name)",
                "Steam ID 76561198000000002 (duplicate display name)"
            ],
            view.ReceiverChoices.Select(peer => peer.IdentityText).Order(StringComparer.Ordinal).ToArray());
        Assert.All(view.ReceiverChoices, peer => Assert.True(peer.HasDuplicateDisplayName));
    }

    [Fact]
    public async Task Revoke_all_does_not_depend_on_receiver_selection()
    {
        var controller = new FakeLogStreamingController(Snapshot(peers:
        [
            Peer("one", canReceive: true, receivingMyLogs: true),
            Peer("two", canReceive: true, receivingMyLogs: true)
        ]));
        var view = new LogStreamingViewModel(controller);
        view.Refresh();
        Assert.True(view.RevokeAllSharingCommand.CanExecute(null));

        await view.RevokeAllSharingCommand.ExecuteAsync(null);

        Assert.True(controller.RevokedAll);
    }

    [Theory]
    [InlineData(LogStreamingPeerState.Unsupported, "Unsupported", LogStreamingStatusTone.Danger)]
    [InlineData(LogStreamingPeerState.NotSharing, "Not sharing with you", LogStreamingStatusTone.Danger)]
    [InlineData(LogStreamingPeerState.Ready, "Ready", LogStreamingStatusTone.Success)]
    [InlineData(LogStreamingPeerState.Streaming, "Streaming", LogStreamingStatusTone.Success)]
    [InlineData(LogStreamingPeerState.Reconnecting, "Reconnecting", LogStreamingStatusTone.Warning)]
    [InlineData(LogStreamingPeerState.ReceiverPaused, "Your capture is paused", LogStreamingStatusTone.Warning)]
    [InlineData(LogStreamingPeerState.StorageLimited, "Storage limit reached", LogStreamingStatusTone.Warning)]
    [InlineData(LogStreamingPeerState.Disconnected, "Disconnected", LogStreamingStatusTone.Muted)]
    public void Peer_state_has_text_as_well_as_a_colour(
        LogStreamingPeerState state, string text, LogStreamingStatusTone tone)
    {
        var row = new LogStreamingPeerViewModel(Peer("peer", state: state), false);

        Assert.Equal(text, row.Incoming.StatusText);
        Assert.Equal(tone, row.Incoming.StatusTone);
    }

    [Fact]
    public async Task Incoming_and_outgoing_states_are_independent_and_only_outgoing_is_selectable()
    {
        var peer = Peer("two", canReceive: true, receivingMyLogs: true) with
        {
            Incoming = Direction(LogStreamingPeerState.Streaming),
            Outgoing = Direction(LogStreamingPeerState.ReceiverPaused)
        };
        var controller = new FakeLogStreamingController(Snapshot(peers: [peer]));
        var view = new LogStreamingViewModel(controller);
        view.Refresh();
        var row = Assert.Single(view.Peers);

        Assert.Equal("Streaming", row.Incoming.StatusText);
        Assert.Equal("Receiver paused", row.Outgoing.StatusText);
        Assert.True(row.IsSelectedReceiver);

        await view.RevokeSelectedSharingCommand.ExecuteAsync(null);
        Assert.Equal(["two"], controller.Revoked.Single());
    }

    [Theory]
    [InlineData(false, "Receiving your logs. Deep trace off.")]
    [InlineData(true, "Receiving your logs. Deep trace on.")]
    public void Approved_sender_row_shows_the_receivers_current_deep_trace_state(
        bool deepTraceEnabled, string expected)
    {
        var row = new LogStreamingPeerViewModel(Peer(
            "receiver", canReceive: true, receivingMyLogs: true,
            receiverDeepTraceEnabled: deepTraceEnabled), false);

        Assert.Equal(expected, row.ReceiverChoiceText);
    }

    [Fact]
    public void Viewer_filters_literal_text_and_keeps_a_bounded_tail()
    {
        var lines = Enumerable.Range(0, LogStreamingViewModel.MaximumViewerRows + 7)
            .Select(index => new LogStreamingLineUiState
            {
                Sequence = index,
                Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(index),
                SenderId = index % 2 == 0 ? "one" : "two",
                SenderName = index % 2 == 0 ? "One" : "Two",
                FileName = index % 3 == 0 ? "exceptionLog.txt" : "consoleLog.txt",
                Text = index == LogStreamingViewModel.MaximumViewerRows + 6 ? "literal [value]." : "ordinary"
            })
            .ToArray();
        var controller = new FakeLogStreamingController(Snapshot(lines: lines));
        var view = new LogStreamingViewModel(controller);
        view.Refresh();

        Assert.Equal(LogStreamingViewModel.MaximumViewerRows, view.VisibleLogLines.Count);
        Assert.Equal(7, view.VisibleLogLines[0].Sequence);

        view.SearchText = "[value].";
        Assert.Single(view.VisibleLogLines);
        Assert.Equal("literal [value].", view.VisibleLogLines[0].Text);

        view.SearchText = "";
        view.SelectedSenderId = "one";
        view.SelectedFileName = "exceptionLog.txt";
        Assert.All(view.VisibleLogLines, line =>
        {
            Assert.Equal("one", line.SenderId);
            Assert.Equal("exceptionLog.txt", line.FileName);
        });
    }

    [Fact]
    public void Charts_only_include_the_last_minute()
    {
        var now = DateTimeOffset.UtcNow;
        var controller = new FakeLogStreamingController(Snapshot(now: now, samples:
        [
            new(now.AddSeconds(-61), 10, 20, 1),
            new(now.AddSeconds(-30), 20, 30, 2),
            new(now, 40, 50, 4)
        ]));
        var view = new LogStreamingViewModel(controller);

        view.Refresh();

        Assert.Equal(2, view.ThroughputPoints.Count);
        Assert.Equal(2, view.BacklogPoints.Count);
        Assert.Equal(2, view.AcknowledgementAgePoints.Count);
        Assert.Equal(300, view.ThroughputPoints[0].X, 6);
        Assert.Equal(600, view.ThroughputPoints[1].X, 6);
    }

    [Fact]
    public void Backlog_is_only_reported_for_logs_sent_by_this_app()
    {
        var peer = Peer("one") with
        {
            Incoming = Direction(LogStreamingPeerState.Streaming) with
            {
                BacklogBytes = 900,
                AcknowledgementAge = TimeSpan.FromSeconds(9)
            },
            Outgoing = Direction(LogStreamingPeerState.Streaming) with
            {
                BacklogBytes = 25,
                AcknowledgementAge = TimeSpan.FromSeconds(2)
            }
        };
        var controller = new FakeLogStreamingController(Snapshot(peers: [peer]));
        var view = new LogStreamingViewModel(controller);

        view.Refresh();

        var row = Assert.Single(view.Peers);
        Assert.Equal("Not reported", row.Incoming.BacklogText);
        Assert.Equal("25 B", row.Outgoing.BacklogText);
        Assert.Equal("25 B", view.CurrentBacklogText);
        Assert.Equal("LAST FLUSH", row.Incoming.AcknowledgementAgeLabel);
        Assert.Equal("ACK AGE", row.Outgoing.AcknowledgementAgeLabel);
        Assert.Equal("2.0 s", view.LongestAcknowledgementAgeText);
    }

    [Fact]
    public void Log_streaming_view_loads_and_lays_out_with_app_resources()
    {
        var failure = WpfTestHost.Run(() =>
        {
            var resources = new ResourceDictionary();
            resources.MergedDictionaries.Add(LoadResource("Themes/Palette.Light.xaml"));
            resources.MergedDictionaries.Add(LoadResource("Themes/Controls.xaml"));
            resources.MergedDictionaries.Add(LoadResource("Theme.xaml"));
            Application.Current!.Resources = resources;

            var controller = new FakeLogStreamingController(Snapshot(peers:
            [
                Peer("one") with
                {
                    Incoming = Direction(LogStreamingPeerState.Streaming),
                    Outgoing = Direction(LogStreamingPeerState.Streaming)
                }
            ]));
            var viewModel = new LogStreamingViewModel(controller);
            viewModel.Refresh();
            var view = new LogStreamingView { DataContext = viewModel };
            var window = new Window
            {
                Content = view,
                Width = 960,
                Height = 900,
                Left = -10000,
                Top = -10000,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.ToolWindow
            };
            try
            {
                window.Show();
                window.UpdateLayout();
                string[] text = Descendants<TextBlock>(view).Select(item => item.Text).ToArray();
                Assert.Contains("LAST FLUSH", text);
                Assert.Contains("ACK AGE", text);
                Assert.Contains("CONFIRMED LOG THROUGHPUT", text);
                Assert.Contains("DEEP TRACE", text);
                Assert.Single(Descendants<CheckBox>(view), item => Equals(item.Content, "Deep trace"));
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Null(failure);
    }

    [Fact]
    public void Large_log_viewer_has_a_finite_virtualized_scroll_viewport()
    {
        var failure = WpfTestHost.Run(() =>
        {
            var resources = new ResourceDictionary();
            resources.MergedDictionaries.Add(LoadResource("Themes/Palette.Light.xaml"));
            resources.MergedDictionaries.Add(LoadResource("Themes/Controls.xaml"));
            resources.MergedDictionaries.Add(LoadResource("Theme.xaml"));
            Application.Current!.Resources = resources;

            var lines = Enumerable.Range(0, LogStreamingViewModel.MaximumViewerRows)
                .Select(index => new LogStreamingLineUiState
                {
                    Sequence = index,
                    Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(index),
                    SenderId = "one",
                    SenderName = "One",
                    FileName = "consoleLog.txt",
                    Text = "Log line " + index
                })
                .ToArray();
            var viewModel = new LogStreamingViewModel(new FakeLogStreamingController(Snapshot(lines: lines)));
            viewModel.Refresh();
            var view = new LogStreamingView { DataContext = viewModel };
            var window = new Window
            {
                Content = view,
                Width = 960,
                Height = 900,
                Left = -10000,
                Top = -10000,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.ToolWindow
            };
            try
            {
                window.Show();
                window.UpdateLayout();
                var list = Assert.IsType<ListBox>(view.FindName("LiveLogList"));
                ScrollViewer scroll = Assert.Single(Descendants<ScrollViewer>(list));

                Assert.InRange(list.ActualHeight, 1, 400);
                Assert.True(scroll.ScrollableHeight > 0);
                Assert.InRange(Descendants<ListBoxItem>(list).Count(), 1, 1999);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Null(failure);
    }

    [Fact]
    public void Viewer_choices_survive_live_refreshes()
    {
        var controller = new FakeLogStreamingController(Snapshot(peers: [Peer("one")], lines:
        [
            new() { SenderId = "one", SenderName = "One", FileName = "consoleLog.txt", Text = "needle" }
        ]));
        var view = new LogStreamingViewModel(controller);
        view.Refresh();
        view.SelectedSenderId = "one";
        view.SelectedFileName = "consoleLog.txt";
        view.SearchText = "needle";
        view.AutoScroll = false;
        controller.SetSnapshot(controller.Snapshot() with
        {
            Lines =
            [
                new() { Sequence = 1, SenderId = "one", SenderName = "One", FileName = "consoleLog.txt", Text = "needle again" }
            ]
        });

        view.Refresh();

        Assert.Equal("one", view.SelectedSenderId);
        Assert.Equal("consoleLog.txt", view.SelectedFileName);
        Assert.Equal("needle", view.SearchText);
        Assert.False(view.AutoScroll);
        Assert.Single(view.VisibleLogLines);
    }

    [Fact]
    public async Task Folder_and_event_actions_are_forwarded_without_file_access_in_the_view_model()
    {
        var controller = new FakeLogStreamingController(Snapshot(
            advertised: true, captureState: LogStreamingCaptureState.Capturing,
            captureFolder: "C:\\capture"));
        var view = new LogStreamingViewModel(controller) { EventNote = "Before gate" };
        view.Refresh();

        await view.OpenCaptureFolderCommand.ExecuteAsync(null);
        await view.MarkEventCommand.ExecuteAsync(null);

        Assert.True(controller.OpenedFolder);
        Assert.Equal(["Before gate"], controller.EventNotes);
        Assert.Equal("", view.EventNote);
    }

    private static LogStreamingUiState Snapshot(
        bool advertised = true,
        LogStreamingCaptureState captureState = LogStreamingCaptureState.Stopped,
        string captureFolder = "",
        IReadOnlyList<LogStreamingPeerUiState>? peers = null,
        IReadOnlyList<LogStreamingLineUiState>? lines = null,
        IReadOnlyList<LogStreamingChartUiSample>? samples = null,
        bool deepTraceEnabled = false,
        DateTimeOffset? now = null) => new()
    {
        ObservedAt = now ?? DateTimeOffset.UtcNow,
        IsSteamLobby = true,
        ReceiverAdvertised = advertised,
        DeepTraceEnabled = deepTraceEnabled,
        CaptureState = captureState,
        CaptureFolder = captureFolder,
        Peers = peers ?? [],
        Lines = lines ?? [],
        Samples = samples ?? []
    };

    private static LogStreamingPeerUiState Peer(
        string id,
        bool canReceive = false,
        bool receivingMyLogs = false,
        bool host = false,
        bool receiverDeepTraceEnabled = false,
        LogStreamingPeerState state = LogStreamingPeerState.NotSharing) => new()
    {
        Id = id,
        DisplayName = "Player " + id,
        IsHost = host,
        CanReceiveMyLogs = canReceive,
        IsReceivingMyLogs = receivingMyLogs,
        ReceiverDeepTraceEnabled = receiverDeepTraceEnabled,
        Incoming = Direction(state),
        Outgoing = Direction(state)
    };

    private static LogStreamingDirectionUiState Direction(LogStreamingPeerState state) => new()
    {
        State = state,
        LogSession = "session-1"
    };

    private static ResourceDictionary LoadResource(string path) => new()
    {
        Source = new Uri(
            "pack://application:,,,/RainWorldCompanion;component/" + path,
            UriKind.Absolute)
    };

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (T descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private sealed class FakeLogStreamingController(LogStreamingUiState snapshot) : ILogStreamingController
    {
        private LogStreamingUiState _snapshot = snapshot;

        public List<bool> ReceiverAvailability { get; } = [];
        public List<LogStreamingCaptureState> CaptureStates { get; } = [];
        public List<bool> DeepTraceStates { get; } = [];
        public List<IReadOnlyList<string>> Prepared { get; } = [];
        public List<IReadOnlyList<string>> Revoked { get; } = [];
        public List<string> EventNotes { get; } = [];
        public bool RevokedAll { get; private set; }
        public bool OpenedFolder { get; private set; }

        public LogStreamingUiState Snapshot() => _snapshot;

        public void SetSnapshot(LogStreamingUiState snapshot) => _snapshot = snapshot;

        public Task SetReceiverAvailabilityAsync(bool available)
        {
            ReceiverAvailability.Add(available);
            _snapshot = _snapshot with { ReceiverAdvertised = available };
            return Task.CompletedTask;
        }

        public Task SetCaptureStateAsync(LogStreamingCaptureState state)
        {
            CaptureStates.Add(state);
            _snapshot = _snapshot with { CaptureState = state };
            return Task.CompletedTask;
        }

        public Task SetDeepTraceEnabledAsync(bool enabled)
        {
            DeepTraceStates.Add(enabled);
            _snapshot = _snapshot with { DeepTraceEnabled = enabled };
            return Task.CompletedTask;
        }

        public Task PrepareSharingAsync(IReadOnlyList<string> receiverIds)
        {
            Prepared.Add(receiverIds);
            return Task.CompletedTask;
        }

        public Task RevokeSharingAsync(IReadOnlyList<string> receiverIds)
        {
            Revoked.Add(receiverIds);
            return Task.CompletedTask;
        }

        public Task RevokeAllSharingAsync()
        {
            RevokedAll = true;
            return Task.CompletedTask;
        }

        public Task OpenCaptureFolderAsync()
        {
            OpenedFolder = true;
            return Task.CompletedTask;
        }

        public Task MarkEventAsync(string note)
        {
            EventNotes.Add(note);
            return Task.CompletedTask;
        }
    }
}
