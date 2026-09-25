using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using RainWorldCompanion.Core.LogStreaming.Analysis;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.Controls;

public sealed class CaptureTimelineCanvas : FrameworkElement
{
    private const double RulerHeight = 30;
    private const double MinimumPlotWidth = 80;
    private readonly List<(Rect Bounds, CaptureTimelineMoment Moment)> _momentHits = [];
    private readonly List<(Rect Bounds, CaptureIncidentViewModel Incident)> _incidentHits = [];
    private Point? _panPoint;
    private bool _scrubbing;
    private INotifyCollectionChanged? _incidentCollection;

    public static readonly DependencyProperty TracksProperty = DependencyProperty.Register(
        nameof(Tracks), typeof(IReadOnlyList<CaptureTimelineTrackViewModel>), typeof(CaptureTimelineCanvas),
        new FrameworkPropertyMetadata(Array.Empty<CaptureTimelineTrackViewModel>(),
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IncidentsProperty = DependencyProperty.Register(
        nameof(Incidents), typeof(IReadOnlyList<CaptureIncidentViewModel>), typeof(CaptureTimelineCanvas),
        new FrameworkPropertyMetadata(Array.Empty<CaptureIncidentViewModel>(),
            FrameworkPropertyMetadataOptions.AffectsRender, OnIncidentsChanged));

    public static readonly DependencyProperty VisibleStartProperty = DependencyProperty.Register(
        nameof(VisibleStart), typeof(DateTimeOffset), typeof(CaptureTimelineCanvas),
        new FrameworkPropertyMetadata(default(DateTimeOffset), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty VisibleEndProperty = DependencyProperty.Register(
        nameof(VisibleEnd), typeof(DateTimeOffset), typeof(CaptureTimelineCanvas),
        new FrameworkPropertyMetadata(default(DateTimeOffset), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CursorTimeProperty = DependencyProperty.Register(
        nameof(CursorTime), typeof(DateTimeOffset), typeof(CaptureTimelineCanvas),
        new FrameworkPropertyMetadata(default(DateTimeOffset),
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty LaneHeightProperty = DependencyProperty.Register(
        nameof(LaneHeight), typeof(double), typeof(CaptureTimelineCanvas),
        new FrameworkPropertyMetadata(46d,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            null, (_, value) => Math.Clamp(double.IsFinite((double)value) ? (double)value : 46, 26, 90)));

    public static readonly DependencyProperty LabelWidthProperty = DependencyProperty.Register(
        nameof(LabelWidth), typeof(double), typeof(CaptureTimelineCanvas),
        new FrameworkPropertyMetadata(190d,
            FrameworkPropertyMetadataOptions.AffectsRender,
            null, (_, value) => Math.Clamp(double.IsFinite((double)value) ? (double)value : 190, 120, 320)));

    public static readonly DependencyProperty SelectedMomentProperty = DependencyProperty.Register(
        nameof(SelectedMoment), typeof(CaptureTimelineMoment), typeof(CaptureTimelineCanvas),
        new FrameworkPropertyMetadata(null,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty SelectedIncidentProperty = DependencyProperty.Register(
        nameof(SelectedIncident), typeof(CaptureIncidentViewModel), typeof(CaptureTimelineCanvas),
        new FrameworkPropertyMetadata(null,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public CaptureTimelineCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
        Cursor = Cursors.Cross;
        AutomationProperties.SetName(this, "Capture event timeline");
        AutomationProperties.SetHelpText(this,
            "Left-click or drag to move the playhead. Drag with the middle or right button to pan. Use the mouse wheel to zoom and Control plus the wheel to resize lanes.");
    }

    public IReadOnlyList<CaptureTimelineTrackViewModel> Tracks
    {
        get => (IReadOnlyList<CaptureTimelineTrackViewModel>)GetValue(TracksProperty);
        set => SetValue(TracksProperty, value ?? Array.Empty<CaptureTimelineTrackViewModel>());
    }

    public IReadOnlyList<CaptureIncidentViewModel> Incidents
    {
        get => (IReadOnlyList<CaptureIncidentViewModel>)GetValue(IncidentsProperty);
        set => SetValue(IncidentsProperty, value ?? Array.Empty<CaptureIncidentViewModel>());
    }

    public DateTimeOffset VisibleStart
    {
        get => (DateTimeOffset)GetValue(VisibleStartProperty);
        set => SetValue(VisibleStartProperty, value);
    }

    public DateTimeOffset VisibleEnd
    {
        get => (DateTimeOffset)GetValue(VisibleEndProperty);
        set => SetValue(VisibleEndProperty, value);
    }

    public DateTimeOffset CursorTime
    {
        get => (DateTimeOffset)GetValue(CursorTimeProperty);
        set => SetValue(CursorTimeProperty, value);
    }

    public double LaneHeight
    {
        get => (double)GetValue(LaneHeightProperty);
        set => SetValue(LaneHeightProperty, value);
    }

    public double LabelWidth
    {
        get => (double)GetValue(LabelWidthProperty);
        set => SetValue(LabelWidthProperty, value);
    }

    public CaptureTimelineMoment? SelectedMoment
    {
        get => (CaptureTimelineMoment?)GetValue(SelectedMomentProperty);
        set => SetValue(SelectedMomentProperty, value);
    }

    public CaptureIncidentViewModel? SelectedIncident
    {
        get => (CaptureIncidentViewModel?)GetValue(SelectedIncidentProperty);
        set => SetValue(SelectedIncidentProperty, value);
    }

    public event Action<DateTimeOffset>? PlayheadChanged;
    public event Action<CaptureTimelineMoment>? MomentSelected;
    public event Action<CaptureIncidentViewModel>? IncidentSelected;
    public event Action<double, double>? HorizontalZoomRequested;
    public event Action<double>? HorizontalPanRequested;
    public event Action<double>? LaneHeightRequested;

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width;
        double height = RulerHeight + Math.Max(1, Tracks.Count) * LaneHeight + 1;
        return new Size(Math.Max(320, width), height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        _momentHits.Clear();
        _incidentHits.Clear();
        Brush background = Resource("Brush.PanelAlt", Color.FromRgb(20, 23, 26));
        Brush panel = Resource("Brush.Panel", Color.FromRgb(30, 34, 38));
        Brush border = Resource("Brush.Border", Color.FromRgb(67, 73, 79));
        Brush text = Resource("Brush.Text", Colors.White);
        Brush muted = Resource("Brush.TextMuted", Color.FromRgb(165, 171, 178));
        dc.DrawRectangle(background, null, new Rect(RenderSize));

        double plotLeft = Math.Min(LabelWidth, Math.Max(0, ActualWidth - MinimumPlotWidth));
        double plotWidth = Math.Max(0, ActualWidth - plotLeft);
        if (!HasTimeRange || plotWidth < 1)
        {
            DrawText(dc, "Load a capture to view its timeline.", new Point(12, 9), 12, muted,
                Math.Max(100, ActualWidth - 24));
            return;
        }

        for (int lane = 0; lane < Tracks.Count; lane++)
        {
            CaptureTimelineTrackViewModel track = Tracks[lane];
            double top = RulerHeight + lane * LaneHeight;
            var laneBounds = new Rect(0, top, ActualWidth, LaneHeight);
            if ((lane & 1) == 0) dc.DrawRectangle(panel, null, laneBounds);
            dc.DrawLine(new Pen(border, 1), new Point(0, top + LaneHeight), new Point(ActualWidth, top + LaneHeight));
            dc.DrawLine(new Pen(border, 1), new Point(plotLeft, top), new Point(plotLeft, top + LaneHeight));

            DrawText(dc, track.Name, new Point(9, top + 5), 12, text, Math.Max(30, plotLeft - 18), FontWeights.SemiBold);
            if (LaneHeight >= 38)
            {
                string detail = track.Role + "  " + track.Coverage;
                DrawText(dc, detail, new Point(9, top + 23), 9.5, muted, Math.Max(30, plotLeft - 18));
            }
        }

        DrawRuler(dc, plotLeft, plotWidth, border, muted);
        DrawIncidents(dc, plotLeft, plotWidth);
        for (int lane = 0; lane < Tracks.Count; lane++)
        {
            CaptureTimelineTrackViewModel track = Tracks[lane];
            double top = RulerHeight + lane * LaneHeight;

            dc.PushClip(new RectangleGeometry(new Rect(plotLeft, top, plotWidth, LaneHeight)));
            foreach (CaptureTimelineMoment moment in track.Moments)
            {
                if (moment.Timestamp < VisibleStart || moment.Timestamp > VisibleEnd) continue;
                double x = TimeX(moment.Timestamp, plotLeft, plotWidth);
                Rect hit = DrawMoment(dc, moment, x, top, LaneHeight);
                _momentHits.Add((hit, moment));
            }
            dc.Pop();
        }

        if (CursorTime >= VisibleStart && CursorTime <= VisibleEnd)
        {
            double x = TimeX(CursorTime, plotLeft, plotWidth);
            Brush playhead = Resource("Brush.Accent", Color.FromRgb(82, 184, 255));
            dc.DrawLine(new Pen(playhead, 2), new Point(x, 0), new Point(x, ActualHeight));
            dc.DrawGeometry(playhead, null, Triangle(new Point(x, RulerHeight - 1), 6, upward: false));
        }

        dc.DrawLine(new Pen(border, 1), new Point(plotLeft, 0), new Point(plotLeft, ActualHeight));
    }

    private void DrawRuler(DrawingContext dc, double plotLeft, double plotWidth, Brush border, Brush muted)
    {
        dc.DrawLine(new Pen(border, 1), new Point(0, RulerHeight - 1), new Point(ActualWidth, RulerHeight - 1));
        DrawText(dc, "PLAYERS", new Point(9, 8), 9.5, muted, Math.Max(30, plotLeft - 18), FontWeights.SemiBold);
        const int divisions = 6;
        TimeSpan span = VisibleEnd - VisibleStart;
        for (int index = 0; index <= divisions; index++)
        {
            double fraction = index / (double)divisions;
            double x = plotLeft + plotWidth * fraction;
            DateTimeOffset time = VisibleStart + TimeSpan.FromTicks((long)(span.Ticks * fraction));
            dc.DrawLine(new Pen(border, index is 0 or divisions ? 1 : 0.6),
                new Point(x, RulerHeight - 7), new Point(x, ActualHeight));
            string label = time.ToLocalTime().ToString(span.TotalMinutes < 2 ? "HH:mm:ss.fff" : "HH:mm:ss", CultureInfo.CurrentCulture);
            double maxWidth = Math.Max(34, plotWidth / divisions - 4);
            DrawText(dc, label, new Point(x + (index == divisions ? -maxWidth : 3), 6), 9.5, muted, maxWidth);
        }
    }

    private void DrawIncidents(DrawingContext dc, double plotLeft, double plotWidth)
    {
        foreach (CaptureIncidentViewModel incident in Incidents)
        {
            if (incident.Incident.Ended < VisibleStart || incident.Incident.Started > VisibleEnd) continue;
            double left = TimeX(Clamp(incident.Incident.Started), plotLeft, plotWidth);
            double right = TimeX(Clamp(incident.Incident.Ended), plotLeft, plotWidth);
            double width = Math.Max(6, right - left);
            Brush brush = SeverityBrush(incident.Severity);
            byte opacity = incident == SelectedIncident ? (byte)78 : (byte)38;
            dc.DrawRectangle(new SolidColorBrush(WithAlpha(ColorOf(brush), opacity)), null,
                new Rect(left, RulerHeight, width, Math.Max(0, ActualHeight - RulerHeight)));
            var marker = new Rect(left - 5, 1, 11, 18);
            dc.DrawRoundedRectangle(Resource("Brush.Window", Colors.Black), new Pen(brush, 2), marker, 2, 2);
            DrawText(dc, SeverityGlyph(incident.Severity), new Point(left - 3, 3), 10, brush, 8, FontWeights.Bold);
            _incidentHits.Add((new Rect(left - 7, 0, Math.Max(14, width + 8), RulerHeight), incident));
        }
    }

    private static void OnIncidentsChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var canvas = (CaptureTimelineCanvas)owner;
        if (canvas._incidentCollection is not null)
            canvas._incidentCollection.CollectionChanged -= canvas.OnIncidentCollectionChanged;
        canvas._incidentCollection = args.NewValue as INotifyCollectionChanged;
        if (canvas._incidentCollection is not null)
            canvas._incidentCollection.CollectionChanged += canvas.OnIncidentCollectionChanged;
    }

    private void OnIncidentCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) => InvalidateVisual();

    private Rect DrawMoment(DrawingContext dc, CaptureTimelineMoment moment, double x, double top, double height)
    {
        Brush brush = moment.Severity >= CaptureEventSeverity.Warning
            ? SeverityBrush(moment.Severity)
            : CategoryBrush(moment.Category);
        bool selected = SelectedMoment?.Sequence == moment.Sequence;
        double centerY = top + height / 2;
        double size = moment.Severity >= CaptureEventSeverity.Warning ? 9 : 7;
        Rect hit = new(x - 7, top + 2, 14, Math.Max(14, height - 4));

        if (moment.Severity >= CaptureEventSeverity.Critical)
        {
            dc.DrawEllipse(Resource("Brush.Window", Colors.Black), new Pen(brush, selected ? 3 : 2),
                new Point(x, centerY), size, size);
            DrawText(dc, "!", new Point(x - 2.5, centerY - 7), 11, brush, 7, FontWeights.Bold);
        }
        else if (moment.Severity >= CaptureEventSeverity.Error)
        {
            Geometry diamond = Diamond(new Point(x, centerY), size);
            dc.DrawGeometry(Resource("Brush.Window", Colors.Black), new Pen(brush, selected ? 3 : 2), diamond);
            dc.DrawLine(new Pen(brush, 1.5), new Point(x - 3, centerY - 3), new Point(x + 3, centerY + 3));
            dc.DrawLine(new Pen(brush, 1.5), new Point(x + 3, centerY - 3), new Point(x - 3, centerY + 3));
        }
        else if (moment.Severity == CaptureEventSeverity.Warning)
        {
            Geometry triangle = Triangle(new Point(x, centerY + size / 2), size, upward: true);
            dc.DrawGeometry(Resource("Brush.Window", Colors.Black), new Pen(brush, selected ? 3 : 2), triangle);
        }
        else
        {
            var clip = new Rect(x - 2.5, top + 7, 5, Math.Max(7, height - 14));
            dc.DrawRoundedRectangle(brush, selected ? new Pen(Resource("Brush.Text", Colors.White), 2) : null,
                clip, 2, 2);
        }
        if (selected) dc.DrawEllipse(null, new Pen(Resource("Brush.Text", Colors.White), 1.5),
            new Point(x, centerY), size + 4, size + 4);
        return hit;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        Point point = e.GetPosition(this);
        CaptureIncidentViewModel? incident = HitIncident(point);
        CaptureTimelineMoment? moment = HitMoment(point);
        if (incident is not null)
        {
            SetCurrentValue(SelectedIncidentProperty, incident);
            IncidentSelected?.Invoke(incident);
            RequestPlayhead(incident.Incident.Started + TimeSpan.FromTicks((incident.Incident.Ended - incident.Incident.Started).Ticks / 2));
        }
        else if (moment is not null)
        {
            SetCurrentValue(SelectedMomentProperty, moment);
            MomentSelected?.Invoke(moment);
            RequestPlayhead(moment.Timestamp);
        }
        else
        {
            _scrubbing = true;
            CaptureMouse();
            Scrub(point);
        }
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        Point point = e.GetPosition(this);
        if (_scrubbing && e.LeftButton == MouseButtonState.Pressed)
        {
            Scrub(point);
            return;
        }
        if (_panPoint is { } previous && (e.MiddleButton == MouseButtonState.Pressed || e.RightButton == MouseButtonState.Pressed))
        {
            double width = PlotWidth;
            if (width > 0) HorizontalPanRequested?.Invoke(-(point.X - previous.X) / width);
            _panPoint = point;
            return;
        }

        CaptureTimelineMoment? moment = HitMoment(point);
        if (moment is not null)
        {
            ToolTip = $"{moment.Timestamp.ToLocalTime():HH:mm:ss.fff}  {moment.SenderName}\n{moment.Severity}: {moment.Summary}";
            return;
        }
        CaptureIncidentViewModel? incident = HitIncident(point);
        ToolTip = incident is null ? null : $"{incident.AlertText}: {incident.Summary}\n{incident.PlayersText}";
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _scrubbing = false;
        if (_panPoint is null) ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton is MouseButton.Middle or MouseButton.Right)
        {
            Focus();
            _panPoint = e.GetPosition(this);
            CaptureMouse();
            e.Handled = true;
            return;
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (e.ChangedButton is MouseButton.Middle or MouseButton.Right)
        {
            _panPoint = null;
            if (!_scrubbing) ReleaseMouseCapture();
            e.Handled = true;
            return;
        }
        base.OnMouseUp(e);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        _scrubbing = false;
        _panPoint = null;
        base.OnLostMouseCapture(e);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            double requested = Math.Clamp(LaneHeight + Math.Sign(e.Delta) * 4, 26, 90);
            SetCurrentValue(LaneHeightProperty, requested);
            LaneHeightRequested?.Invoke(requested);
        }
        else
        {
            double factor = Math.Pow(1.25, e.Delta / 120.0);
            double anchor = Math.Clamp((e.GetPosition(this).X - PlotLeft) / Math.Max(1, PlotWidth), 0, 1);
            HorizontalZoomRequested?.Invoke(factor, anchor);
        }
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        TimeSpan visible = HasTimeRange ? VisibleEnd - VisibleStart : TimeSpan.Zero;
        double step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 0.1 : 0.01;
        switch (e.Key)
        {
            case Key.Left: RequestPlayhead(CursorTime - TimeSpan.FromTicks((long)(visible.Ticks * step))); break;
            case Key.Right: RequestPlayhead(CursorTime + TimeSpan.FromTicks((long)(visible.Ticks * step))); break;
            case Key.Home: RequestPlayhead(VisibleStart); break;
            case Key.End: RequestPlayhead(VisibleEnd); break;
            case Key.PageUp: HorizontalPanRequested?.Invoke(-0.8); break;
            case Key.PageDown: HorizontalPanRequested?.Invoke(0.8); break;
            case Key.Add or Key.OemPlus: HorizontalZoomRequested?.Invoke(1.6, 0.5); break;
            case Key.Subtract or Key.OemMinus: HorizontalZoomRequested?.Invoke(1 / 1.6, 0.5); break;
            case Key.Up when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                RequestLaneHeight(LaneHeight + 4); break;
            case Key.Down when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                RequestLaneHeight(LaneHeight - 4); break;
            default:
                base.OnKeyDown(e);
                return;
        }
        e.Handled = true;
    }

    private void RequestLaneHeight(double value)
    {
        double requested = Math.Clamp(value, 26, 90);
        SetCurrentValue(LaneHeightProperty, requested);
        LaneHeightRequested?.Invoke(requested);
    }

    private void Scrub(Point point)
    {
        if (!HasTimeRange || PlotWidth <= 0) return;
        double fraction = Math.Clamp((point.X - PlotLeft) / PlotWidth, 0, 1);
        TimeSpan span = VisibleEnd - VisibleStart;
        RequestPlayhead(VisibleStart + TimeSpan.FromTicks((long)(span.Ticks * fraction)));
    }

    private void RequestPlayhead(DateTimeOffset time)
    {
        if (!HasTimeRange) return;
        DateTimeOffset requested = time < VisibleStart ? VisibleStart : time > VisibleEnd ? VisibleEnd : time;
        SetCurrentValue(CursorTimeProperty, requested);
        PlayheadChanged?.Invoke(requested);
    }

    private CaptureTimelineMoment? HitMoment(Point point) => _momentHits
        .Where(item => item.Bounds.Contains(point))
        .OrderBy(item => Math.Abs(item.Bounds.X + item.Bounds.Width / 2 - point.X))
        .Select(item => item.Moment).FirstOrDefault();

    private CaptureIncidentViewModel? HitIncident(Point point) => _incidentHits
        .Where(item => item.Bounds.Contains(point))
        .OrderBy(item => Math.Abs(item.Bounds.X + item.Bounds.Width / 2 - point.X))
        .Select(item => item.Incident).FirstOrDefault();

    private bool HasTimeRange => VisibleStart != default && VisibleEnd > VisibleStart;
    private double PlotLeft => Math.Min(LabelWidth, Math.Max(0, ActualWidth - MinimumPlotWidth));
    private double PlotWidth => Math.Max(0, ActualWidth - PlotLeft);
    private DateTimeOffset Clamp(DateTimeOffset time) => time < VisibleStart ? VisibleStart : time > VisibleEnd ? VisibleEnd : time;

    private double TimeX(DateTimeOffset time, double plotLeft, double plotWidth)
    {
        double fraction = (time - VisibleStart).TotalSeconds / Math.Max(0.000001, (VisibleEnd - VisibleStart).TotalSeconds);
        return plotLeft + Math.Clamp(fraction, 0, 1) * plotWidth;
    }

    private Brush SeverityBrush(CaptureEventSeverity severity) => severity switch
    {
        CaptureEventSeverity.Critical => Resource("Brush.Danger", Color.FromRgb(255, 82, 96)),
        CaptureEventSeverity.Error => Resource("Brush.Danger", Color.FromRgb(238, 91, 100)),
        CaptureEventSeverity.Warning => Resource("Brush.Warn", Color.FromRgb(232, 189, 82)),
        _ => Resource("Brush.Accent", Color.FromRgb(82, 184, 255)),
    };

    private Brush CategoryBrush(CaptureEventCategory category) => category switch
    {
        CaptureEventCategory.Location or CaptureEventCategory.Player => new SolidColorBrush(Color.FromRgb(82, 199, 133)),
        CaptureEventCategory.Connection or CaptureEventCategory.Network => new SolidColorBrush(Color.FromRgb(91, 177, 255)),
        CaptureEventCategory.Performance => new SolidColorBrush(Color.FromRgb(214, 130, 255)),
        CaptureEventCategory.Action or CaptureEventCategory.Marker => new SolidColorBrush(Color.FromRgb(255, 190, 76)),
        CaptureEventCategory.Mods => new SolidColorBrush(Color.FromRgb(114, 211, 211)),
        CaptureEventCategory.Log => new SolidColorBrush(Color.FromRgb(198, 205, 211)),
        _ => Resource("Brush.TextMuted", Color.FromRgb(165, 171, 178)),
    };

    private Brush Resource(string key, Color fallback) => TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    private static Color ColorOf(Brush brush) => brush is SolidColorBrush solid ? solid.Color : Colors.White;

    private static Color WithAlpha(Color color, byte alpha)
    {
        color.A = alpha;
        return color;
    }

    private static string SeverityGlyph(CaptureEventSeverity severity) => severity switch
    {
        CaptureEventSeverity.Critical => "!",
        CaptureEventSeverity.Error => "×",
        CaptureEventSeverity.Warning => "△",
        _ => "·",
    };

    private void DrawText(DrawingContext dc, string value, Point point, double size, Brush brush,
        double maximumWidth, FontWeight? weight = null)
    {
        if (maximumWidth <= 0) return;
        var formatted = new FormattedText(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, maximumWidth),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        dc.DrawText(formatted, point);
    }

    private static Geometry Diamond(Point center, double radius)
    {
        var geometry = new StreamGeometry();
        using StreamGeometryContext context = geometry.Open();
        context.BeginFigure(new Point(center.X, center.Y - radius), true, true);
        context.LineTo(new Point(center.X + radius, center.Y), true, false);
        context.LineTo(new Point(center.X, center.Y + radius), true, false);
        context.LineTo(new Point(center.X - radius, center.Y), true, false);
        geometry.Freeze();
        return geometry;
    }

    private static Geometry Triangle(Point point, double radius, bool upward)
    {
        double direction = upward ? -1 : 1;
        var geometry = new StreamGeometry();
        using StreamGeometryContext context = geometry.Open();
        context.BeginFigure(new Point(point.X, point.Y + direction * radius), true, true);
        context.LineTo(new Point(point.X + radius, point.Y - direction * radius), true, false);
        context.LineTo(new Point(point.X - radius, point.Y - direction * radius), true, false);
        geometry.Freeze();
        return geometry;
    }
}
