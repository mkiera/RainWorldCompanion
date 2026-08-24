// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists in the referenced Core assembly, so a using written inside the namespace body risks
// binding "System" to that namespace instead of the BCL root.
using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;
using RainWorldSaveManager.Core.Saves;

namespace RainWorldSaveManager.Services;

/// <summary>
/// Draws the stand-in icon used when no portrait file resolves: a slugcat head in the slugcat's
/// own colour, on a transparent ground, in an 84x84 box so it lines up with the real portraits.
///
/// This is a normal part of the UI rather than an error state. Inv ships no portrait at all, so
/// its icon is always this one, and a player with the game installed elsewhere sees it for every
/// campaign.
/// </summary>
public static class FallbackSlugcatIcon
{
    /// <summary>Side of the logical box, matching the 84x84 portrait PNGs.</summary>
    public const double Size = 84;

    // The head, front on, drawn once in the 84x84 box. The ear wedges start inside the head so
    // the union has no seam where they meet.
    private const string HeadPath =
        "M 42,20.5 C 57,20.5 67,32 67,46 C 67,60.5 56,74.5 42,74.5 " +
        "C 28,74.5 17,60.5 17,46 C 17,32 27,20.5 42,20.5 Z";

    private const string LeftEarPath =
        "M 23,31.5 C 18.5,21 17.5,11.5 21.5,9.5 C 25.5,7.5 31,15.5 36,24 Z";

    private const string RightEarPath =
        "M 61,31.5 C 65.5,21 66.5,11.5 62.5,9.5 C 58.5,7.5 53,15.5 48,24 Z";

    // What the eye colour and the outline are mixed towards. Near black with a cold cast, so the
    // features stay readable on a pale head like Survivor and on a dark one like Inv.
    private static readonly Color Ink = Color.FromRgb(0x0F, 0x11, 0x14);

    // SlugcatCatalog.NeutralColorHex, written out so a colour that fails to parse cannot send
    // this class back through the parser.
    private static readonly Color NeutralColor = Color.FromRgb(0x9E, 0x9E, 0x9E);

    private static readonly Geometry SilhouetteGeometry = BuildSilhouette();

    private static readonly Geometry EyesGeometry = BuildEyes();

    // A transparent square pinning the drawing to the full 84x84 box. Without it the image would
    // report the bounds of the head alone and would not line up with a real portrait.
    private static readonly Geometry BoxGeometry =
        Frozen(new RectangleGeometry(new Rect(0, 0, Size, Size)));

    private static readonly ConcurrentDictionary<Color, ImageSource> ByColor = new();

    /// <summary>
    /// The icon for a slugcat id, using the colour the catalog gives that id. Unknown ids get the
    /// catalog's neutral grey rather than nothing.
    /// </summary>
    public static ImageSource ForSlugcat(string? slugcatId)
    {
        return ForColor(SlugcatCatalog.ForId(slugcatId).ColorHex);
    }

    /// <summary>
    /// The icon for a "#RRGGBB" colour. Cached per colour, so the nine campaigns of one save cost
    /// at most nine drawings and a redraw costs none. A colour that will not parse falls back to
    /// neutral grey.
    /// </summary>
    public static ImageSource ForColor(string? colorHex)
    {
        return ByColor.GetOrAdd(ParseColor(colorHex), static color => Draw(color));
    }

    private static ImageSource Draw(Color bodyColor)
    {
        var body = Frozen(new SolidColorBrush(bodyColor));
        var featureColor = MixTowardsInk(bodyColor, 0.80);
        var features = Frozen(new SolidColorBrush(featureColor));

        // The outline is stroked on the unioned silhouette, so it traces the outside only and the
        // ear joins stay invisible.
        var outline = Frozen(new Pen(features, 2)
        {
            LineJoin = PenLineJoin.Round,
        });

        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(Brushes.Transparent, null, BoxGeometry));
        drawing.Children.Add(new GeometryDrawing(body, outline, SilhouetteGeometry));
        drawing.Children.Add(new GeometryDrawing(features, null, EyesGeometry));
        drawing.Freeze();

        return Frozen(new DrawingImage(drawing));
    }

    private static Geometry BuildSilhouette()
    {
        var head = Geometry.Parse(HeadPath);
        var withLeftEar = Geometry.Combine(
            head,
            Geometry.Parse(LeftEarPath),
            GeometryCombineMode.Union,
            null);

        return Frozen(Geometry.Combine(
            withLeftEar,
            Geometry.Parse(RightEarPath),
            GeometryCombineMode.Union,
            null));
    }

    private static Geometry BuildEyes()
    {
        var eyes = new GeometryGroup { FillRule = FillRule.Nonzero };
        eyes.Children.Add(Eye(31, 46, 14));
        eyes.Children.Add(Eye(53, 46, -14));
        return Frozen(eyes);
    }

    /// <summary>
    /// One eye: an oval tilted so it slopes down towards the middle of the face. The two tilts
    /// mirror each other.
    /// </summary>
    private static EllipseGeometry Eye(double centerX, double centerY, double tiltDegrees)
    {
        return new EllipseGeometry(new Point(centerX, centerY), 7.5, 6)
        {
            Transform = new RotateTransform(tiltDegrees, centerX, centerY),
        };
    }

    /// <summary>
    /// Pulls a colour most of the way towards the ink, keeping a trace of the body hue. Every
    /// catalog colour ends up dark enough to read against its own head.
    /// </summary>
    private static Color MixTowardsInk(Color color, double amount)
    {
        var keep = 1 - amount;
        return Color.FromRgb(
            (byte)Math.Round((color.R * keep) + (Ink.R * amount)),
            (byte)Math.Round((color.G * keep) + (Ink.G * amount)),
            (byte)Math.Round((color.B * keep) + (Ink.B * amount)));
    }

    private static Color ParseColor(string? colorHex)
    {
        if (string.IsNullOrWhiteSpace(colorHex))
        {
            return NeutralColor;
        }

        try
        {
            if (ColorConverter.ConvertFromString(colorHex.Trim()) is Color parsed)
            {
                return parsed;
            }
        }
        catch (Exception)
        {
            // A colour the converter rejects is a data problem, never a reason to fail to draw.
        }

        return NeutralColor;
    }

    /// <summary>
    /// Freezes so the result can cross threads and so WPF stops tracking it for changes.
    /// </summary>
    private static T Frozen<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
