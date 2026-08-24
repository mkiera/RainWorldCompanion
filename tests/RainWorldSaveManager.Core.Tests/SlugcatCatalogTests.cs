using System.Globalization;

using RainWorldSaveManager.Core.Saves;

namespace RainWorldSaveManager.Tests;

/// <summary>
/// The catalog turns the id stored in a save into the name and colour the UI paints. It is
/// asked about ids that came out of a file, so an id nobody has heard of has to come back as a
/// usable entry rather than as null.
/// </summary>
public class SlugcatCatalogTests
{
    [Theory]
    [InlineData("White", "Survivor")]
    [InlineData("Yellow", "Monk")]
    [InlineData("Red", "Hunter")]
    [InlineData("Gourmand", "Gourmand")]
    [InlineData("Artificer", "Artificer")]
    [InlineData("Rivulet", "Rivulet")]
    [InlineData("Spear", "Spearmaster")]
    [InlineData("Saint", "Saint")]
    [InlineData("Inv", "Inv")]
    [InlineData("Watcher", "Watcher")]
    public void Every_known_id_maps_to_its_in_game_name(string id, string displayName)
    {
        Assert.Equal(displayName, SlugcatCatalog.ForId(id).DisplayName);
    }

    [Theory]
    [InlineData("white")]
    [InlineData("WHITE")]
    [InlineData("wHiTe")]
    public void Lookup_is_case_insensitive(string id)
    {
        Assert.Equal("Survivor", SlugcatCatalog.ForId(id).DisplayName);
    }

    [Fact]
    public void The_known_list_holds_the_ten_campaigns_the_game_ships()
    {
        var ids = SlugcatCatalog.Known.Select(s => s.Id).ToArray();

        Assert.Equal(10, SlugcatCatalog.Known.Count);
        Assert.Equal(
            new[] { "Artificer", "Gourmand", "Inv", "Red", "Rivulet", "Saint", "Spear", "Watcher", "White", "Yellow" },
            ids.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    [Fact]
    public void Every_known_entry_carries_a_six_digit_hex_colour()
    {
        foreach (var slugcat in SlugcatCatalog.Known)
        {
            Assert.False(string.IsNullOrWhiteSpace(slugcat.DisplayName));
            AssertIsHexColour(slugcat.ColorHex);
        }
    }

    [Fact]
    public void The_known_entries_carry_the_colours_the_game_uses()
    {
        Assert.Equal("#E5E0DC", SlugcatCatalog.ForId("White").ColorHex, ignoreCase: true);
        Assert.Equal("#F2D74E", SlugcatCatalog.ForId("Yellow").ColorHex, ignoreCase: true);
        Assert.Equal("#D14B4B", SlugcatCatalog.ForId("Red").ColorHex, ignoreCase: true);
        Assert.Equal("#4C6E7A", SlugcatCatalog.ForId("Watcher").ColorHex, ignoreCase: true);
    }

    [Fact]
    public void Looking_up_a_known_id_returns_the_catalog_entry_itself()
    {
        var known = SlugcatCatalog.Known.Single(s => string.Equals(s.Id, "Saint", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(known.DisplayName, SlugcatCatalog.ForId("saint").DisplayName);
        Assert.Equal(known.ColorHex, SlugcatCatalog.ForId("saint").ColorHex);
    }

    [Fact]
    public void An_unknown_id_round_trips_as_its_own_display_name()
    {
        var slugcat = SlugcatCatalog.ForId("Bubbles");

        Assert.Equal("Bubbles", slugcat.Id, ignoreCase: true);
        Assert.Equal("Bubbles", slugcat.DisplayName);
        AssertIsHexColour(slugcat.ColorHex);
    }

    [Fact]
    public void An_unknown_id_is_not_added_to_the_known_list()
    {
        var before = SlugcatCatalog.Known.Count;

        SlugcatCatalog.ForId("Bubbles");

        Assert.Equal(before, SlugcatCatalog.Known.Count);
    }

    [Fact]
    public void A_null_id_comes_back_as_an_entry_rather_than_null()
    {
        var slugcat = SlugcatCatalog.ForId(null);

        Assert.NotNull(slugcat);
        Assert.NotNull(slugcat.Id);
        Assert.NotNull(slugcat.DisplayName);
        AssertIsHexColour(slugcat.ColorHex);
    }

    [Fact]
    public void An_empty_id_comes_back_as_an_entry_rather_than_null()
    {
        var slugcat = SlugcatCatalog.ForId("");

        Assert.NotNull(slugcat);
        Assert.NotNull(slugcat.DisplayName);
        AssertIsHexColour(slugcat.ColorHex);
    }

    private static void AssertIsHexColour(string colorHex)
    {
        Assert.False(string.IsNullOrWhiteSpace(colorHex));
        Assert.StartsWith("#", colorHex, StringComparison.Ordinal);
        Assert.Equal(7, colorHex.Length);
        Assert.True(
            int.TryParse(colorHex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _),
            colorHex + " is not a six digit hex colour");
    }
}
