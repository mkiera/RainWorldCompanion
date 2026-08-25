// Usings sit above the namespace declaration on purpose. RainWorldCompanion.Core.System
// exists in the Core assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Text.Json;

using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Saves.Models;

namespace RainWorldCompanion.Tests;

/// <summary>
/// The number stored under KARMA is not the number a player sees, and it is not always the number
/// the game plays with either. Two rules out of the game explain the gap:
///
/// DeathPersistentSaveData.FromString ends with an unconditional
/// Custom.IntClamp(this.karma, 0, this.karmaCap), so a stored value outside 0..cap is discarded on
/// load. HUD.KarmaMeter builds sprite names as "smallKarma" + karma over smallKarma0 to
/// smallKarma9, so the stored value is a 0-based index and the meter reads one higher.
///
/// The raw <see cref="CampaignSummary.Karma"/> and <see cref="CampaignSummary.KarmaCap"/> stay
/// exactly as they sit on disk, because that is what a save editor writes back. Everything the UI
/// shows is derived from them, and this suite pins that derivation.
/// </summary>
public class KarmaMathTests
{
    // ---- The nine campaigns in the live save folder ----

    /// <summary>
    /// Every campaign in the real save folder, with the karma the game loads and the karma the
    /// meter shows for each. Two of them store a value the game throws away: Yellow and Watcher
    /// sit exactly one above their cap, and Artificer and Saint store the -1 that
    /// VoidSea.VoidWorm.MainWormBehavior.Update writes during the ascension sequence.
    /// </summary>
    [Theory]
    [InlineData("White", 7, 9, 7, 8, 10, false)]
    [InlineData("Yellow", 10, 9, 9, 10, 10, true)]
    [InlineData("Gourmand", 8, 8, 8, 9, 9, false)]
    [InlineData("Artificer", -1, 0, 0, 1, 1, true)]
    [InlineData("Rivulet", 6, 6, 6, 7, 7, false)]
    [InlineData("Spear", 4, 6, 4, 5, 7, false)]
    [InlineData("Saint", -1, 9, 0, 1, 10, true)]
    [InlineData("Watcher", 5, 4, 4, 5, 5, true)]
    [InlineData("Red", 0, 4, 0, 1, 5, false)]
    public void A_real_campaign_reports_the_karma_the_game_loads_and_the_karma_the_meter_shows(
        string slugcatId,
        int storedKarma,
        int storedCap,
        int expectedEffective,
        int expectedDisplayKarma,
        int expectedDisplayCap,
        bool expectedOutOfRange)
    {
        var campaign = Campaign(storedKarma, storedCap, slugcatId);

        Assert.Equal(storedKarma, campaign.Karma);
        Assert.Equal(storedCap, campaign.KarmaCap);
        Assert.Equal(expectedEffective, campaign.EffectiveKarma);
        Assert.Equal(expectedDisplayKarma, campaign.DisplayKarma);
        Assert.Equal(expectedDisplayCap, campaign.DisplayKarmaCap);
        Assert.Equal(expectedOutOfRange, campaign.KarmaStoredOutOfRange);
    }

    /// <summary>The same nine, as the one string the campaign card puts on screen.</summary>
    [Theory]
    [InlineData("White", 7, 9, "8 / 10")]
    [InlineData("Yellow", 10, 9, "10 / 10")]
    [InlineData("Gourmand", 8, 8, "9 / 9")]
    [InlineData("Artificer", -1, 0, "1 / 1")]
    [InlineData("Rivulet", 6, 6, "7 / 7")]
    [InlineData("Spear", 4, 6, "5 / 7")]
    [InlineData("Saint", -1, 9, "1 / 10")]
    [InlineData("Watcher", 5, 4, "5 / 5")]
    [InlineData("Red", 0, 4, "1 / 5")]
    public void A_real_campaign_formats_its_karma_the_way_the_meter_reads(
        string slugcatId, int storedKarma, int storedCap, string expectedText)
    {
        Assert.Equal(expectedText, Campaign(storedKarma, storedCap, slugcatId).KarmaText);
    }

