using System.Windows;
using RainWorldCompanion.Core.Settings;

namespace RainWorldCompanion.Theming;

/// <summary>
/// Swaps the palette dictionary App.xaml merges first. Every style reaches its colours through
/// DynamicResource, so the windows already open repaint rather than needing a restart.
/// </summary>
public static class ThemeManager
{
    // Any key the palettes hold and nothing else does, to tell that dictionary from Theme.xaml.
    private const string PaletteMarkerKey = "Brush.Window";

    private static readonly Uri LightPalette = new("Themes/Palette.Light.xaml", UriKind.Relative);
    private static readonly Uri DarkPalette = new("Themes/Palette.Dark.xaml", UriKind.Relative);

    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    public static void Apply(AppTheme theme)
    {
        Current = theme;

        if (Application.Current is not { } app)
        {
            return;
        }

        var merged = app.Resources.MergedDictionaries;
        var replacement = new ResourceDictionary
        {
            Source = theme == AppTheme.Dark ? DarkPalette : LightPalette,
        };

        // Assigned in place rather than removed and re-added: a removal leaves every
        // DynamicResource unresolved for the moment in between, and WPF paints that gap.
        var index = IndexOfPalette(merged);
        if (index >= 0)
        {
            merged[index] = replacement;
        }
        else
        {
            merged.Insert(0, replacement);
        }
    }

    private static int IndexOfPalette(IList<ResourceDictionary> merged)
    {
        for (var i = 0; i < merged.Count; i++)
        {
            if (merged[i].Contains(PaletteMarkerKey))
            {
                return i;
            }
        }

        return -1;
    }
}
