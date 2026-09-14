using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RainWorldCompanion.Controls;
using RainWorldCompanion.Core.LogStreaming.Analysis;
using RainWorldCompanion.ViewModels;
using RainWorldCompanion.Views;

namespace RainWorldCompanion.App.Tests;

public sealed class LogCaptureAnalysisLoadRegressionTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Dense_capture_loads_after_the_view_is_attached_without_freezing_the_ui()
    {
        CaptureAnalysisSnapshot snapshot = DenseSnapshot();
        TimeSpan loadTime = default;
        TimeSpan renderTime = default;
        int trackCount = 0;
        int visibleEventCount = 0;
        int incidentCount = 0;
        double timelineWidth = 0;
        double timelineHeight = 0;

        Exception? failure = await WpfTestHost.RunAsync(async () =>
        {
            Application.Current!.Resources = LoadThemeResources();
            var model = new LogCaptureAnalysisViewModel(
                new YieldingCaptureController(snapshot),
                _ => Task.FromResult<string?>(@"C:\capture"));
            var view = new LogCaptureAnalysisView { DataContext = model };
            var window = new Window
            {
                Content = view,
                Width = 1920,
                Height = 1000,
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

                var stopwatch = Stopwatch.StartNew();
                await model.LoadCaptureCommand.ExecuteAsync(null);
                window.UpdateLayout();
                loadTime = stopwatch.Elapsed;

                CaptureTimelineCanvas timeline = Assert.Single(Descendants<CaptureTimelineCanvas>(view));
                trackCount = model.TimelineTracks.Count;
                visibleEventCount = model.VisibleEvents.Count;
                incidentCount = model.Incidents.Count;
                timelineWidth = timeline.ActualWidth;
                timelineHeight = timeline.ActualHeight;

                stopwatch.Restart();
                var bitmap = new RenderTargetBitmap(
                    Math.Max(1, (int)Math.Ceiling(window.ActualWidth)),
                    Math.Max(1, (int)Math.Ceiling(window.ActualHeight)),
                    96,
                    96,
                    PixelFormats.Pbgra32);
                bitmap.Render(window);
                renderTime = stopwatch.Elapsed;
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Null(failure);
        Assert.Equal(3, trackCount);
        Assert.Equal(2_000, visibleEventCount);
        Assert.Equal(275, incidentCount);
        Assert.True(double.IsFinite(timelineWidth));
        Assert.True(double.IsFinite(timelineHeight));
        Assert.True(timelineWidth > 640, $"Timeline width was {timelineWidth:N0}px.");
        Assert.InRange(timelineHeight, 300, 600);
        Assert.True(loadTime < TimeSpan.FromSeconds(8), $"Attached-view load took {loadTime}.");
        Assert.True(renderTime < TimeSpan.FromSeconds(4), $"Dense timeline render took {renderTime}.");
    }

    [Fact]
    public async Task Unexpected_controller_failure_is_reported_instead_of_escaping_the_load_command()
    {
        var model = new LogCaptureAnalysisViewModel(
            new ThrowingCaptureController(),
            _ => Task.FromResult<string?>(@"C:\capture"));

        Exception? failure = await Record.ExceptionAsync(() => model.LoadCaptureCommand.ExecuteAsync(null));

        Assert.Null(failure);
        Assert.False(model.IsLoading);
        Assert.False(model.HasAnalysis);
        Assert.Contains("capture could not be loaded", model.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("synthetic controller failure", model.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    private static CaptureAnalysisSnapshot DenseSnapshot()
    {
        const int momentCount = 2_528;
        const int errorCount = 1_282;
        const int warningCount = 515;
        TimeSpan duration = TimeSpan.FromMinutes(33) + TimeSpan.FromSeconds(32);
        var moments = new CaptureTimelineMoment[momentCount];

        for (int index = 0; index < moments.Length; index++)
        {
            DateTimeOffset timestamp = Start + TimeSpan.FromTicks(duration.Ticks * index / (momentCount - 1));
            string senderId = index < 128 ? "" : (index - 128) % 2 == 0 ? "alice" : "bob";
            string senderName = senderId switch { "alice" => "Alice", "bob" => "Bob", _ => "Capture" };
            CaptureEventSeverity severity = index < errorCount
                ? CaptureEventSeverity.Error
                : index < errorCount + warningCount ? CaptureEventSeverity.Warning : CaptureEventSeverity.Info;
            CaptureEventCategory category = severity >= CaptureEventSeverity.Warning
                ? CaptureEventCategory.Log
                : (index % 3) switch
                {
                    0 => CaptureEventCategory.Location,
                    1 => CaptureEventCategory.Connection,
                    _ => CaptureEventCategory.Action,
                };
            moments[index] = new(
                index + 1,
                timestamp,
                timestamp,
                timestamp,
                CaptureTimingConfidence.Exact,
                severity,
                category,
                severity >= CaptureEventSeverity.Warning ? "log-alert" : "game-event",
                senderId,
                senderName,
                senderId.Length == 0 ? "capture-session" : senderId + "-session",
                $"Synthetic event {index + 1:N0}",
                "Synthetic dense capture regression data.",
                senderId.Length == 0 ? "events.jsonl" : "BepInEx/LogOutput.log",
                1,
                index * 128L,
                RoomId: index % 2 == 0 ? "SU_A07" : "SU_A08",
                Region: "SU");
        }

        var incidents = new CaptureIncident[275];
        for (int index = 0; index < incidents.Length; index++)
        {
            CaptureTimelineMoment first = moments[index * 9];
            CaptureTimelineMoment second = moments[index * 9 + 1];
            incidents[index] = new(
                $"incident-{index + 1}",
                first.Timestamp,
                second.Timestamp,
                first.Severity,
                $"Synthetic incident {index + 1}",
                ["alice", "bob"],
                ["Alice", "Bob"],
                [first.Sequence, second.Sequence],
                true,
                first.RoomId,
                first.Region,
                []);
        }

        return new()
        {
            CaptureFolder = @"C:\capture",
            CaptureId = "dense-capture",
            StartedUtc = Start,
            EndedUtc = Start + duration,
            IsComplete = false,
            IsIncomplete = true,
            HasGaps = true,
            StatusText = "Dense capture loaded.",
            TerminationText = "Some streamed data is missing.",
            Warnings = ["The capture contains gaps."],
            Participants =
            [
                new("alice", "Alice", true, true, true, "Shared logs and direct diagnostics."),
                new("bob", "Bob", false, true, true, "Shared logs and direct diagnostics."),
            ],
            Moments = moments,
            Incidents = incidents,
        };
    }

    private static ResourceDictionary LoadThemeResources()
    {
        var resources = new ResourceDictionary();
        resources.MergedDictionaries.Add(LoadResource("Themes/Palette.Dark.xaml"));
        resources.MergedDictionaries.Add(LoadResource("Themes/Controls.xaml"));
        resources.MergedDictionaries.Add(LoadResource("Theme.xaml"));
        return resources;
    }

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

    private sealed class YieldingCaptureController(CaptureAnalysisSnapshot snapshot) : ILogCaptureAnalysisController
    {
        public string CaptureFolder { get; private set; } = snapshot.CaptureFolder;
        public CaptureAnalysisSnapshot Snapshot { get; private set; } = snapshot;

        public async Task<CaptureAnalysisSnapshot> OpenAsync(
            string folder,
            CancellationToken cancellationToken = default)
        {
            CaptureFolder = folder;
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return Snapshot;
        }

        public Task<CaptureAnalysisSnapshot> RefreshAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);
    }

    private sealed class ThrowingCaptureController : ILogCaptureAnalysisController
    {
        public string CaptureFolder => @"C:\capture";
        public CaptureAnalysisSnapshot Snapshot => CaptureAnalysisSnapshot.Empty(CaptureFolder);

        public async Task<CaptureAnalysisSnapshot> OpenAsync(
            string folder,
            CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new InvalidOperationException("Synthetic controller failure.");
        }

        public Task<CaptureAnalysisSnapshot> RefreshAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Synthetic controller failure.");
    }
}
