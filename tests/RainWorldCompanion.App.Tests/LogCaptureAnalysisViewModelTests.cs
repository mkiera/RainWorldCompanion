using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using RainWorldCompanion.Core.LogStreaming.Analysis;
using RainWorldCompanion.Core.Mods;
using RainWorldCompanion.Controls;
using RainWorldCompanion.ViewModels;
using RainWorldCompanion.Views;

namespace RainWorldCompanion.App.Tests;

public sealed class LogCaptureAnalysisViewModelTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Same_version_with_different_fingerprints_is_a_visible_mismatch()
    {
        CaptureAnalysisSnapshot snapshot = Snapshot();
        var controller = new FakeCaptureController(snapshot);
        var view = new LogCaptureAnalysisViewModel(
            controller,
            _ => Task.FromResult<string?>(@"C:\capture"));

        await view.LoadCaptureCommand.ExecuteAsync(null);

        Assert.Equal("capture", view.CaptureName);
        CaptureModComparisonViewModel meadow = Assert.Single(view.ModComparison);
        Assert.True(meadow.HasMismatch);
        Assert.Equal("Same stated version, different code fingerprints", meadow.StatusText);
        Assert.Contains("Alice: 0.4.2", meadow.Builds, StringComparison.Ordinal);
        Assert.Contains("Bob: 0.4.2", meadow.Builds, StringComparison.Ordinal);
        Assert.Contains("aaaaaaaaaa", meadow.Builds, StringComparison.Ordinal);
        Assert.Contains("bbbbbbbbbb", meadow.Builds, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_or_partial_fingerprints_are_shown_as_unverified_not_matching()
    {
        CaptureAnalysisSnapshot snapshot = Snapshot() with
        {
            ModSnapshots =
            [
                Mods("alice", "Alice", new string('a', 64), "complete"),
                Mods("bob", "Bob", new string('a', 64), "partial"),
            ],
        };
        var view = new LogCaptureAnalysisViewModel(new FakeCaptureController(snapshot),
            _ => Task.FromResult<string?>(@"C:\capture"));

        await view.LoadCaptureCommand.ExecuteAsync(null);

        CaptureModComparisonViewModel meadow = Assert.Single(view.ModComparison);
        Assert.False(meadow.HasMismatch);
        Assert.True(meadow.HasVerificationGap);
        Assert.Equal("Code fingerprint unavailable or incomplete", meadow.StatusText);
        Assert.True(view.HasModVerificationGaps);
        Assert.Contains("cannot be verified", view.ModVerificationSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Bundled_packages_do_not_create_fingerprint_warnings_or_mismatches()
    {
        CaptureModEntry[] aliceMods = EnabledModsFile.BuiltIn.Select(id =>
            new CaptureModEntry(id, id, "1.0", new string('a', 64), "complete")).Append(
            new("rainmeadow", "Rain Meadow", "0.4.2", new string('a', 64), "complete")).ToArray();
        CaptureModEntry[] bobMods = EnabledModsFile.BuiltIn.Select(id =>
            new CaptureModEntry(id, id, "1.0", "", "no-code")).Append(
            new("rainmeadow", "Rain Meadow", "0.4.2", new string('b', 64), "complete")).ToArray();
        CaptureAnalysisSnapshot snapshot = Snapshot() with
        {
            ModSnapshots =
            [
                new(Start, "alice", "Alice", "alice-session", "1.4.0-beta.3", "v1.11.8", "1.0.12", aliceMods, false),
                new(Start, "bob", "Bob", "bob-session", "1.4.0-beta.3", "v1.11.8", "1.0.12", bobMods, false),
            ],
        };
        var view = new LogCaptureAnalysisViewModel(new FakeCaptureController(snapshot),
            _ => Task.FromResult<string?>(@"C:\capture"));

        await view.LoadCaptureCommand.ExecuteAsync(null);

        foreach (string id in EnabledModsFile.BuiltIn)
        {
            CaptureModComparisonViewModel package = Assert.Single(view.ModComparison, item => item.Id == id);
            Assert.False(package.HasMismatch);
            Assert.False(package.HasVerificationGap);
            Assert.Equal("Bundled with Rain World", package.StatusText);
            Assert.DoesNotContain("hash", package.Builds, StringComparison.OrdinalIgnoreCase);
        }
        CaptureModComparisonViewModel meadow = Assert.Single(view.ModComparison, item => item.Id == "rainmeadow");
        Assert.True(meadow.HasMismatch);
        Assert.False(view.HasModVerificationGaps);
    }

    [Fact]
    public async Task Truncated_inventory_does_not_claim_a_listed_mod_is_missing()
    {
        CaptureAnalysisSnapshot snapshot = Snapshot() with
        {
            ModSnapshots =
            [
                Mods("alice", "Alice", new string('a', 64), "complete"),
                new(Start, "bob", "Bob", "bob-session", "1.4.0-beta.3", "v1.11.8", "1.0.12",
                    [new("rainmeadow", "Rain Meadow", "0.4.2", new string('a', 64), "complete")], true),
            ],
        };
        var view = new LogCaptureAnalysisViewModel(new FakeCaptureController(snapshot),
            _ => Task.FromResult<string?>(@"C:\capture"));

        await view.LoadCaptureCommand.ExecuteAsync(null);

        CaptureModComparisonViewModel meadow = Assert.Single(view.ModComparison, item => item.Id == "rainmeadow");
        Assert.False(meadow.HasMismatch);
        Assert.False(meadow.HasVerificationGap);
        Assert.Equal("Matching version and code fingerprint", meadow.StatusText);
        CaptureModComparisonViewModel inventory = Assert.Single(view.ModComparison, item => item.Id == "inventory:bob");
        Assert.True(inventory.HasVerificationGap);
        Assert.Equal("Mod inventory was truncated", inventory.StatusText);
    }

    [Fact]
    public async Task Timeline_filters_scaling_and_focus_apply_to_every_track()
    {
        var view = new LogCaptureAnalysisViewModel(
            new FakeCaptureController(Snapshot()),
            _ => Task.FromResult<string?>(@"C:\capture"));
        await view.LoadCaptureCommand.ExecuteAsync(null);

        Assert.Equal(2, view.TimelineTracks.Count);
        Assert.All(view.TimelineTracks, track => Assert.Contains(track.Moments,
            moment => moment.Severity >= CaptureEventSeverity.Error));
        Assert.Single(view.TimelineIncidents);
        view.ShowErrors = false;
        Assert.All(view.TimelineTracks, track => Assert.DoesNotContain(track.Moments,
            moment => moment.Severity >= CaptureEventSeverity.Error));
        Assert.Empty(view.TimelineIncidents);

        view.ShowErrors = true;
        Assert.Single(view.TimelineIncidents);
        view.SelectedIncident = Assert.Single(view.Incidents);
        view.FocusSelectionCommand.Execute(null);
        Assert.True(view.HorizontalZoom > 1);
        Assert.InRange(view.CursorTime, view.VisibleStart, view.VisibleEnd);

        view.LaneHeight = 4;
        Assert.Equal(26, view.LaneHeight);
        view.LaneHeight = 200;
        Assert.Equal(90, view.LaneHeight);
    }

    [Fact]
    public async Task Timeline_incident_overlays_follow_error_and_warning_filters()
    {
        CaptureAnalysisSnapshot original = Snapshot();
        CaptureIncident warning = original.Incidents[0] with
        {
            Id = "incident-warning",
            Severity = CaptureEventSeverity.Warning,
        };
        var view = new LogCaptureAnalysisViewModel(
            new FakeCaptureController(original with { Incidents = [original.Incidents[0], warning] }),
            _ => Task.FromResult<string?>(@"C:\capture"));
        await view.LoadCaptureCommand.ExecuteAsync(null);

        Assert.Equal(2, view.TimelineIncidents.Count);
        view.ShowWarnings = false;
        Assert.Single(view.TimelineIncidents);
        Assert.All(view.TimelineIncidents,
            incident => Assert.True(incident.Incident.Severity >= CaptureEventSeverity.Error));
        view.ShowErrors = false;
        Assert.Empty(view.TimelineIncidents);
        Assert.Equal(2, view.Incidents.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Growing_capture_does_not_move_a_paused_live_viewport(bool zoomed)
    {
        CaptureAnalysisSnapshot initial = Snapshot() with { IsComplete = false };
        var controller = new FakeCaptureController(initial);
        var view = new LogCaptureAnalysisViewModel(controller,
            _ => Task.FromResult<string?>(@"C:\capture"));
        await view.LoadCaptureCommand.ExecuteAsync(null);

        if (zoomed)
        {
            view.SetPlayhead(Start.AddSeconds(30));
            view.HorizontalZoom = 4;
            view.PanTimeline(0.25);
        }
        else view.FollowLiveEdge = false;
        Assert.False(view.IsPlaying);
        DateTimeOffset start = view.VisibleStart;
        DateTimeOffset end = view.VisibleEnd;
        DateTimeOffset playhead = view.CursorTime;

        controller.Update(initial with
        {
            EndedUtc = Start.AddMinutes(4),
            Moments = initial.Moments
                .Append(Moment(3, "alice", "Alice", Start.AddSeconds(35)))
                .Append(Moment(4, "alice", "Alice", Start.AddMinutes(3))).ToArray(),
        });
        await view.RefreshCaptureCommand.ExecuteAsync(null);

        Assert.False(view.FollowLiveEdge);
        Assert.Equal(Start.AddMinutes(4), view.TimelineEnd);
        Assert.Equal("4", view.ErrorCountText);
        Assert.Equal(start, view.VisibleStart);
        Assert.Equal(end, view.VisibleEnd);
        Assert.Equal(playhead, view.CursorTime);
        Assert.Contains(view.TimelineTracks.SelectMany(track => track.Moments), moment => moment.Sequence == 3);
        Assert.DoesNotContain(view.TimelineTracks.SelectMany(track => track.Moments), moment => moment.Sequence == 4);
    }

    [Fact]
    public async Task Growing_capture_advances_the_playhead_and_viewport_when_live_edge_is_followed()
    {
        CaptureAnalysisSnapshot initial = Snapshot() with { IsComplete = false };
        var controller = new FakeCaptureController(initial);
        var view = new LogCaptureAnalysisViewModel(controller,
            _ => Task.FromResult<string?>(@"C:\capture"));
        await view.LoadCaptureCommand.ExecuteAsync(null);
        view.HorizontalZoom = 4;
        view.FollowLiveEdge = true;

        controller.Update(initial with { EndedUtc = Start.AddMinutes(4) });
        await view.RefreshCaptureCommand.ExecuteAsync(null);

        Assert.True(view.FollowLiveEdge);
        Assert.Equal(Start.AddMinutes(4), view.CursorTime);
        Assert.Equal(Start.AddMinutes(4), view.VisibleEnd);
    }

    [Fact]
    public async Task Selecting_one_players_event_does_not_jump_to_the_first_event_in_its_incident()
    {
        CaptureAnalysisSnapshot snapshot = Snapshot();
        var view = new LogCaptureAnalysisViewModel(
            new FakeCaptureController(snapshot),
            _ => Task.FromResult<string?>(@"C:\capture"));
        await view.LoadCaptureCommand.ExecuteAsync(null);
        CaptureTimelineMoment bobEvent = Assert.Single(snapshot.Moments, moment => moment.SenderId == "bob");

        view.SelectedMoment = bobEvent;

        Assert.Equal(bobEvent.Sequence, view.SelectedMoment?.Sequence);
        Assert.Equal(bobEvent.Timestamp, view.CursorTime);
        Assert.Equal("Bob", view.SelectedMoment?.SenderName);
        Assert.NotNull(view.SelectedIncident);
    }

    [Fact]
    public async Task Map_playback_keeps_multiple_local_players_from_one_log_lane()
    {
        CaptureAnalysisSnapshot snapshot = Snapshot() with
        {
            PlayerObservations =
            [
                Observation("jolly-one", "Player 1", "SU_A01"),
                Observation("jolly-two", "Player 2", "SU_A07"),
            ],
        };
        var view = new LogCaptureAnalysisViewModel(
            new FakeCaptureController(snapshot),
            _ => Task.FromResult<string?>(@"C:\capture"));

        await view.LoadCaptureCommand.ExecuteAsync(null);

        Assert.Equal(2, view.PlayersAtCursor.Count);
        Assert.Contains(view.PlayersAtCursor, player => player.Name == "Player 1" && player.RoomId == "SU_A01");
        Assert.Contains(view.PlayersAtCursor, player => player.Name == "Player 2" && player.RoomId == "SU_A07");
        Assert.All(view.PlayersAtCursor, player => Assert.True(player.HasLogs));
    }

    [Fact]
    public void Capture_analysis_view_has_a_finite_editor_timeline_and_investigation_panels()
    {
        Exception? failure = WpfTestHost.Run(() =>
        {
            var resources = new ResourceDictionary();
            resources.MergedDictionaries.Add(LoadResource("Themes/Palette.Dark.xaml"));
            resources.MergedDictionaries.Add(LoadResource("Themes/Controls.xaml"));
            resources.MergedDictionaries.Add(LoadResource("Theme.xaml"));
            Application.Current!.Resources = resources;

            var controller = new FakeCaptureController(Snapshot());
            var model = new LogCaptureAnalysisViewModel(controller, _ => Task.FromResult<string?>(@"C:\capture"));
            model.LoadCaptureCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            var view = new LogCaptureAnalysisView { DataContext = model };
            var window = new Window
            {
                Content = view,
                Width = 1200,
                Height = 900,
                Left = -10000,
                Top = -10000,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.ToolWindow,
            };
            try
            {
                window.Show();
                window.UpdateLayout();
                CaptureTimelineCanvas timeline = Assert.Single(Descendants<CaptureTimelineCanvas>(view));
                Assert.InRange(timeline.ActualHeight, 300, 600);
                Assert.Single(timeline.Incidents);
                model.ShowErrors = false;
                window.UpdateLayout();
                Assert.Empty(timeline.Incidents);
                Assert.Single(Descendants<CaptureReplayMapCanvas>(view));
                Assert.Contains(Descendants<TextBlock>(view), item => item.Text == "Possible mod causes");
                Assert.Contains(Descendants<TextBlock>(view), item => item.Text == "Mod build comparison");
                Assert.Contains(Descendants<TextBlock>(view), item =>
                    item.Text == "Same stated version, different code fingerprints");
                Assert.Contains(Descendants<Slider>(view), item =>
                    AutomationProperties.GetName(item) == "Timeline horizontal zoom");
                Assert.Contains(Descendants<Slider>(view), item =>
                    AutomationProperties.GetName(item) == "Timeline lane height");
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Null(failure);
    }

    [Fact]
    public async Task Live_refresh_keeps_the_visible_canvas_still_after_its_follow_checkbox_is_cleared()
    {
        Exception? failure = await WpfTestHost.RunAsync(async () =>
        {
            var resources = new ResourceDictionary();
            resources.MergedDictionaries.Add(LoadResource("Themes/Palette.Dark.xaml"));
            resources.MergedDictionaries.Add(LoadResource("Themes/Controls.xaml"));
            resources.MergedDictionaries.Add(LoadResource("Theme.xaml"));
            Application.Current!.Resources = resources;

            CaptureAnalysisSnapshot initial = Snapshot() with { IsComplete = false };
            var controller = new FakeCaptureController(initial);
            var model = new LogCaptureAnalysisViewModel(controller,
                _ => Task.FromResult<string?>(@"C:\capture"));
            var view = new LogCaptureAnalysisView { DataContext = model };
            var window = new Window
            {
                Content = view,
                Width = 1200,
                Height = 900,
                Left = -10000,
                Top = -10000,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.ToolWindow,
            };
            try
            {
                window.Show();
                await model.LoadCaptureCommand.ExecuteAsync(null);
                window.UpdateLayout();
                CaptureTimelineCanvas timeline = Assert.Single(Descendants<CaptureTimelineCanvas>(view));
                CheckBox follow = Assert.Single(Descendants<CheckBox>(view), item =>
                    string.Equals(item.Content as string, "Follow live edge", StringComparison.Ordinal));
                follow.IsChecked = false;
                Assert.False(model.FollowLiveEdge);
                DateTimeOffset start = timeline.VisibleStart;
                DateTimeOffset end = timeline.VisibleEnd;
                DateTimeOffset playhead = timeline.CursorTime;

                controller.Update(initial with
                {
                    EndedUtc = Start.AddMinutes(4),
                    Moments = initial.Moments.Append(Moment(3, "alice", "Alice", Start.AddSeconds(35))).ToArray(),
                });
                await model.RefreshCaptureCommand.ExecuteAsync(null);
                window.UpdateLayout();

                Assert.Equal(start, timeline.VisibleStart);
                Assert.Equal(end, timeline.VisibleEnd);
                Assert.Equal(playhead, timeline.CursorTime);
                Assert.Contains(timeline.Tracks.SelectMany(track => track.Moments), moment => moment.Sequence == 3);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Null(failure);
    }

    private static CaptureAnalysisSnapshot Snapshot()
    {
        CaptureTimelineMoment aliceError = Moment(1, "alice", "Alice", Start.AddSeconds(20));
        CaptureTimelineMoment bobError = Moment(2, "bob", "Bob", Start.AddSeconds(20.4));
        var cause = new CaptureCauseCandidate("rainmeadow", "Rain Meadow", 0.85, "high confidence",
            ["The stack names this mod.", "Affected players reported different code fingerprints."]);
        var incident = new CaptureIncident("incident-1", aliceError.Timestamp, bobError.Timestamp,
            CaptureEventSeverity.Error, "Lobby failure", ["alice", "bob"], ["Alice", "Bob"], [1, 2], true,
            "SU_A07", "SU", [cause]);
        return new()
        {
            CaptureFolder = @"C:\capture",
            CaptureId = "capture-1",
            StartedUtc = Start,
            EndedUtc = Start.AddMinutes(2),
            IsComplete = true,
            StatusText = "Capture loaded.",
            Participants =
            [
                new("alice", "Alice", true, true, true, "Shared logs and direct diagnostics."),
                new("bob", "Bob", false, true, true, "Shared logs and direct diagnostics."),
            ],
            Moments = [aliceError, bobError],
            Incidents = [incident],
            ModSnapshots =
            [
                Mods("alice", "Alice", new string('a', 64)),
                Mods("bob", "Bob", new string('b', 64)),
            ],
        };
    }

    private static CaptureTimelineMoment Moment(long sequence, string id, string name, DateTimeOffset time) => new(
        sequence, time, time, time, CaptureTimingConfidence.Exact, CaptureEventSeverity.Error,
        CaptureEventCategory.Log, "log-error", id, name, id + "-session", "Lobby failure",
        "RainMeadow.WorldSession threw while entering SU_A07", "BepInEx/LogOutput.log", 1, 10,
        RoomId: "SU_A07", Region: "SU");

    private static CaptureModSnapshot Mods(string id, string name, string fingerprint, string status = "complete") => new(
        Start, id, name, id + "-session", "1.4.0-beta.3", "v1.11.8", "1.0.12",
        [new("rainmeadow", "Rain Meadow", "0.4.2", fingerprint, status)], false);

    private static CapturePlayerObservation Observation(string id, string name, string room) => new(
        Start.AddSeconds(10), "alice", "Alice", "alice-session", id, name, room, "SU", false,
        true, id == "jolly-one", CaptureObservationAuthority.Direct, "Alice");

    private static ResourceDictionary LoadResource(string path) => new()
    {
        Source = new Uri("pack://application:,,,/RainWorldCompanion;component/" + path, UriKind.Absolute),
    };

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T typed) yield return typed;
            foreach (T nested in Descendants<T>(child)) yield return nested;
        }
    }

    private sealed class FakeCaptureController(CaptureAnalysisSnapshot snapshot) : ILogCaptureAnalysisController
    {
        public string CaptureFolder { get; private set; } = snapshot.CaptureFolder;
        public CaptureAnalysisSnapshot Snapshot { get; private set; } = snapshot;

        public Task<CaptureAnalysisSnapshot> OpenAsync(string folder, CancellationToken cancellationToken = default)
        {
            CaptureFolder = folder;
            return Task.FromResult(Snapshot);
        }

        public Task<CaptureAnalysisSnapshot> RefreshAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);

        public void Update(CaptureAnalysisSnapshot snapshot) => Snapshot = snapshot;
    }
}
