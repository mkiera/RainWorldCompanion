using System.IO;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.Core.Saves.Models;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.App.Tests;

public class DevourmentContentsTargetTests
{
    [Fact]
    public void Applying_contents_keeps_the_library_slot_link()
    {
        string source = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "ViewModelSource", "MainViewModel.cs"));
        int start = source.IndexOf("ApplyDevourmentContentsAsync", StringComparison.Ordinal);
        int end = source.IndexOf("DeleteCampaignAsync", start, StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start);
        Assert.DoesNotContain("ReleaseSlotClaimAsync", source[start..end], StringComparison.Ordinal);
    }

    [Fact]
    public void Every_live_campaign_is_offered_including_other_slugcats()
    {
        var slots = new[]
        {
            Slot(1, Campaign("White", 12), Campaign("Yellow", 34)),
            Slot(3, Campaign("Gourmand", 56)),
        };

        IReadOnlyList<DevourmentContentsTarget> targets = DevourmentContentsTarget.Build(slots);

        Assert.Collection(
            targets,
            target => AssertTarget(target, 1, "White", "Survivor"),
            target => AssertTarget(target, 1, "Yellow", "Monk"),
            target => AssertTarget(target, 3, "Gourmand", "Gourmand"));
    }

    [Fact]
    public void A_campaign_with_saved_devourment_contents_offers_the_action()
    {
        var summary = Campaign("White", 12, devourmentStateCount: 2);
        var source = new CampaignSource(
            @"C:\backup\sav",
            "a backup",
            null,
            SaveRealm.Local,
            1,
            "sav");

        var campaign = new CampaignViewModel(summary, new FakeIcons(), source);

        Assert.True(campaign.CanApplyDevourmentContents);
    }

    private static SlotMetadata Slot(int number, params CampaignSummary[] campaigns) =>
        new()
        {
            Slot = number,
            Realm = SaveRealm.Local,
            FileName = new SaveSlotRef(SaveRealm.Local, number).FileName,
            Campaigns = campaigns,
        };

    private static CampaignSummary Campaign(string slugcatId, int cycle, int devourmentStateCount = 0) =>
        new() { SlugcatId = slugcatId, CycleNum = cycle, DevourmentStateCount = devourmentStateCount };

    private static void AssertTarget(
        DevourmentContentsTarget target,
        int slot,
        string slugcatId,
        string campaignName)
    {
        Assert.Equal(slot, target.Slot.Slot);
        Assert.Equal(slugcatId, target.SlugcatId);
        Assert.Equal(campaignName, target.CampaignName);
        Assert.Contains("Slot " + slot, target.Label, StringComparison.Ordinal);
        Assert.Contains(campaignName, target.Label, StringComparison.Ordinal);
    }
}
