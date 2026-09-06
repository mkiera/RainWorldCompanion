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
    public event Action<MappedRoom>? RoomSelected;
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
        dc.DrawImage(_image, new Rect(Viewport.OffsetX, Viewport.OffsetY, _map.ImageWidth * Viewport.Scale, _map.ImageHeight * Viewport.Scale));
        foreach (var room in Players.Where(p => p.Placement is not null).Select(p => p.Placement!).DistinctBy(r => r.RoomId))
            DrawRoom(dc, room, Brushes.Cyan);
        if (SelectedRoom is not null) DrawRoom(dc, SelectedRoom, Brushes.Gold);
        foreach (var group in Players.Where(p => p.Placement is not null).GroupBy(p => p.Placement!.RoomId))
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

    private MappedRoom? HitRoom(Point point)
    {
        if (_map is null) return null;
        Point image = Viewport.ToImage(point);
        return RoomMapCatalog.ForMap(_map.Id).Where(r => r.Bounds.Any(b => new Rect(b.X, b.Y, b.Width, b.Height).Contains(image))
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
            else if (HitRoom(point) is { } room) RoomSelected?.Invoke(room);
        }
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
