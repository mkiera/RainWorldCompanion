using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Tests;

/// <summary>
/// Names read off the installed game, not written from memory. The check that matters is the
/// last one: every creature a real played save holds is one the catalog can name.
/// </summary>
public class CreatureCatalogTests
{
    [Fact]
    public void The_base_game_creatures_are_there()
    {
        Assert.True(CreatureCatalog.IsKnown("PinkLizard"));
        Assert.True(CreatureCatalog.IsKnown("Scavenger"));
        Assert.True(CreatureCatalog.IsKnown("KingVulture"));
        Assert.True(CreatureCatalog.IsKnown("Slugcat"));
    }

    [Fact]
    public void The_expansion_creatures_are_there_too()
    {
        Assert.Equal(CreatureSource.Downpour, CreatureCatalog.ForName("EelLizard").Source);
        Assert.Equal(CreatureSource.Downpour, CreatureCatalog.ForName("Inspector").Source);
        Assert.Equal(CreatureSource.Watcher, CreatureCatalog.ForName("BigMoth").Source);
        Assert.Equal(CreatureSource.Watcher, CreatureCatalog.ForName("DrillCrab").Source);
    }

    /// <summary>
    /// Both are bases the game builds other templates from, not creatures anything spawns, so a
    /// picker must not offer them. Free text still writes either.
    /// </summary>
    [Fact]
    public void The_two_names_that_are_templates_rather_than_creatures_are_left_out()
    {
        Assert.False(CreatureCatalog.IsKnown("StandardGroundCreature"));
        Assert.False(CreatureCatalog.IsKnown("LizardTemplate"));
    }

    [Fact]
    public void A_creature_the_catalog_has_never_heard_of_is_still_named_after_itself()
    {
        CreatureKind kind = CreatureCatalog.ForName("SomeModCreature");

        Assert.Equal("SomeModCreature", kind.Name);
        Assert.Equal("SomeModCreature", kind.DisplayName);
        Assert.False(CreatureCatalog.IsKnown("SomeModCreature"));
    }

    [Fact]
    public void The_display_name_puts_the_words_apart()
    {
        Assert.Equal("Pink Lizard", CreatureCatalog.ForName("PinkLizard").DisplayName);
        Assert.Equal("Daddy Long Legs", CreatureCatalog.ForName("DaddyLongLegs").DisplayName);
        Assert.Equal("Cicada A", CreatureCatalog.ForName("CicadaA").DisplayName);
    }

    [Fact]
    public void Searching_puts_what_starts_with_the_query_first()
    {
        var matches = CreatureCatalog.Search("Big").ToList();

        Assert.All(matches.Take(4), kind => Assert.StartsWith("Big", kind.Name, StringComparison.Ordinal));
        Assert.Contains(matches, kind => kind.Name == "BigEel");
        Assert.Contains(matches, kind => kind.Name == "BigMoth");
    }

    [Fact]
    public void Searching_matches_the_display_name_as_well_as_the_stored_one()
        => Assert.Contains(CreatureCatalog.Search("Long Legs"), kind => kind.Name == "BrotherLongLegs");

    [Fact]
    public void An_empty_search_offers_the_whole_list()
    {
        Assert.Equal(CreatureCatalog.Known.Count, CreatureCatalog.Search("").Count());
        Assert.Equal(CreatureCatalog.Known.Count, CreatureCatalog.Search(null).Count());
    }

    [Fact]
    public void No_name_is_in_the_list_twice()
        => Assert.Equal(
            CreatureCatalog.Known.Count,
            CreatureCatalog.Known.Select(kind => kind.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());

    /// <summary>Types a real save carries as swallowed prey. One the catalog cannot name means it was built from the wrong place.</summary>
    [Theory]
    [InlineData("BlackLizard")]
    [InlineData("BigMoth")]
    [InlineData("JetFish")]
    [InlineData("YellowLizard")]
    [InlineData("PinkLizard")]
    [InlineData("AquaCenti")]
    [InlineData("CicadaB")]
    [InlineData("CyanLizard")]
    [InlineData("EelLizard")]
    [InlineData("Slugcat")]
    [InlineData("SpitLizard")]
    [InlineData("Vulture")]
    [InlineData("VultureGrub")]
    [InlineData("WhiteLizard")]
    public void Every_creature_found_in_a_played_save_is_one_the_catalog_names(string type)
        => Assert.True(CreatureCatalog.IsKnown(type), type + " is in a real save but not in the catalog");
}
