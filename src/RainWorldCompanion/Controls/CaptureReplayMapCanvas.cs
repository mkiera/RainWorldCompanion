using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.Controls;

public sealed class CaptureReplayMapCanvas : FrameworkElement
{
    private static readonly Brush[] PlayerBrushes =
    [
        Brushes.Cyan, Brushes.Gold, Brushes.HotPink, Brushes.LimeGreen,
        Brushes.Coral, Brushes.Violet, Brushes.DeepSkyBlue, Brushes.SpringGreen,
    ];

    private readonly List<(CaptureMapPlayerViewModel Player, Point Point)> _markers = [];
    private BitmapSource? _image;
    private DenMapDefinition? _loadedMap;
    private Point? _press;
    private Point _last;
    private bool _dragged;
    private INotifyCollectionChanged? _playerCollection;

    public static readonly DependencyProperty MapProperty = DependencyProperty.Register(
        nameof(Map), typeof(DenMapDefinition), typeof(CaptureReplayMapCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnMapChanged));

    public static readonly DependencyProperty PlayersProperty = DependencyProperty.Register(
        nameof(Players), typeof(IReadOnlyList<CaptureMapPlayerViewModel>), typeof(CaptureReplayMapCanvas),
        new FrameworkPropertyMetadata(Array.Empty<CaptureMapPlayerViewModel>(),
            FrameworkPropertyMetadataOptions.AffectsRender, OnPlayersChanged));

    public static readonly DependencyProperty CursorTimeProperty = DependencyProperty.Register(
        nameof(CursorTime), typeof(DateTimeOffset), typeof(CaptureReplayMapCanvas),
        new FrameworkPropertyMetadata(default(DateTimeOffset), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectedPlayerIdProperty = DependencyProperty.Register(
        nameof(SelectedPlayerId), typeof(string), typeof(CaptureReplayMapCanvas),
        new FrameworkPropertyMetadata(null,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public CaptureReplayMapCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
        Cursor = Cursors.Hand;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        AutomationProperties.SetName(this, "Capture replay world map");
        AutomationProperties.SetHelpText(this,
            "Shows player rooms at the timeline playhead. Drag to pan, use the mouse wheel to zoom, and press F to fit the map.");
    }

    public DenMapDefinition? Map
    {
        get => (DenMapDefinition?)GetValue(MapProperty);
        set => SetValue(MapProperty, value);
    }

    public IReadOnlyList<CaptureMapPlayerViewModel> Players
    {
        get => (IReadOnlyList<CaptureMapPlayerViewModel>)GetValue(PlayersProperty);
        set => SetValue(PlayersProperty, value ?? Array.Empty<CaptureMapPlayerViewModel>());
    }

    public DateTimeOffset CursorTime
    {
        get => (DateTimeOffset)GetValue(CursorTimeProperty);
        set => SetValue(CursorTimeProperty, value);
    }

    public string? SelectedPlayerId
    {
        get => (string?)GetValue(SelectedPlayerIdProperty);
        set => SetValue(SelectedPlayerIdProperty, value);
    }

    public DenMapViewport Viewport { get; } = new();
    public event Action<string>? PlayerSelected;
    public event Action? ManuallyPanned;

    public void Fit()
    {
        Viewport.Fit();
        InvalidateVisual();
    }

    public void Zoom(double factor)
    {
        if (!double.IsFinite(factor) || factor <= 0) return;
        Viewport.Zoom(factor, new Point(ActualWidth / 2, ActualHeight / 2));
        InvalidateVisual();
    }

    public void CenterPlayer(string playerId, bool preserveZoom = true)
    {
        CaptureMapPlayerViewModel? player = Players.FirstOrDefault(item => item.Id == playerId && item.Placement is not null);
        if (player?.Placement is null) return;
        if (!preserveZoom) Viewport.Zoom(Math.Max(1, Viewport.FitScale) / Math.Max(0.0001, Viewport.Scale),
            new Point(ActualWidth / 2, ActualHeight / 2));
        Viewport.Center(new Point(player.Placement.X, player.Placement.Y));
        InvalidateVisual();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Viewport.Resize(sizeInfo.NewSize);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        _markers.Clear();
        dc.DrawRectangle(Brushes.Black, null, new Rect(RenderSize));
        if (_image is null || _loadedMap is null)
        {
            DrawMessage(dc, "No bundled map is available at the selected time.", new Point(14, 14));
            DrawUnplacedPlayers(dc, 48);
            return;
        }

        dc.DrawImage(_image, new Rect(Viewport.OffsetX, Viewport.OffsetY,
            _loadedMap.ImageWidth * Viewport.Scale, _loadedMap.ImageHeight * Viewport.Scale));

        foreach (MappedRoom room in Players.Where(player => player.Placement is not null)
                     .Select(player => player.Placement!).DistinctBy(room => room.RoomId))
            DrawRoom(dc, room);

        foreach (IGrouping<string, CaptureMapPlayerViewModel> group in Players
                     .Where(player => player.Placement is not null).GroupBy(player => player.Placement!.RoomId))
        {
            CaptureMapPlayerViewModel[] players = group.OrderBy(player => player.Id, StringComparer.Ordinal).ToArray();
            for (int index = 0; index < players.Length; index++)
            {
                CaptureMapPlayerViewModel player = players[index];
                MappedRoom room = player.Placement!;
                Point anchor = Viewport.ToScreen(new Point(room.X, room.Y));
                Point marker = anchor + new Vector(0, (index - (players.Length - 1) / 2.0) * 28);
                if (marker.X < -120 || marker.Y < -35 || marker.X > ActualWidth + 120 || marker.Y > ActualHeight + 35) continue;
                _markers.Add((player, marker));
                DrawPlayer(dc, player, anchor, marker);
            }
        }

        string time = CursorTime == default ? "Selected capture time" : CursorTime.ToLocalTime().ToString("MMM d  HH:mm:ss.fff", CultureInfo.CurrentCulture);
        DrawMessage(dc, $"{_loadedMap.Id}  {time}", new Point(12, 10));
        DrawUnplacedPlayers(dc, 42);
    }

    private static void OnMapChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var canvas = (CaptureReplayMapCanvas)owner;
        var map = (DenMapDefinition?)args.NewValue;
        canvas._loadedMap = map;
        canvas._image = map is null ? null : DenMapCanvas.LoadImage(map);
        if (map is not null) canvas.Viewport.SetMap(map);
        canvas.ToolTip = null;
        canvas.InvalidateVisual();
    }

    private static void OnPlayersChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var canvas = (CaptureReplayMapCanvas)owner;
        if (canvas._playerCollection is not null)
            canvas._playerCollection.CollectionChanged -= canvas.OnPlayerCollectionChanged;
        canvas._playerCollection = args.NewValue as INotifyCollectionChanged;
        if (canvas._playerCollection is not null)
            canvas._playerCollection.CollectionChanged += canvas.OnPlayerCollectionChanged;
    }

    private void OnPlayerCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) => InvalidateVisual();