    // ---- The three cases the clamp exists for ----

    [Fact]
    public void Karma_stored_above_the_cap_loads_as_the_cap()
    {
        var campaign = Campaign(10, 9);

        Assert.Equal(10, campaign.Karma);
        Assert.Equal(9, campaign.EffectiveKarma);
        Assert.Equal(10, campaign.DisplayKarma);
        Assert.Equal(10, campaign.DisplayKarmaCap);
        Assert.True(campaign.KarmaStoredOutOfRange);
        Assert.Equal("10 / 10", campaign.KarmaText);
    }

    [Fact]
    public void Karma_stored_as_minus_one_loads_as_the_lowest_level()
    {
        var campaign = Campaign(-1, 0);

        Assert.Equal(-1, campaign.Karma);
        Assert.Equal(0, campaign.EffectiveKarma);
        Assert.Equal(1, campaign.DisplayKarma);
        Assert.Equal(1, campaign.DisplayKarmaCap);
        Assert.True(campaign.KarmaStoredOutOfRange);
        Assert.Equal("1 / 1", campaign.KarmaText);
    }

    [Fact]
    public void Karma_stored_inside_the_range_survives_the_clamp_untouched()
    {
        var campaign = Campaign(7, 9);

        Assert.Equal(7, campaign.EffectiveKarma);
        Assert.Equal(8, campaign.DisplayKarma);
        Assert.Equal(10, campaign.DisplayKarmaCap);
        Assert.False(campaign.KarmaStoredOutOfRange);
        Assert.Equal("8 / 10", campaign.KarmaText);
    }

    // ---- Boundaries ----

    [Fact]
    public void Karma_equal_to_the_cap_is_in_range()
    {
        var campaign = Campaign(9, 9);

        Assert.Equal(9, campaign.EffectiveKarma);
        Assert.Equal(10, campaign.DisplayKarma);
        Assert.False(campaign.KarmaStoredOutOfRange);
        Assert.Equal("10 / 10", campaign.KarmaText);
    }

    [Fact]
    public void Karma_zero_at_a_cap_of_zero_is_in_range_and_reads_as_one_of_one()
    {
        // The Artificer constructor sets karmaCap 0, which the game shows as a cap of 1.
        var campaign = Campaign(0, 0);

        Assert.Equal(0, campaign.EffectiveKarma);
        Assert.Equal(1, campaign.DisplayKarma);
        Assert.Equal(1, campaign.DisplayKarmaCap);
        Assert.False(campaign.KarmaStoredOutOfRange);
        Assert.Equal("1 / 1", campaign.KarmaText);
    }

    [Fact]
    public void The_default_starting_cap_of_four_reads_as_five()
    {
        // DeathPersistentSaveData's constructor defaults karmaCap to 4, the well known cap of 5.
        Assert.Equal(5, Campaign(0, 4).DisplayKarmaCap);
    }

    [Fact]
    public void The_maximum_cap_of_nine_reads_as_ten()
    {
        // SSOracleBehavior.Update and MoreSlugcats.HRKarmaShrine.Update both set karmaCap 9.
        Assert.Equal(10, Campaign(0, 9).DisplayKarmaCap);
    }

    // ---- Missing fields ----

    [Fact]
    public void A_record_with_no_karma_field_derives_nothing_from_it()
    {
        var campaign = Campaign(null, 9);

        Assert.Null(campaign.Karma);
        Assert.Null(campaign.EffectiveKarma);
        Assert.Null(campaign.DisplayKarma);
        Assert.False(campaign.KarmaStoredOutOfRange);
        Assert.Equal("-", campaign.KarmaText);

        // The cap is still known, so it still converts.
        Assert.Equal(10, campaign.DisplayKarmaCap);
    }

