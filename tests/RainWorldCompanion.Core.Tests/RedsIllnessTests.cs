using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.Core.Saves.Models;

namespace RainWorldCompanion.Tests;

/// <summary>
/// Hunter is shown RedsIllness.RedsCycles minus the stored cycle, not the cycle itself.
/// SaveState.LoadGame also clears the REDSDEATH flag on load whenever the stored cycle is below
/// that limit, so the token on disk is not the flag the game actually runs with.
/// </summary>
public class RedsIllnessTests
{
    [Fact]
    public void The_limit_is_nineteen_cycles_and_twenty_four_with_the_extra_ones()
    {
        Assert.Equal(19, RedsIllness.RedsCycles(false));
        Assert.Equal(24, RedsIllness.RedsCycles(true));
    }

    /// <summary>
    /// The live Hunter campaign stores CYCLENUM 5 and carries no REDEXTRACYCLES token, and the
    /// game's own save select screen reads "Cycle 14" for it.
    /// </summary>
    [Fact]
    public void Hunter_is_shown_the_cycles_left_rather_than_the_cycles_played()
    {
        var campaign = Campaign("Red", cycleNum: 5);

        Assert.Equal(5, campaign.CycleNum);
        Assert.Equal(14, campaign.DisplayCycleNum);
    }

    [Fact]
    public void The_extra_cycles_flag_moves_the_countdown_by_five()
    {
        var campaign = Campaign("Red", cycleNum: 5, redExtraCycles: true);

        Assert.Equal(19, campaign.DisplayCycleNum);
    }

    /// <summary>Every campaign in the live slot but Hunter's, with the cycle each one stores.</summary>
    [Theory]
    [InlineData("White", 87)]
    [InlineData("Yellow", 28)]
    [InlineData("Gourmand", 85)]
    [InlineData("Artificer", 34)]
    [InlineData("Rivulet", 47)]
    [InlineData("Spear", 98)]
    [InlineData("Saint", 88)]
    [InlineData("Watcher", 149)]
    public void Every_other_slugcat_is_shown_the_number_the_save_stores(string slugcatId, int cycle)
    {
        Assert.Equal(cycle, Campaign(slugcatId, cycle).DisplayCycleNum);
    }

    [Fact]
    public void A_record_with_no_cycle_number_has_none_to_show()
    {
        Assert.Null(Campaign("Red", cycleNum: null).DisplayCycleNum);
        Assert.Null(Campaign("White", cycleNum: null).DisplayCycleNum);
    }

    /// <summary>Past the limit the countdown goes negative, exactly as HUD.Map.CycleLabel does.</summary>
    [Fact]
    public void A_hunter_run_past_the_limit_counts_below_zero()
    {
        Assert.Equal(0, Campaign("Red", 19).DisplayCycleNum);
        Assert.Equal(-3, Campaign("Red", 22).DisplayCycleNum);
    }

    /// <summary>
    /// The live Hunter campaign stores the REDSDEATH token on cycle 5, and the game clears the
    /// flag on load because 5 is under the limit.
    /// </summary>
    [Fact]
    public void A_stored_token_inside_the_cycle_limit_is_cleared_on_load()
    {
        var campaign = Campaign("Red", cycleNum: 5, redsDeathStored: true);

        Assert.True(campaign.RedsDeathStored);
        Assert.False(campaign.EffectiveRedsDeath);
    }

    [Fact]
    public void A_stored_token_at_or_past_the_cycle_limit_survives_the_load()
    {
        Assert.True(Campaign("Red", 19, redsDeathStored: true).EffectiveRedsDeath);
        Assert.True(Campaign("Red", 30, redsDeathStored: true).EffectiveRedsDeath);
    }

    [Fact]
    public void The_extra_cycles_flag_moves_the_point_the_load_stops_clearing_it()
    {
        Assert.False(Campaign("Red", 19, redsDeathStored: true, redExtraCycles: true).EffectiveRedsDeath);
        Assert.True(Campaign("Red", 24, redsDeathStored: true, redExtraCycles: true).EffectiveRedsDeath);
    }

    [Fact]
    public void A_campaign_with_no_token_has_no_flag_whatever_the_cycle()
    {
        Assert.False(Campaign("Red", 30).EffectiveRedsDeath);
        Assert.False(Campaign("Red", 5).EffectiveRedsDeath);
    }

    /// <summary>
    /// A schema 1 backup recorded no cycle number, so there is nothing to apply the rule to and
    /// the stored token stands rather than being cleared on no evidence.
    /// </summary>
    [Fact]
    public void A_record_with_no_cycle_number_keeps_the_token_as_stored()
    {
        Assert.True(Campaign("Red", cycleNum: null, redsDeathStored: true).EffectiveRedsDeath);
    }

    [Theory]
    [InlineData("Red", true)]
    [InlineData("red", true)]
    [InlineData("White", false)]
    [InlineData("Saint", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_the_hunter_campaign_counts_down(string? slugcatId, bool isHunter)
    {
        Assert.Equal(isHunter, RedsIllness.IsHunter(slugcatId));
    }

    /// <summary>
    /// REDEXTRACYCLES is written twice, once in the SAVE STATE record and once inside
    /// DEATHPERSISTENTSAVEDATA, and SaveState.RedExtraCycles is true when either is set.
    /// </summary>
    [Theory]
    [InlineData("REDEXTRACYCLES", "")]
    [InlineData("", "REDEXTRACYCLES")]
    [InlineData("REDEXTRACYCLES", "REDEXTRACYCLES")]
    public void Either_extra_cycles_token_counts(string saveStateFlag, string deathFlag)
    {
        var fields = new List<string> { CampaignFixture.Field("SAV STATE NUMBER", "Red"), CampaignFixture.Field("CYCLENUM", "5") };

        if (saveStateFlag.Length != 0)
        {
            fields.Add(saveStateFlag);
        }

        fields.Add(deathFlag.Length == 0
            ? CampaignFixture.DeathData(CampaignFixture.DeathEntry("KARMA", "0"))
            : CampaignFixture.DeathData(deathFlag));

        var campaign = CampaignFixture.Campaign(fields.ToArray());

        Assert.True(campaign.RedExtraCycles);
        Assert.Equal(19, campaign.DisplayCycleNum);
    }

    [Fact]
    public void Neither_token_leaves_the_flag_clear()
    {
        var campaign = CampaignFixture.Campaign(
            CampaignFixture.Field("SAV STATE NUMBER", "Red"),
            CampaignFixture.Field("CYCLENUM", "5"));

        Assert.False(campaign.RedExtraCycles);
        Assert.Equal(14, campaign.DisplayCycleNum);
    }

    private static CampaignSummary Campaign(
        string? slugcatId,
        int? cycleNum,
        bool redsDeathStored = false,
        bool redExtraCycles = false)
        => new()
        {
            SlugcatId = slugcatId ?? "",
            CycleNum = cycleNum,
            RedsDeathStored = redsDeathStored,
            RedExtraCycles = redExtraCycles,
        };
}