    private void DrawRoom(DrawingContext dc, MappedRoom room)
    {
        Brush highlight = TryFindResource("Brush.Accent") as Brush ?? Brushes.Cyan;
        foreach (RoomMapRect rectangle in room.Bounds)
        {
            Point top = Viewport.ToScreen(new Point(rectangle.X, rectangle.Y));
            var bounds = new Rect(top.X, top.Y, rectangle.Width * Viewport.Scale, rectangle.Height * Viewport.Scale);
            dc.PushOpacity(0.18);
            dc.DrawRectangle(highlight, null, bounds);
            dc.Pop();
            dc.DrawRectangle(null, new Pen(highlight, 1.25), bounds);
        }
        if (room.Bounds.Count == 0)
            dc.DrawEllipse(null, new Pen(highlight, 1.5), Viewport.ToScreen(new Point(room.X, room.Y)), 12, 12);
    }

    private void DrawPlayer(DrawingContext dc, CaptureMapPlayerViewModel player, Point anchor, Point marker)
    {
        Brush playerBrush = PlayerBrushes[PositiveHash(player.Id) % PlayerBrushes.Length];
        if (player.State.Equals("Dead", StringComparison.OrdinalIgnoreCase)) playerBrush = Brushes.Gray;
        dc.DrawLine(new Pen(Brushes.White, 1), anchor, marker);
        bool selected = string.Equals(SelectedPlayerId, player.Id, StringComparison.Ordinal);
        double radius = 8;
        if (player.HasLogs)
            dc.DrawEllipse(playerBrush, new Pen(Brushes.Black, 2), marker, radius, radius);
        else
            dc.DrawEllipse(Brushes.Black, new Pen(playerBrush, 2.5), marker, radius, radius);
        if (player.IsHost) DrawText(dc, "H", marker + new Vector(-3.5, -7), 10, Brushes.Black, 9, FontWeights.Bold);
        if (player.HasNearbyError)
        {
            Brush danger = TryFindResource("Brush.Danger") as Brush ?? Brushes.Red;
            dc.DrawGeometry(Brushes.Black, new Pen(danger, 2.5), Diamond(marker, radius + 5));
            DrawText(dc, "!", marker + new Vector(-2.4, -8), 12, danger, 8, FontWeights.Bold);
        }
        if (selected) dc.DrawEllipse(null, new Pen(Brushes.White, 2), marker, radius + 6, radius + 6);

        string suffix = player.State.Equals("Dead", StringComparison.OrdinalIgnoreCase) ? "  DEAD" : "";
        string label = player.Name + suffix;
        var formatted = Format(label, 11.5, Brushes.White, 170, FontWeights.SemiBold);
        var labelBounds = new Rect(marker.X + 12, marker.Y - 10, formatted.Width + 9, 22);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(225, 15, 17, 19)),
            player.HasNearbyError ? new Pen(TryFindResource("Brush.Danger") as Brush ?? Brushes.Red, 1) : null,
            labelBounds, 3, 3);
        dc.DrawText(formatted, new Point(marker.X + 16, marker.Y - 8));
    }

    private void DrawUnplacedPlayers(DrawingContext dc, double top)
    {
        CaptureMapPlayerViewModel[] unplaced = Players.Where(player => player.Placement is null).ToArray();
        if (unplaced.Length == 0) return;
        string text = string.Join("\n", unplaced.Take(8).Select(player =>
            $"{player.Name}: {player.LocationText} ({player.CoverageText})"));
        if (unplaced.Length > 8) text += $"\n+{unplaced.Length - 8} more without map placement";
        DrawMessage(dc, text, new Point(12, top));
    }

    private void DrawMessage(DrawingContext dc, string value, Point point)
    {
        var text = Format(value, 11, Brushes.White, Math.Max(100, ActualWidth - 40));
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(220, 15, 17, 19)), null,
            new Rect(point.X - 4, point.Y - 3, text.Width + 9, text.Height + 7), 3, 3);
        dc.DrawText(text, point);
    }

    private void DrawText(DrawingContext dc, string value, Point point, double size, Brush brush,
        double maximumWidth, FontWeight? weight = null) => dc.DrawText(Format(value, size, brush, maximumWidth, weight), point);

    private FormattedText Format(string value, double size, Brush brush, double maximumWidth, FontWeight? weight = null) => new(
        value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
        new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal),
        size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
    {
        MaxTextWidth = Math.Max(1, maximumWidth),
        Trimming = TextTrimming.CharacterEllipsis,
    };

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        Viewport.Zoom(Math.Pow(1.2, e.Delta / 120.0), e.GetPosition(this));
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        _press = _last = e.GetPosition(this);
        _dragged = false;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        Point point = e.GetPosition(this);
        if (_press is { } start && e.LeftButton == MouseButtonState.Pressed)
        {
            if (!_dragged && (point - start).Length >= 4)
            {
                _dragged = true;
                ManuallyPanned?.Invoke();
            }
            if (_dragged)
            {
                Viewport.Pan(point - _last);
                InvalidateVisual();
            }
            _last = point;
            return;
        }

        CaptureMapPlayerViewModel? player = HitPlayer(point);
        ToolTip = player is null ? null
            : $"{player.Name}: {player.LocationText}, {player.State}\n{player.CoverageText}. Observed by {player.Observer}.";
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        bool clicked = _press is not null && !_dragged;
        _press = null;
        ReleaseMouseCapture();
        if (clicked && HitPlayer(e.GetPosition(this)) is { } player)
        {
            SetCurrentValue(SelectedPlayerIdProperty, player.Id);
            PlayerSelected?.Invoke(player.Id);
        }
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        _press = null;
        base.OnLostMouseCapture(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        Vector? movement = e.Key switch
        {
            Key.Left => new Vector(60, 0),
            Key.Right => new Vector(-60, 0),
            Key.Up => new Vector(0, 60),
            Key.Down => new Vector(0, -60),
            _ => null,
        };
        if (movement is { } delta)
        {
            ManuallyPanned?.Invoke();
            Viewport.Pan(delta);
            InvalidateVisual();
            e.Handled = true;
        }
        else if (e.Key is Key.Add or Key.OemPlus)
        {
            Zoom(1.2);
            e.Handled = true;
        }
        else if (e.Key is Key.Subtract or Key.OemMinus)
        {
            Zoom(1 / 1.2);
            e.Handled = true;
        }
        else if (e.Key == Key.F)
        {
            Fit();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    private CaptureMapPlayerViewModel? HitPlayer(Point point) => _markers
        .Where(marker => (marker.Point - point).Length <= 13)
        .OrderBy(marker => (marker.Point - point).Length)
        .Select(marker => marker.Player).FirstOrDefault();

    private static int PositiveHash(string value)
    {
        int hash = 17;
        foreach (char character in value) hash = unchecked(hash * 31 + character);
        return hash & 0x7fffffff;
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
}