    [Fact]
    public void A_record_with_no_cap_clamps_the_lower_bound_only()
    {
        // Nothing bounds the value from above, so a stored 12 is taken at face value.
        var campaign = Campaign(12, null);

        Assert.Equal(12, campaign.EffectiveKarma);
        Assert.Equal(13, campaign.DisplayKarma);
        Assert.Null(campaign.DisplayKarmaCap);
        Assert.False(campaign.KarmaStoredOutOfRange);
        Assert.Equal("13", campaign.KarmaText);
        Assert.DoesNotContain("/", campaign.KarmaText);
    }

    [Fact]
    public void A_record_with_no_cap_still_lifts_a_stored_minus_one_to_the_lowest_level()
    {
        var campaign = Campaign(-1, null);

        Assert.Equal(0, campaign.EffectiveKarma);
        Assert.Equal(1, campaign.DisplayKarma);
        Assert.Null(campaign.DisplayKarmaCap);
        Assert.True(campaign.KarmaStoredOutOfRange);
        Assert.Equal("1", campaign.KarmaText);
    }

    [Fact]
    public void A_record_with_neither_field_derives_nothing_at_all()
    {
        var campaign = Campaign(null, null);

        Assert.Null(campaign.EffectiveKarma);
        Assert.Null(campaign.DisplayKarma);
        Assert.Null(campaign.DisplayKarmaCap);
        Assert.False(campaign.KarmaStoredOutOfRange);
        Assert.Equal("-", campaign.KarmaText);
    }

    // ---- A cap below zero, which Math.Clamp cannot survive ----

    /// <summary>
    /// No save has been seen with a negative cap, but a hand-edited file can hold one and the
    /// reader takes whatever parses. Math.Clamp throws when its min is above its max, so reading
    /// karma through it would turn a junk field into a crash. RWCustom.Custom.IntClamp does not
    /// throw. Its IL is three tests in a fixed order:
    ///
    ///   if (val &lt; inclMin) return inclMin;
    ///   if (val &gt; inclMax) return inclMax;
    ///   return val;
    ///
    /// With inclMin 0 above inclMax -1 the value falls out of whichever test it fails first, so a
    /// stored 5 comes back as -1 and a stored -1 comes back as 0. Both answers are asserted
    /// literally: a switch to Math.Clamp throws here, and a Max(min, Min(max, val)) rewrite
    /// answers 0 to both, so either change fails this test rather than reaching a user.
    /// </summary>
    [Theory]
    [InlineData(5, -1, -1)]
    [InlineData(0, -1, -1)]
    [InlineData(-1, -1, 0)]
    [InlineData(-5, -1, 0)]
    [InlineData(3, -4, -4)]
    public void A_cap_below_zero_follows_the_games_clamp_instead_of_throwing(
        int storedKarma, int storedCap, int expectedEffective)
    {
        var campaign = Campaign(storedKarma, storedCap);

        Assert.Equal(expectedEffective, campaign.EffectiveKarma);
        Assert.Equal(expectedEffective + 1, campaign.DisplayKarma);
        Assert.Equal(storedCap + 1, campaign.DisplayKarmaCap);
        Assert.Equal(storedKarma != expectedEffective, campaign.KarmaStoredOutOfRange);
    }

    [Fact]
    public void A_cap_below_zero_still_formats_rather_than_throwing()
    {
        Assert.Equal("0 / 0", Campaign(5, -1).KarmaText);
    }

    // ---- A cap or a karma at the top of the range, which the +1 cannot survive ----

    /// <summary>
    /// The reader takes any number int.TryParse accepts, so a hand-edited KARMACAP of 2147483647
    /// reaches this. Nothing in the assembly is compiled checked, so adding 1 to it wraps to
    /// -2147483648 and the chip renders "8 / -2147483648" as if it were a cap. A number that
    /// cannot be a karma level falls back to the dash the rest of the file uses for unusable input.
    /// </summary>
    [Fact]
    public void A_cap_at_the_top_of_the_range_reads_as_missing_rather_than_wrapping()
    {
        var campaign = Campaign(7, int.MaxValue);

        Assert.Equal(int.MaxValue, campaign.KarmaCap);
        Assert.Equal(7, campaign.EffectiveKarma);
        Assert.Equal(8, campaign.DisplayKarma);
        Assert.Null(campaign.DisplayKarmaCap);
        Assert.Equal("8", campaign.KarmaText);
    }

