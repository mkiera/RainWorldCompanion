using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.Core.Saves.Models;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.App.Tests;

/// <summary>
/// Whether a stored slot is what the game holds right now. Three states have to stay apart the way
/// they do for mod settings: the same, different, and nothing to compare against. A campaign nobody
/// compared must never read as one that matched.
/// </summary>
public class LiveComparisonTests
{
    private static CampaignSummary Campaign(
        string slugcat = "White",
        int cycle = 10,
        int? karma = 5,
        bool ascended = false) =>
        new()
        {
            SlugcatId = slugcat,
            CycleNum = cycle,
            Karma = karma,
            KarmaCap = 10,
            Ascended = ascended,
        };

    private static SlotMetadata Slot(int number, params CampaignSummary[] campaigns) =>
        new()
        {
            Slot = number,
            FileName = new SaveSlotRef(SaveRealm.Local, number).FileName,
            Realm = SaveRealm.Local,
            ChecksumValid = true,
            RecordCount = campaigns.Length,
            Campaigns = campaigns,
        };

    private static SlotViewModel View(SlotMetadata slot, SlotMetadata? live) =>
        new(slot, new FakeIcons(), live: live);

    // ---- nothing to compare against ----

    [Fact]
    public void With_no_live_slot_a_row_makes_no_claim_either_way()
    {
        var view = View(Slot(1, Campaign()), null);

        Assert.False(view.ComparedToLive);
        Assert.False(view.DiffersFromLive);
        Assert.Equal("", view.LiveComparisonText);
        Assert.False(view.HasLiveComparisonText);
    }

    [Fact]
    public void A_campaign_the_live_slot_does_not_hold_leaves_its_tiles_unmarked()
    {
        var view = View(Slot(1, Campaign("Yellow")), Slot(1, Campaign("White")));

        var campaign = Assert.Single(view.Campaigns);
        Assert.False(campaign.ComparedToLive);
        Assert.Equal("", campaign.LiveComparisonText);
        Assert.DoesNotContain(campaign.RunStats, tile => tile.DiffersFromLive);
    }

    // ---- the same ----

    [Fact]
    public void The_same_values_read_as_the_same_as_live()
    {
        var view = View(Slot(1, Campaign()), Slot(1, Campaign()));

        Assert.True(view.ComparedToLive);
        Assert.False(view.DiffersFromLive);
        Assert.Equal("Same as live", view.LiveComparisonText);

        var campaign = Assert.Single(view.Campaigns);
        Assert.Equal("Same as live", campaign.LiveComparisonText);
        Assert.DoesNotContain(campaign.RunStats, tile => tile.DiffersFromLive);
        Assert.DoesNotContain(campaign.Badges, badge => badge.DiffersFromLive);
    }

    // ---- different ----

    [Fact]
    public void Only_the_tile_that_differs_is_marked()
    {
        var view = View(Slot(1, Campaign(cycle: 87)), Slot(1, Campaign(cycle: 10)));

        var campaign = Assert.Single(view.Campaigns);
        Assert.True(campaign.DiffersFromLive);
        Assert.Equal("Differs from live", campaign.LiveComparisonText);

        var cycle = Assert.Single(campaign.RunStats, tile => tile.Label == "Cycle");
        Assert.True(cycle.DiffersFromLive);

        // Everything else in the same group is left alone.
        Assert.DoesNotContain(campaign.RunStats, tile => tile.Label != "Cycle" && tile.DiffersFromLive);
    }

    [Fact]
    public void A_karma_difference_is_marked_on_the_karma_tile()
    {
        var view = View(Slot(1, Campaign(karma: 8)), Slot(1, Campaign(karma: 2)));

        var campaign = Assert.Single(view.Campaigns);
        Assert.True(Assert.Single(campaign.KarmaStats, tile => tile.Label == "Karma").DiffersFromLive);
    }

    [Fact]
    public void A_badge_that_is_on_one_side_only_is_marked()
    {
        var view = View(Slot(1, Campaign(ascended: true)), Slot(1, Campaign(ascended: false)));

        var campaign = Assert.Single(view.Campaigns);
        Assert.Contains(campaign.Badges, badge => badge.DiffersFromLive);
        Assert.True(campaign.DiffersFromLive);
    }

    [Fact]
    public void One_differing_campaign_makes_the_whole_slot_differ()
    {
        var view = View(
            Slot(1, Campaign("White"), Campaign("Yellow", cycle: 99)),
            Slot(1, Campaign("White"), Campaign("Yellow", cycle: 3)));

        Assert.True(view.DiffersFromLive);
        Assert.Equal("Differs from live", view.LiveComparisonText);
    }

    /// <summary>
    /// A campaign in one and not the other is a difference between the slots, even though that
    /// campaign has nothing of its own to compare.
    /// </summary>
    [Fact]
    public void A_slot_holding_a_campaign_the_live_one_lacks_differs()
    {
        var view = View(Slot(1, Campaign("White"), Campaign("Yellow")), Slot(1, Campaign("White")));

        Assert.True(view.DiffersFromLive);
    }

    [Fact]
    public void A_slot_missing_a_campaign_the_live_one_holds_differs()
    {
        var view = View(Slot(1, Campaign("White")), Slot(1, Campaign("White"), Campaign("Yellow")));

        Assert.True(view.DiffersFromLive);
    }

    // ---- the live panel itself ----

    /// <summary>
    /// The live folder is not compared with itself: every row would read "Same as live", which is
    /// noise standing where a real signal goes elsewhere.
    /// </summary>
    [Fact]
    public void The_live_panel_labels_nothing()
    {
        var panel = Panels.Live(Panels.Slot(1), Panels.Slot(2));

        foreach (var slot in panel.Slots)
        {
            Assert.False(slot.HasLiveComparisonText);

            foreach (var campaign in slot.Campaigns)
            {
                Assert.False(campaign.HasLiveComparisonText);
            }
        }
    }
}
