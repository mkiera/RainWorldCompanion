using System.IO;
using System.Text.RegularExpressions;

namespace RainWorldCompanion.App.Tests;

/// <summary>
/// A StaticResource that names a key nothing holds throws when the window loads, so a typo shows
/// up the first time anyone opens it. DynamicResource, which is what the theme swap needs, just
/// leaves the property unset: the element paints with no brush and nothing is reported. These
/// stand in for that missing error.
/// </summary>
public class ThemeTests
{
    private static readonly string XamlRoot =
        Path.Combine(AppContext.BaseDirectory, "Xaml");

    private static readonly Regex DeclaredKey = new(@"x:Key=""(?<key>[^""]+)""");
    private static readonly Regex UsedKey = new(@"\{DynamicResource (?<key>[^}]+)\}");
    private static readonly Regex UsedStaticKey = new(@"\{StaticResource (?<key>[^}]+)\}");

    [Fact]
    public void The_two_palettes_hold_the_same_keys()
    {
        var light = KeysDeclaredIn("Themes/Palette.Light.xaml");
        var dark = KeysDeclaredIn("Themes/Palette.Dark.xaml");

        Assert.NotEmpty(light);
        Assert.Empty(light.Except(dark).Order());
        Assert.Empty(dark.Except(light).Order());
    }

    [Fact]
    public void Every_colour_the_markup_asks_for_is_in_the_palette()
    {
        var palette = KeysDeclaredIn("Themes/Palette.Light.xaml");

        var missing = Directory
            .EnumerateFiles(XamlRoot, "*.xaml", SearchOption.AllDirectories)
            .SelectMany(file => UsedKey.Matches(File.ReadAllText(file))
                .Select(match => new { File = Path.GetFileName(file), Key = match.Groups["key"].Value }))
            .Where(used => !palette.Contains(used.Key))
            .Select(used => used.File + " asks for " + used.Key)
            .Distinct()
            .Order()
            .ToList();

        Assert.Empty(missing);
    }

    /// <summary>
    /// The styles carry the colours, so one left on StaticResource keeps its startup brush when
    /// the palette underneath it is swapped.
    /// </summary>
    [Fact]
    public void No_markup_reaches_a_palette_colour_through_StaticResource()
    {
        var stuck = Directory
            .EnumerateFiles(XamlRoot, "*.xaml", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains("{StaticResource Brush."))
            .Select(Path.GetFileName)
            .Order()
            .ToList();

        Assert.Empty(stuck);
    }

    // Unlike a missing DynamicResource, a missing StaticResource key throws and takes the window with it.
    [Fact]
    public void Every_style_the_markup_asks_for_by_name_is_declared_somewhere()
    {
        var files = Directory.EnumerateFiles(XamlRoot, "*.xaml", SearchOption.AllDirectories).ToList();

        var declared = files
            .SelectMany(file => DeclaredKey.Matches(File.ReadAllText(file))
                .Select(match => match.Groups["key"].Value))
            .ToHashSet(StringComparer.Ordinal);

        var missing = files
            .SelectMany(file => UsedStaticKey.Matches(File.ReadAllText(file))
                .Select(match => new { File = Path.GetFileName(file), Key = match.Groups["key"].Value }))
            // A key of {x:Type Foo} is the implicit style for that type, which no x:Key declares.
            .Where(used => !used.Key.StartsWith('{'))
            .Where(used => !declared.Contains(used.Key))
            .Select(used => used.File + " asks for " + used.Key)
            .Distinct()
            .Order()
            .ToList();

        Assert.Empty(missing);
    }

    // A key declared twice in one dictionary throws while that dictionary loads, which happens
    // before the first window is built and so takes the whole app down at startup.
    [Fact]
    public void No_dictionary_declares_the_same_key_twice()
    {
        var repeated = Directory
            .EnumerateFiles(XamlRoot, "*.xaml", SearchOption.AllDirectories)
            .SelectMany(file => DeclaredKey.Matches(File.ReadAllText(file))
                .Select(match => match.Groups["key"].Value)
                .GroupBy(key => key, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => Path.GetFileName(file) + " declares " + group.Key + " twice"))
            .Order()
            .ToList();

        Assert.Empty(repeated);
    }

    private static HashSet<string> KeysDeclaredIn(string relativePath)
    {
        var path = Path.Combine(XamlRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), path + " should have been copied beside the tests");

        return DeclaredKey.Matches(File.ReadAllText(path))
            .Select(match => match.Groups["key"].Value)
            .ToHashSet(StringComparer.Ordinal);
    }
}