    [Fact]
    public void A_karma_at_the_top_of_the_range_reads_as_missing_rather_than_wrapping()
    {
        // With no cap recorded nothing bounds the value from above, so the stored number passes
        // through EffectiveKarma untouched and lands on the increment.
        var campaign = Campaign(int.MaxValue, null);

        Assert.Equal(int.MaxValue, campaign.EffectiveKarma);
        Assert.Null(campaign.DisplayKarma);
        Assert.Equal("-", campaign.KarmaText);
    }

    [Fact]
    public void A_karma_at_the_top_of_the_range_still_clamps_to_a_cap_that_is_below_it()
    {
        var campaign = Campaign(int.MaxValue, 9);

        Assert.Equal(9, campaign.EffectiveKarma);
        Assert.Equal(10, campaign.DisplayKarma);
        Assert.True(campaign.KarmaStoredOutOfRange);
        Assert.Equal("10 / 10", campaign.KarmaText);
    }

    // ---- Serialisation ----

    /// <summary>The camelCase names the derived properties would take if they were serialised.</summary>
    private static readonly string[] DerivedKarmaJsonNames =
    {
        "effectiveKarma", "displayKarma", "displayKarmaCap", "karmaStoredOutOfRange", "karmaText",
    };

    [Fact]
    public void A_manifest_records_the_stored_karma_and_none_of_the_numbers_derived_from_it()
    {
        using var world = new BackupWorld();
        var snapshot = world.Service.CreateBackup("today", null);

        using var document = JsonDocument.Parse(File.ReadAllText(snapshot.ManifestPath));

        var campaigns = document.RootElement.GetProperty("slots").EnumerateArray()
            .SelectMany(slot => slot.GetProperty("campaigns").EnumerateArray())
            .ToList();

        Assert.NotEmpty(campaigns);

        // The live tree is built from sav2.bin, which stores KARMA 6 with KARMACAP 9, and
        // sav3.bin, which stores KARMA 3 with KARMACAP 4. Both go into the file untouched.
        var stored = new List<(int Karma, int Cap)>();

        foreach (var campaign in campaigns)
        {
            Assert.True(campaign.TryGetProperty("karma", out var karma), "a campaign has no karma");
            Assert.True(campaign.TryGetProperty("karmaCap", out var cap), "a campaign has no karmaCap");
            stored.Add((karma.GetInt32(), cap.GetInt32()));
        }

        Assert.Contains(stored, pair => pair == (6, 9));
        Assert.Contains(stored, pair => pair == (3, 4));

        foreach (var campaign in campaigns)
        {
            foreach (var name in DerivedKarmaJsonNames)
            {
                Assert.False(
                    campaign.TryGetProperty(name, out _),
                    name + " was written to the manifest, but it is computed from karma and karmaCap");
            }
        }
    }

    [Fact]
    public void A_campaign_reads_its_karma_back_out_of_a_manifest_and_derives_the_rest_again()
    {
        using var world = new BackupWorld();
        var snapshot = world.Service.CreateBackup("today", null);

        var reloaded = BackupSnapshot.Load(snapshot.DirectoryPath);
        var campaign = reloaded.Manifest!.Slots
            .SelectMany(slot => slot.Campaigns)
            .First(c => c.KarmaCap == 4);

        Assert.Equal(3, campaign.Karma);
        Assert.Equal(3, campaign.EffectiveKarma);
        Assert.Equal(4, campaign.DisplayKarma);
        Assert.Equal(5, campaign.DisplayKarmaCap);
        Assert.False(campaign.KarmaStoredOutOfRange);
        Assert.Equal("4 / 5", campaign.KarmaText);
    }

    private static CampaignSummary Campaign(int? karma, int? karmaCap, string slugcatId = "White")
        => new() { SlugcatId = slugcatId, Karma = karma, KarmaCap = karmaCap };
}
