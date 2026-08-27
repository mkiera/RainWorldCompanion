// Usings sit above the namespace: RainWorldCompanion.Core.System would otherwise shadow System.
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.Core.System;

namespace RainWorldCompanion.Services;

/// <summary>
/// The portrait PNGs belong to the game publisher and are read from the player's own install,
/// never shipped with this app. Every image handed out is frozen, so one decoded on a worker
/// thread can be bound on the dispatcher.
/// </summary>
public sealed class SlugcatIconProvider : ISlugcatIconProvider
{
    private readonly object _gate = new();

    // A null value is a remembered miss: an id with no art is not probed twice.
    private readonly Dictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);

    private string? _installPath;

    /// <param name="installPath">Null costs the portraits and nothing else: every id draws its own.</param>
    public SlugcatIconProvider(string? installPath = null)
    {
        _installPath = Resolve(installPath);
    }

    public string? InstallPath
    {
        get
        {
            lock (_gate)
            {
                return _installPath;
            }
        }
    }

    public bool HasInstall => InstallPath is not null;

    /// <summary>
    /// A path that does not look like an install is treated as no install. Changing the path drops
    /// every cached image, because the cache belongs to the path it was filled from.
    /// </summary>
    public void UseInstall(string? installPath)
    {
        var resolved = Resolve(installPath);

        lock (_gate)
        {
            if (string.Equals(resolved, _installPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _installPath = resolved;
            _cache.Clear();
        }
    }

    /// <summary>
    /// The one call that touches disk. Run it on a background thread, before building the view
    /// models that will ask for these icons.
    /// </summary>
    public void Preload(IEnumerable<string> slugcatIds)
    {
        foreach (var id in slugcatIds)
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                Load(id.Trim());
            }
        }
    }

    /// <inheritdoc />
    public ImageSource GetIcon(string? slugcatId)
    {
        // The catalog answers for any id, including one from a mod it has never heard of.
        var slugcat = SlugcatCatalog.ForId(slugcatId);

        return Portrait(slugcatId) ?? FallbackSlugcatIcon.ForColor(slugcat.ColorHex);
    }

    /// <summary>Null when the install has no portrait for this id. Prefer <see cref="GetIcon"/>.</summary>
    public ImageSource? Portrait(string? slugcatId)
    {
        if (string.IsNullOrWhiteSpace(slugcatId))
        {
            return null;
        }

        return Load(slugcatId.Trim());
    }

    private ImageSource? Load(string slugcatId)
    {
        string? install;

        lock (_gate)
        {
            if (_cache.TryGetValue(slugcatId, out var cached))
            {
                return cached;
            }

            install = _installPath;
        }

        ImageSource? image = null;

        if (install is not null)
        {
            try
            {
                var file = GameInstallLocator.FindPortraitFile(install, slugcatId);
                if (file is not null)
                {
                    image = Decode(file);
                }
            }
            catch (Exception)
            {
                image = null;
            }
        }

        lock (_gate)
        {
            // A miss for the same id on two threads decodes twice and the second result wins.
            // Both are the same picture, so the read is not serialised.
            _cache[slugcatId] = image;
        }

        return image;
    }

    /// <summary>
    /// The player may be starting the game while this window is open, so the file is opened for
    /// sharing. OnLoad reads the whole image during EndInit, which is what lets the stream be
    /// disposed here rather than held open behind the image.
    /// </summary>
    private static ImageSource Decode(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.SequentialScan);

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();

        return AtScreenDpi(bitmap);
    }

    /// <summary>
    /// The art for Survivor, Monk, Hunter and Watcher is tagged 300 DPI while the rest is tagged
    /// 96, and WPF sizes an image by its DPI, so left alone those four measure 26.88 units against
    /// everyone else's 84. Only the stamp changes here, so no pixel is resampled.
    /// </summary>
    private static ImageSource AtScreenDpi(BitmapSource source)
    {
        const double ScreenDpi = 96;

        if (Math.Abs(source.DpiX - ScreenDpi) < 0.5 && Math.Abs(source.DpiY - ScreenDpi) < 0.5)
        {
            return source;
        }

        // Anything this large is not portrait art, and rebuilding it would cost more than the icon.
        if (source.PixelWidth is <= 0 or > 1024 || source.PixelHeight is <= 0 or > 1024)
        {
            return source;
        }

        // Copied in whatever format the decoder produced, so an image with premultiplied alpha
        // does not round trip through a straight one.
        var stride = ((source.PixelWidth * source.Format.BitsPerPixel) + 7) / 8;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);

        var restamped = BitmapSource.Create(
            source.PixelWidth,
            source.PixelHeight,
            ScreenDpi,
            ScreenDpi,
            source.Format,
            source.Palette,
            pixels,
            stride);

        restamped.Freeze();
        return restamped;
    }

    private static string? Resolve(string? installPath)
    {
        return GameInstallLocator.LooksLikeInstall(installPath) ? installPath!.Trim() : null;
    }
}
