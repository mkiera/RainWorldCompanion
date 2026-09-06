using System.Windows;
using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Controls;

public sealed class DenMapViewport
{
    public double Scale { get; private set; } = 1;
    public double OffsetX { get; private set; }
    public double OffsetY { get; private set; }
    public Size Size { get; private set; }
    public bool IsFitted { get; private set; } = true;
    public double FitScale => Size.Width > 0 && Size.Height > 0
        ? Math.Min(Size.Width / DenMapCatalog.ImageWidth, Size.Height / DenMapCatalog.ImageHeight) : 1;

    public Point ToScreen(Point image) => new(image.X * Scale + OffsetX, image.Y * Scale + OffsetY);
    public Point ToImage(Point screen) => new((screen.X - OffsetX) / Scale, (screen.Y - OffsetY) / Scale);

    public void Resize(Size size)
    {
        Point center = ToImage(new Point(Size.Width / 2, Size.Height / 2));
        Size = size;
        if (IsFitted)
        {
            Fit();
        }
        else
        {
            Scale = Math.Max(FitScale, Scale);
            Center(center);
        }
    }

    public void Fit()
    {
        IsFitted = true;
        Scale = FitScale;
        OffsetX = (Size.Width - DenMapCatalog.ImageWidth * Scale) / 2;
        OffsetY = (Size.Height - DenMapCatalog.ImageHeight * Scale) / 2;
    }

    public void Zoom(double factor, Point anchor)
    {
        Point image = ToImage(anchor);
        Scale = Math.Clamp(Scale * factor, FitScale, Math.Max(4, FitScale));
        OffsetX = anchor.X - image.X * Scale;
        OffsetY = anchor.Y - image.Y * Scale;
        IsFitted = false;
        Clamp();
    }

    public void Focus(MappedDen den)
    {
        Scale = Math.Max(FitScale, 1);
        IsFitted = false;
        Center(new Point(den.X, den.Y));
    }

    public void Pan(Vector delta)
    {
        IsFitted = false;
        OffsetX += delta.X;
        OffsetY += delta.Y;
        Clamp();
    }

    public MappedDen? HitTest(Point screen, IEnumerable<MappedDen> dens) => dens
        .Select(den => (Den: den, Distance: (ToScreen(new Point(den.X, den.Y)) - screen).Length))
        .Where(hit => hit.Distance <= Math.Max(14, 12 * Scale))
        .OrderBy(hit => hit.Distance)
        .Select(hit => hit.Den)
        .FirstOrDefault();

    private void Center(Point image)
    {
        OffsetX = Size.Width / 2 - image.X * Scale;
        OffsetY = Size.Height / 2 - image.Y * Scale;
        Clamp();
    }

    private void Clamp()
    {
        OffsetX = ClampAxis(OffsetX, Size.Width, DenMapCatalog.ImageWidth * Scale);
        OffsetY = ClampAxis(OffsetY, Size.Height, DenMapCatalog.ImageHeight * Scale);
    }

    private static double ClampAxis(double offset, double viewport, double image) => image <= viewport
        ? (viewport - image) / 2
        : Math.Clamp(offset, viewport - image - 40, 40);
}
