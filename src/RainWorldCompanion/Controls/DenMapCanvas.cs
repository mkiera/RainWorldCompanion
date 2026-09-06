using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Controls;

public sealed class DenMapCanvas : FrameworkElement
{
    private static readonly Lazy<BitmapSource> MapImage = new(LoadImage);
    private Point? _press;
    private Point _last;
    private bool _dragged;
    private MappedDen? _hovered;
    private MappedDen? _selected;

    public DenMapCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
        Cursor = Cursors.Hand;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
    }

    public DenMapViewport Viewport { get; } = new();
    public MappedDen? CurrentDen { get; set; }
    public event Action<MappedDen>? DenSelected;

    public void Load() => _ = MapImage.Value;

    public void Select(MappedDen? den, bool center)
    {
        _selected = den;
        if (center && den is not null)
        {
            Viewport.Focus(den);
        }
        InvalidateVisual();
    }

    public void Fit()
    {
        Viewport.Fit();
        InvalidateVisual();
    }

    public void FocusDen(MappedDen den)
    {
        Viewport.Focus(den);
        InvalidateVisual();
    }

    public void Zoom(double factor)
    {
        Viewport.Zoom(factor, new Point(ActualWidth / 2, ActualHeight / 2));
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Viewport.Resize(sizeInfo.NewSize);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Black, null, new Rect(RenderSize));
        dc.DrawImage(MapImage.Value, new Rect(Viewport.OffsetX, Viewport.OffsetY,
            DenMapCatalog.ImageWidth * Viewport.Scale, DenMapCatalog.ImageHeight * Viewport.Scale));
        foreach (MappedDen den in DenMapCatalog.All)
        {
            Point center = Viewport.ToScreen(new Point(den.X, den.Y));
            if (center.X < -30 || center.Y < -30 || center.X > ActualWidth + 30 || center.Y > ActualHeight + 30)
            {
                continue;
            }
            bool selected = den == _selected;
            bool current = den == CurrentDen;
            double radius = Math.Max(4, 12 * Viewport.Scale);
            if (current)
            {
                dc.DrawEllipse(null, new Pen(Brushes.Cyan, 2), center, radius + 4, radius + 4);
            }
            dc.DrawEllipse(null, new Pen(selected ? Brushes.Gold : den == _hovered ? Brushes.White : Brushes.LightGray,
                selected ? 3 : 1), center, radius, radius);
        }
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
            }
            if (_dragged)
            {
                Viewport.Pan(point - _last);
                InvalidateVisual();
            }
            _last = point;
            return;
        }
        MappedDen? hovered = Viewport.HitTest(point, DenMapCatalog.All);
        if (_hovered != hovered)
        {
            _hovered = hovered;
            ToolTip = hovered?.Label;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        bool click = _press is not null && !_dragged;
        _press = null;
        ReleaseMouseCapture();
        if (click && Viewport.HitTest(e.GetPosition(this), DenMapCatalog.All) is { } den)
        {
            DenSelected?.Invoke(den);
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
            Key.Left => new Vector(60, 0), Key.Right => new Vector(-60, 0),
            Key.Up => new Vector(0, 60), Key.Down => new Vector(0, -60), _ => null,
        };
        if (movement is { } delta)
        {
            Viewport.Pan(delta);
            InvalidateVisual();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    private static BitmapSource LoadImage()
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri("pack://application:,,,/RainWorldCompanion;component/Assets/Maps/Downpour.png");
        image.EndInit();
        if (image.PixelWidth != DenMapCatalog.ImageWidth || image.PixelHeight != DenMapCatalog.ImageHeight)
        {
            throw new InvalidOperationException("The map dimensions do not match the den catalog.");
        }
        image.Freeze();
        return image;
    }
}
