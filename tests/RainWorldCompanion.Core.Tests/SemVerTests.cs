using RainWorldCompanion.Core.Updates;

namespace RainWorldCompanion.Tests;

/// <summary>
/// Getting this wrong silently offers a downgrade or nothing at all, so the spec's own precedence
/// chain is pinned here rather than sampled.
/// </summary>
public class SemVerTests
{
    private static SemVer Parse(string text)
    {
        Assert.True(SemVer.TryParse(text, out var version), text + " should parse");
        return version;
    }

    /// <summary>
    /// Section 11 of the Semantic Versioning 2.0.0 spec, as consecutive pairs. beta.2 below
    /// beta.11 catches a naive implementation: numeric identifiers compare as numbers, but a
    /// string comparison puts "11" before "2".
    /// </summary>
    [Theory]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [InlineData("1.0.0-alpha.beta", "1.0.0-beta")]
    [InlineData("1.0.0-beta", "1.0.0-beta.2")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11")]
    [InlineData("1.0.0-beta.11", "1.0.0-rc.1")]
    [InlineData("1.0.0-rc.1", "1.0.0")]
    [InlineData("1.0.0", "2.0.0")]
    [InlineData("2.0.0", "2.1.0")]
    [InlineData("2.1.0", "2.1.1")]
    public void Every_step_of_the_semver_precedence_chain_orders_correctly(string lower, string higher)
    {
        var low = Parse(lower);
        var high = Parse(higher);

        Assert.True(low < high, lower + " should rank below " + higher);
        Assert.True(high > low, higher + " should rank above " + lower);
        Assert.NotEqual(low, high);
    }

    // build-test.yml names a branch build after the last tag, the word alpha, and the commits on
    // top of that tag. These pin the ordering that naming depends on. A build that sorts below a
    // release already out is what made the what's new list replay 47 changes already seen.
    [Theory]
    [InlineData("1.2.0", "1.2.1-alpha.0.1")]
    [InlineData("1.2.1-alpha.0.1", "1.2.1-alpha.0.9")]
    [InlineData("1.2.1-alpha.0.9", "1.2.1-beta.1")]
    [InlineData("1.2.1-beta.1", "1.2.1-beta.1.alpha.1")]
    [InlineData("1.2.1-beta.1.alpha.1", "1.2.1-beta.1.alpha.7")]
    [InlineData("1.2.1-beta.1.alpha.7", "1.2.1-beta.2")]
    [InlineData("1.2.1-beta.2", "1.2.1-beta.2.alpha.2")]
    [InlineData("1.2.1-beta.2.alpha.2", "1.2.1")]
    [InlineData("1.2.1", "1.2.2-alpha.0.1")]
    public void A_branch_build_sits_between_the_tag_it_follows_and_the_next_one(string lower, string higher)
    {
        Assert.True(Parse(lower) < Parse(higher), lower + " should rank below " + higher);
    }

    [Fact]
    public void A_branch_build_named_for_a_patch_still_ranks_below_a_larger_release()
    {
        // Why naming a branch build 1.2.1-alpha.0.N commits to nothing: the release it turns out
        // to precede can be any size, and each of these still outranks it.
        var build = Parse("1.2.1-alpha.0.4");

        Assert.True(build > Parse("1.2.0"));
        Assert.True(build < Parse("1.2.1"));
        Assert.True(build < Parse("1.3.0-beta.1"));
        Assert.True(build < Parse("1.3.0"));
        Assert.True(build < Parse("2.0.0"));
    }

    [Fact]
    public void Every_branch_build_says_alpha_somewhere_in_its_tail()
    {
        // The version is read on its own in bug reports, the updates list and the install counts,
        // where a tail of bare numbers would pass for the release it was built after.
        foreach (var version in new[] { "1.2.1-alpha.0.4", "1.2.1-beta.1.alpha.7", "1.2.2-alpha.0.1" })
        {
            Assert.Contains("alpha", Parse(version).PreRelease, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_version_the_old_rule_produced_ranked_below_releases_that_were_already_out()
    {
        // The bug itself, kept as a test: 1.2.0 was released, and a branch build made afterwards
        // was still named a prerelease of it, so it landed under every 1.2.0 beta.
        var wrong = Parse("1.2.0-alpha.33");

        Assert.True(wrong < Parse("1.2.0-beta.2"));
        Assert.True(wrong < Parse("1.2.0"));

        Assert.True(Parse("1.2.1-alpha.0.33") > Parse("1.2.0"));
    }

    [Fact]
    public void A_release_outranks_every_prerelease_of_the_same_version()
    {
        Assert.True(Parse("1.1.0") > Parse("1.1.0-beta.1"));
        Assert.True(Parse("1.1.0") > Parse("1.1.0-rc.99"));
        Assert.True(Parse("1.1.0") > Parse("1.1.0-zzz"));
    }

    [Fact]
    public void A_numeric_prerelease_identifier_ranks_below_an_alphanumeric_one()
    {
        Assert.True(Parse("1.0.0-1") < Parse("1.0.0-alpha"));
        Assert.True(Parse("1.0.0-alpha.1") < Parse("1.0.0-alpha.a"));
    }

    [Fact]
    public void A_longer_run_of_identifiers_wins_when_the_shared_ones_match()
    {
        Assert.True(Parse("1.0.0-alpha") < Parse("1.0.0-alpha.0"));
        Assert.True(Parse("1.0.0-rc.1") < Parse("1.0.0-rc.1.1"));
    }

    /// <summary>
    /// The .NET SDK appends the commit to InformationalVersion, so a running build always carries
    /// metadata that its own release tag never has. If metadata took part in comparison, a build
    /// would never match its own release and would be offered it forever.
    /// </summary>
    [Fact]
    public void Build_metadata_takes_no_part_in_ordering_or_equality()
    {
        var stamped = Parse("1.1.0-beta.1+a1b2c3d4e5f6");
        var plain = Parse("1.1.0-beta.1");

        Assert.Equal(plain, stamped);
        Assert.Equal(0, stamped.CompareTo(plain));
        Assert.False(stamped > plain);
        Assert.False(stamped < plain);
        Assert.Equal(plain.GetHashCode(), stamped.GetHashCode());
        Assert.Equal("a1b2c3d4e5f6", stamped.BuildMetadata);
    }

    [Fact]
    public void A_numeric_identifier_wider_than_an_integer_still_orders_by_value()
    {
        // Valid semver, and nothing in the spec caps the width. Comparing the digits by length and
        // then by character says 999...9 is the larger without parsing either into a number.
        Assert.True(Parse("1.0.0-99999999999999999999") > Parse("1.0.0-99999999999999999998"));
        Assert.True(Parse("1.0.0-9") < Parse("1.0.0-10"));
    }

    [Theory]
    [InlineData("v1.2.3")]
    [InlineData("1.2.3")]
    [InlineData(" 1.2.3 ")]
    public void A_leading_v_and_surrounding_space_are_tolerated(string text)
    {
        var version = Parse(text);

        Assert.Equal(1, version.Major);
        Assert.Equal(2, version.Minor);
        Assert.Equal(3, version.Patch);
        Assert.False(version.IsPreRelease);
    }

    /// <summary>
    /// Anyone can push a tag, so the picker walks a list it does not control. An unplaceable tag
    /// must be passed over, which means parsing reports failure rather than throwing or guessing.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nightly")]
    [InlineData("1")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.x")]
    [InlineData("01.2.3")]
    [InlineData("1.02.3")]
    [InlineData("-1.2.3")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-alpha..1")]
    [InlineData("1.2.3-alpha.01")]
    [InlineData("1.2.3-al pha")]
    [InlineData("1.2.3+")]
    [InlineData("1.2.3+meta..data")]
    [InlineData("99999999999.0.0")]
    public void An_unorderable_tag_reports_failure_rather_than_a_guess(string? text)
    {
        Assert.False(SemVer.TryParse(text, out _));
    }

    [Fact]
    public void Build_metadata_may_hold_a_leading_zero_where_a_prerelease_identifier_may_not()
    {
        // Metadata is never compared, so the clause forbidding leading zeros does not apply to it.
        Assert.True(SemVer.TryParse("1.2.3+007", out _));
        Assert.False(SemVer.TryParse("1.2.3-007", out _));
    }

    [Fact]
    public void A_version_round_trips_through_its_text()
    {
        Assert.Equal("1.2.3", Parse("v1.2.3").ToString());
        Assert.Equal("1.2.3-beta.1", Parse("v1.2.3-beta.1").ToString());
        Assert.Equal("1.2.3-beta.1+abc", Parse("1.2.3-beta.1+abc").ToString());
    }

    [Fact]
    public void A_build_stamp_reads_its_version_and_shortens_its_commit()
    {
        var stamp = new BuildStamp("1.1.0-beta.1", "a1b2c3d4e5f60718293a", "feature/updater", "42");

        Assert.Equal("a1b2c3d", stamp.ShortSha);
        Assert.Equal(Parse("1.1.0-beta.1"), stamp.ParsedVersion);
    }

    [Fact]
    public void A_build_stamp_that_cannot_say_what_it_is_reads_as_no_version_at_all()
    {
        // Null rather than 0.0.0. A zero version would rank below every release and turn a copy
        // that cannot identify itself into one that accepts anything on offer.
        Assert.Null(BuildStamp.ForVersion("nightly").ParsedVersion);
        Assert.Equal("", BuildStamp.ForVersion("1.0.0").ShortSha);
    }
}
