// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists in the referenced Core assembly, so a using written inside the namespace body would
// bind "System" to that namespace instead of the BCL root.
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RainWorldSaveManager.Core.Saves;
using RainWorldSaveManager.Core.System;

namespace RainWorldSaveManager.Services;

/// <summary>
/// Hands out the icon for a save's slugcat id: the game's own portrait art when the install has
/// it, and a drawn head when it does not.
///
/// The PNGs belong to the game publisher. They are never copied into this repo, never added to
/// the project as resources and never shipped. Each one is read from the player's own install the
/// first time it is asked for and held in memory for the life of the process. That is why
/// <see cref="FallbackSlugcatIcon"/> exists: a player whose install this app cannot find still
/// gets a complete list, drawn rather than empty. Inv has no portrait in any install, so its icon
/// is always the drawn one.
///
/// <see cref="Preload"/> is the call that touches disk and is meant to run on a background
/// thread. Everything after that is a dictionary hit. Every image handed out is frozen, so one
/// decoded on a worker thread can be bound on the dispatcher.
/// </summary>
public sealed class SlugcatIconProvider : ISlugcatIconProvider
{
    private readonly object _gate = new();

    // Keyed by slugcat id, so a slot with nine campaigns reads at most nine files and a redraw
    // reads none. A null value is a remembered miss: an id with no art is not probed twice.
    private readonly Dictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);

    private string? _installPath;

    /// <param name="installPath">
    /// The Rain World install, or null when none was found. Null costs portraits and nothing
    /// else: every id then draws its own icon.
    /// </param>
    public SlugcatIconProvider(string? installPath = null)
    {
        _installPath = Resolve(installPath);
    }

    /// <summary>The install the portraits are read from, or null when none was usable.</summary>
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

    /// <summary>True when a folder that looks like a Rain World install is in use.</summary>
    public bool HasInstall => InstallPath is not null;

    /// <summary>
    /// Points the provider at a different install, for when the setting changes while the window
    /// is open. A path that does not look like an install is treated as no install at all.
    /// Changing the path drops every cached image, because the cache belongs to the path it was
    /// filled from.
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
    /// Reads and decodes the portraits for these slugcat ids. Call it from a background thread
    /// before building the view models that will ask for them.
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
        // The catalog answers for any id, including one from a mod it has never heard of, so the
        // colour for the drawn icon is always there.
        var slugcat = SlugcatCatalog.ForId(slugcatId);

        return Portrait(slugcatId) ?? FallbackSlugcatIcon.ForColor(slugcat.ColorHex);
    }

    /// <summary>
    /// The portrait file for a slugcat id, or null when the install has none. Prefer
    /// <see cref="GetIcon"/> unless the caller wants to render its own stand-in.
    /// </summary>
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
                // A moved file, a locked one, a truncated one, a drive that went away or art this
                // app cannot decode all mean the same thing: no portrait, so the drawn icon is
                // used. An icon is never worth taking the window down for.
                image = null;
            }
        }

        lock (_gate)
        {
            // A miss for the same id on two threads decodes twice and the second result wins.
            // Both are the same picture, so the read does not need to be serialised.
            _cache[slugcatId] = image;
        }

        return image;
    }

    /// <summary>
    /// Decodes a PNG into a frozen image at a size that matches every other icon.
    ///
    /// The player may be starting the game while this window is open, so the file is opened for
    /// sharing and closed as soon as the pixels are decoded. OnLoad reads the whole image during
    /// EndInit, which is what lets the stream be disposed here instead of being held open behind
    /// the image for as long as it is on screen.
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
    /// Restamps an image to 96 DPI so its layout size equals its pixel size.
    ///
    /// Every portrait in the install is 84 by 84 pixels, but the art for Survivor, Monk, Hunter
    /// and Watcher is tagged 300 DPI while the rest is tagged 96. WPF sizes an image by its DPI,
    /// so left alone those four measure 26.88 units against everyone else's 84 and the list shows
    /// two sizes of icon. Only the stamp changes here, so no pixel is resampled and the drawn
    /// fallback lines up with all of them.
    /// </summary>
    private static ImageSource AtScreenDpi(BitmapSource source)
    {
        const double ScreenDpi = 96;

        if (Math.Abs(source.DpiX - ScreenDpi) < 0.5 && Math.Abs(source.DpiY - ScreenDpi) < 0.5)
        {
            return source;
        }

        // Portrait art is tiny. Anything far larger is not a portrait, and rebuilding it would
        // cost more memory than the icon is worth, so it is left at whatever size it claims.
        if (source.PixelWidth is <= 0 or > 1024 || source.PixelHeight is <= 0 or > 1024)
        {
            return source;
        }

        // Copied in whatever format the decoder produced, so no pixel is converted on the way
        // through and an image with premultiplied alpha does not round trip through a straight
        // one.
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
