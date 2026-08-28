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

    /// <summary>
    /// A slot that matches wears no chip at all. Labelling agreement everywhere would bury the one
    /// row that disagrees, which is the row worth finding.
    /// </summary>
    [Fact]
    public void A_slot_that_matches_says_nothing()
    {
        var view = View(Slot(1, Campaign()), Slot(1, Campaign()));

        Assert.True(view.ComparedToLive);
        Assert.False(view.DiffersFromLive);
        Assert.Equal("", view.LiveComparisonText);
        Assert.False(view.HasLiveComparisonText);

        var campaign = Assert.Single(view.Campaigns);
        Assert.False(campaign.HasLiveComparisonText);
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

    // ---- every panel that shows stored bytes ----

    /// <summary>
    /// Both panels ask the same question of the same live folder, so both have to be handed it.
    /// A factory that takes the live slots and then forgets to pass them on shows nothing, which
    /// is exactly what an unlabelled row looks like when there is genuinely nothing to compare.
    /// </summary>
    [Fact]
    public void A_backup_panel_marks_the_slot_that_differs()
    {
        using var root = new TempDirectory("backups");

        var panel = Panels.Backup(
            root,
            new[] { Slot(1, Campaign(cycle: 10)) },
            Slot(1, Campaign(cycle: 87)));

        var slot = Assert.Single(panel.Slots);
        Assert.Equal("Differs from live", slot.LiveComparisonText);
        Assert.True(Assert.Single(slot.Campaigns).DiffersFromLive);
    }

    /// <summary>
    /// A matching slot shows nothing, which is also what an unwired panel shows, so this asserts
    /// the comparison happened rather than what it printed.
    /// </summary>
    [Fact]
    public void A_backup_panel_compares_a_slot_that_matches_and_shows_nothing()
    {
        using var root = new TempDirectory("backups");

        var panel = Panels.Backup(root, new[] { Slot(1, Campaign()) }, Slot(1, Campaign()));

        var slot = Assert.Single(panel.Slots);
        Assert.True(slot.ComparedToLive);
        Assert.False(slot.HasLiveComparisonText);
    }

    [Fact]
    public void A_library_panel_marks_the_slot_that_differs()
    {
        using var root = new TempDirectory("library");

        var panel = Panels.Entry(
            root,
            Slot(1, Campaign(cycle: 87)),
            liveSlots: new[] { Slot(1, Campaign(cycle: 10)) });

        var slot = Assert.Single(panel.Slots);
        Assert.Equal("Differs from live", slot.LiveComparisonText);
    }

    [Fact]
    public void A_panel_handed_no_live_folder_labels_nothing()
    {
        using var root = new TempDirectory("backups");

        var panel = Panels.Backup(root, Slot(1, Campaign()));

        var slot = Assert.Single(panel.Slots);
        Assert.False(slot.ComparedToLive);
        Assert.False(slot.HasLiveComparisonText);
    }

    // ---- the live panel itself ----

    /// <summary>
    /// The live folder is not compared with itself, so nothing in it is ever marked as differing
    /// from itself.
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
