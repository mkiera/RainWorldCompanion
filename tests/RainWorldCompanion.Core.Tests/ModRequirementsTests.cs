using RainWorldCompanion.Core.Mods;

namespace RainWorldCompanion.Tests;

/// <summary>
/// What the game turns on with a mod. A requirement can have requirements of its own, and two mods
/// listing each other is a shape real mods have, so the walk has to terminate on its own rather
/// than trusting the data to be a tree.
/// </summary>
public class ModRequirementsTests
{
    private static ModEntry Mod(string id, params string[] requires) =>
        new()
        {
            Id = id,
            Name = id,
            Origin = ModEntry.InstallOrigin,
            Requirements = requires.ToList(),
        };

    [Fact]
    public void A_mod_that_needs_nothing_pulls_in_nothing()
    {
        Assert.Empty(ModRequirements.Closure("pearlcat", new[] { Mod("pearlcat") }));
    }

    [Fact]
    public void What_a_mod_names_is_pulled_in()
    {
        var closure = ModRequirements.Closure(
            "pearlcat",
            new[] { Mod("pearlcat", "slime-cubed.slugbase"), Mod("slime-cubed.slugbase") });

        Assert.Equal(new[] { "slime-cubed.slugbase" }, closure);
    }

    /// <summary>diverse_shelters needs regionkit, and regionkit needs pom.</summary>
    [Fact]
    public void A_requirement_of_a_requirement_comes_too()
    {
        var closure = ModRequirements.Closure(
            "diverse_shelters",
            new[]
            {
                Mod("diverse_shelters", "regionkit", "crs"),
                Mod("regionkit", "pom"),
                Mod("crs"),
                Mod("pom"),
            });

        Assert.Equal(new[] { "regionkit", "crs", "pom" }, closure);
    }

    [Fact]
    public void The_mod_itself_is_never_in_its_own_closure()
    {
        var closure = ModRequirements.Closure("a", new[] { Mod("a", "a", "b"), Mod("b") });

        Assert.Equal(new[] { "b" }, closure);
    }

    [Fact]
    public void Two_mods_naming_each_other_terminate()
    {
        var closure = ModRequirements.Closure("a", new[] { Mod("a", "b"), Mod("b", "a") });

        Assert.Equal(new[] { "b" }, closure);
    }

    [Fact]
    public void A_longer_cycle_terminates_too()
    {
        var closure = ModRequirements.Closure(
            "a",
            new[] { Mod("a", "b"), Mod("b", "c"), Mod("c", "a") });

        Assert.Equal(new[] { "b", "c" }, closure);
    }

    /// <summary>
    /// It is what the mod asked for, so it is named. Dropping it would say the mod needed nothing
    /// when the reason it may not work is exactly this.
    /// </summary>
    [Fact]
    public void A_requirement_nothing_provides_is_still_named()
    {
        var closure = ModRequirements.Closure("a", new[] { Mod("a", "not-here") });

        Assert.Equal(new[] { "not-here" }, closure);
    }

    [Fact]
    public void The_chain_stops_at_a_mod_that_is_not_on_disk()
    {
        // Nothing can be read about what an absent mod needs, so nothing is guessed.
        var closure = ModRequirements.Closure("a", new[] { Mod("a", "missing") });

        Assert.Equal(new[] { "missing" }, closure);
    }

    [Fact]
    public void Ids_are_matched_without_regard_to_case()
    {
        var closure = ModRequirements.Closure("A", new[] { Mod("a", "B"), Mod("b") });

        Assert.Equal(new[] { "B" }, closure);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_mod_with_no_id_pulls_in_nothing(string? modId)
    {
        Assert.Empty(ModRequirements.Closure(modId, new[] { Mod("a", "b") }));
    }

    [Fact]
    public void Nothing_installed_pulls_in_nothing()
    {
        Assert.Empty(ModRequirements.Closure("a", null));
    }

    [Fact]
    public void A_blank_requirement_is_skipped()
    {
        Assert.Empty(ModRequirements.Closure("a", new[] { Mod("a", "", "   ") }));
    }

    // ---- the other direction ----

    [Fact]
    public void What_needs_a_mod_names_only_the_ones_left_on()
    {
        var installed = new[] { Mod("a", "b"), Mod("c", "b"), Mod("d"), Mod("b") };

        var needing = ModRequirements.WhatNeeds("b", new[] { "a", "c", "d" }, installed);

        Assert.Equal(new[] { "a", "c" }, needing);
    }

    [Fact]
    public void What_needs_a_mod_reaches_through_a_chain()
    {
        var installed = new[] { Mod("a", "b"), Mod("b", "c"), Mod("c") };

        Assert.Equal(new[] { "a" }, ModRequirements.WhatNeeds("c", new[] { "a" }, installed));
    }

    [Fact]
    public void A_mod_does_not_count_as_needing_itself()
    {
        var installed = new[] { Mod("a", "a") };

        Assert.Empty(ModRequirements.WhatNeeds("a", new[] { "a" }, installed));
    }
}
