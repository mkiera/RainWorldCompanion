using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.Controls;

public sealed class LiveMapCanvas : FrameworkElement
{
    private BitmapSource? _image;
    private bool _spoilerMode = true;
    private HashSet<string> _visited = new(StringComparer.OrdinalIgnoreCase);
    private Geometry _revealed = Geometry.Empty;
    public static readonly DependencyProperty SpoilerDetailViewProperty = DependencyProperty.Register(
        nameof(SpoilerDetailView), typeof(bool), typeof(LiveMapCanvas),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, (owner, _) => ((LiveMapCanvas)owner).ToolTip = null));
    public bool SpoilerDetailView
    {
        get => (bool)GetValue(SpoilerDetailViewProperty);
        set => SetValue(SpoilerDetailViewProperty, value);
    }

    public void SetExploration(bool enabled, IEnumerable<string> visited)
    {
        var next = new HashSet<string>(visited, StringComparer.OrdinalIgnoreCase);
        if (_spoilerMode == enabled && _visited.SetEquals(next)) return;
        _spoilerMode = enabled;
        _visited = next;
        RebuildReveal();
        ToolTip = null;
        _markers.Clear();
        InvalidateVisual();
    }

    private bool IsRoomVisible(MappedRoom room) => !_spoilerMode || _visited.Contains(room.RoomId);

    private void RebuildReveal()
    {
        _revealed = _map == null ? Geometry.Empty : CreateReveal(RoomMapCatalog.ForMap(_map.Id), _visited);
    }

    public static Geometry CreateReveal(IEnumerable<MappedRoom> rooms, IReadOnlySet<string> visited)
    {
        var visible = new GeometryGroup { FillRule = FillRule.Nonzero };
        var hidden = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (var room in rooms)
        {
            foreach (var bounds in room.Bounds)
            {
                var rectangle = new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
                if (visited.Contains(room.RoomId)) visible.Children.Add(new RectangleGeometry(rectangle));
                else
                {
                    rectangle.Inflate(1, 1);
                    hidden.Children.Add(new RectangleGeometry(rectangle));
                }
            }
        }
        var reveal = new CombinedGeometry(GeometryCombineMode.Exclude, visible, hidden);
        reveal.Freeze();
        return reveal;
    }

    private DenMapDefinition? _map;
    private Point? _press;
    private Point _last;
    private bool _dragged;
    private readonly List<(LiveMapPlayer Player, Point Point)> _markers = [];
    private static readonly Brush[] PlayerBrushes = [Brushes.Cyan, Brushes.Gold, Brushes.HotPink, Brushes.LimeGreen, Brushes.Coral, Brushes.Violet];
    public DenMapViewport Viewport { get; } = new();
    public IReadOnlyList<LiveMapPlayer> Players { get; set; } = [];
    public MappedRoom? SelectedRoom { get; set; }
    public string? SelectedPlayerId { get; set; }
    public event Action<string>? PlayerSelected;
    public event Action<string>? PlayerFollowed;
    public event Action<MappedRoom?>? RoomSelected;
    public event Action<MappedRoom>? RoomContextRequested;
    public event Action? ManuallyPanned;

    public LiveMapCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
        Cursor = Cursors.Hand;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
    }

    public void Load(DenMapDefinition? map)
    {
        if (_map == map) return;
        _map = map;
        _image = map is null ? null : DenMapCanvas.LoadImage(map);
        if (map is not null) Viewport.SetMap(map);
        SelectedRoom = null;
        RebuildReveal();
        _markers.Clear();
        InvalidateVisual();
    }

    public void Center(MappedRoom room, bool preserveZoom)
    {
        if (!preserveZoom && Viewport.Scale < 0.7) Viewport.Zoom(0.7 / Viewport.Scale, new Point(ActualWidth / 2, ActualHeight / 2));
        Viewport.Center(new Point(room.X, room.Y), clamp: false);
        InvalidateVisual();
    }

    public void Fit() { Viewport.Fit(); InvalidateVisual(); }
    public void Zoom(double factor) { Viewport.Zoom(factor, new Point(ActualWidth / 2, ActualHeight / 2)); InvalidateVisual(); }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Viewport.Resize(sizeInfo.NewSize);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Black, null, new Rect(RenderSize));
        _markers.Clear();
        if (_map is null || _image is null) return;
        dc.PushTransform(new MatrixTransform(Viewport.Scale, 0, 0, Viewport.Scale, Viewport.OffsetX, Viewport.OffsetY));
        if (SpoilerDetailView)
        {
            dc.PushOpacity(0.25);
            dc.DrawImage(_image, new Rect(0, 0, _map.ImageWidth, _map.ImageHeight));
            dc.Pop();
        }
        if (_spoilerMode) dc.PushClip(_revealed);
        dc.DrawImage(_image, new Rect(0, 0, _map.ImageWidth, _map.ImageHeight));
        if (_spoilerMode) dc.Pop();
        dc.Pop();
        if (SpoilerDetailView) DrawSpoilerDetails(dc);
        foreach (var room in Players.Where(p => p.Placement is not null && IsRoomVisible(p.Placement)).Select(p => p.Placement!).DistinctBy(r => r.RoomId))
            DrawRoom(dc, room, Brushes.Cyan);
        if (SelectedRoom is not null && IsRoomVisible(SelectedRoom)) DrawRoom(dc, SelectedRoom, Brushes.Gold);
        foreach (var group in Players.Where(p => p.Placement is not null && IsRoomVisible(p.Placement)).GroupBy(p => p.Placement!.RoomId))
        {
            int index = 0;
            foreach (var player in group.OrderBy(p => p.Id, StringComparer.Ordinal))
            {
                var room = player.Placement!;
                Point anchor = Viewport.ToScreen(new Point(room.X, room.Y));
                Point marker = anchor + new Vector(0, (index++ - (group.Count() - 1) / 2.0) * 27);
                if (marker.X < -100 || marker.Y < -30 || marker.X > ActualWidth + 100 || marker.Y > ActualHeight + 30) continue;
                _markers.Add((player, marker));
                dc.DrawLine(new Pen(Brushes.White, 1), anchor, marker);
                int color = player.Id.Aggregate(0, (hash, c) => (hash * 31 + c) & 0x7fffffff) % PlayerBrushes.Length;
                dc.DrawEllipse(player.State == "Dead" ? Brushes.Gray : PlayerBrushes[color], new Pen(Brushes.Black, 2), marker, 8, 8);
                if (player.Id == SelectedPlayerId) dc.DrawEllipse(null, new Pen(Brushes.White, 2), marker, 11, 11);
                var text = new FormattedText(player.Name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 12, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip) { MaxTextWidth = 170, Trimming = TextTrimming.CharacterEllipsis };
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(220, 15, 17, 19)), null,
                    new Rect(marker.X + 12, marker.Y - 10, text.Width + 8, 22), 3, 3);
                dc.DrawText(text, new Point(marker.X + 16, marker.Y - 8));
            }
        }
    }

    private void DrawSpoilerDetails(DrawingContext dc)
    {
        var rooms = RoomMapCatalog.ForMap(_map!.Id);
        dc.PushTransform(new MatrixTransform(Viewport.Scale, 0, 0, Viewport.Scale, Viewport.OffsetX, Viewport.OffsetY));
        dc.PushOpacity(0.35);
        dc.DrawGeometry(Brushes.LimeGreen, null, _revealed);
        dc.Pop();
        foreach (var room in rooms)
        {
            bool visited = _visited.Contains(room.RoomId);
            var brush = visited ? Brushes.DeepSkyBlue : Brushes.LightCoral;
            foreach (var bounds in room.Bounds)
            {
                var rectangle = new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
                if (!visited) rectangle.Inflate(1, 1);
                dc.DrawRectangle(null, new Pen(brush, 1 / Viewport.Scale), rectangle);
            }
        }
        dc.Pop();
        if (Viewport.Scale >= 0.45)
            foreach (var room in rooms)
            {
                var point = Viewport.ToScreen(new Point(room.X, room.Y));
                if (point.X < 0 || point.X > ActualWidth || point.Y < 0 || point.Y > ActualHeight) continue;
                DrawDiagnosticText(dc, room.RoomId, point + new Vector(5, 5), _visited.Contains(room.RoomId) ? Brushes.DeepSkyBlue : Brushes.LightCoral);
            }
        int visitedCount = rooms.Count(r => _visited.Contains(r.RoomId));
        DrawDiagnosticText(dc, $"SPOILER DETAILS: {( _spoilerMode ? "mask active" : "spoiler mode off, mask preview" )}\nBlue: visited bounds. Green: revealed. Red: excluded (+1 px).\n{visitedCount} visited / {rooms.Count - visitedCount} unvisited mapped rooms. Dim artwork is reference only.", new Point(12, 12), Brushes.White);
    }

    private void DrawDiagnosticText(DrawingContext dc, string value, Point point, Brush brush)
    {
        var text = new FormattedText(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 12, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
            { MaxTextWidth = Math.Max(100, ActualWidth - 35), Trimming = TextTrimming.CharacterEllipsis };
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(225, 15, 17, 19)), null,
            new Rect(point.X - 3, point.Y - 2, text.Width + 6, text.Height + 4));
        dc.DrawText(text, point);
    }

    private void DrawRoom(DrawingContext dc, MappedRoom room, Brush outline)
    {
        foreach (var rectangle in room.Bounds)
        {
            Point top = Viewport.ToScreen(new Point(rectangle.X, rectangle.Y));
            var bounds = new Rect(top.X, top.Y, rectangle.Width * Viewport.Scale, rectangle.Height * Viewport.Scale);
            dc.PushOpacity(0.25);
            dc.DrawRectangle(outline, null, bounds);
            dc.Pop();
            dc.DrawRectangle(null, new Pen(outline, 1.5), bounds);
        }
        if (room.Bounds.Count == 0)
            dc.DrawEllipse(null, new Pen(outline, 2), Viewport.ToScreen(new Point(room.X, room.Y)), 14, 14);
    }

    private LiveMapPlayer? HitPlayer(Point point) => _markers.Where(p => (p.Point - point).Length <= 13)
        .OrderBy(p => (p.Point - point).Length).Select(p => p.Player).FirstOrDefault();

    private MappedRoom? HitRoom(Point point, bool includeHidden = false)
    {
        if (_map is null) return null;
        Point image = Viewport.ToImage(point);
        return RoomMapCatalog.ForMap(_map.Id).Where(r => includeHidden || IsRoomVisible(r)).Where(r => r.Bounds.Any(b => new Rect(b.X, b.Y, b.Width, b.Height).Contains(image))
                || (Viewport.ToScreen(new Point(r.X, r.Y)) - point).Length < 10)
            .OrderBy(r => (new Point(r.X, r.Y) - image).Length).FirstOrDefault();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        Viewport.Zoom(Math.Pow(1.2, e.Delta / 120.0), e.GetPosition(this));
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        if (e.ClickCount == 2 && HitPlayer(e.GetPosition(this)) is { } player)
        {
            _press = null;
            PlayerFollowed?.Invoke(player.Id);
        }
        else { _press = _last = e.GetPosition(this); _dragged = false; CaptureMouse(); }
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        Point point = e.GetPosition(this);
        if (_press is { } start && e.LeftButton == MouseButtonState.Pressed)
        {
            if (!_dragged && (point - start).Length >= 4) { _dragged = true; ManuallyPanned?.Invoke(); }
            if (_dragged) { Viewport.Pan(point - _last); InvalidateVisual(); }
            _last = point;
            return;
        }
        if (SpoilerDetailView)
        {
            var imagePoint = Viewport.ToImage(point);
            var room = HitRoom(point, includeHidden: true);
            string details = room == null ? "Outside mapped room bounds" : $"{room.RoomId}: {(_visited.Contains(room.RoomId) ? "visited" : "unvisited")}\n{room.Bounds.Count} rectangles. Placement: {room.MatchKind}";
            ToolTip = $"{details}\nImage: {imagePoint.X:F1}, {imagePoint.Y:F1}\nSpoiler mask here: {(_revealed.FillContains(imagePoint) ? "revealed" : "hidden")}";
            return;
        }
        var player = HitPlayer(point);
        ToolTip = player is null ? HitRoom(point)?.RoomId : $"{player.Name}: {player.RoomId}. Double-click to follow.";
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        bool clicked = _press is not null && !_dragged;
        _press = null;
        ReleaseMouseCapture();
        if (clicked)
        {
            Point point = e.GetPosition(this);
            if (HitPlayer(point) is { } player) PlayerSelected?.Invoke(player.Id);
            else RoomSelected?.Invoke(HitRoom(point));
        }
        e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        var room = HitRoom(e.GetPosition(this));
        RoomSelected?.Invoke(room);
        if (room != null) RoomContextRequested?.Invoke(room);
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e) { _press = null; base.OnLostMouseCapture(e); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        Vector delta = e.Key switch { Key.Left => new(60, 0), Key.Right => new(-60, 0), Key.Up => new(0, 60), Key.Down => new(0, -60), _ => new() };
        if (delta.Length > 0) { ManuallyPanned?.Invoke(); Viewport.Pan(delta); InvalidateVisual(); e.Handled = true; }
        base.OnKeyDown(e);
    }
}
