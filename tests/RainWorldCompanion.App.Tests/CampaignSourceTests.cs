using System.IO;

using RainWorldCompanion.Core.Library;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.App.Tests;

/// <summary>
/// The same card is drawn for a campaign in the live folder, one in a backup and one in a
/// library save. Taking a campaign out and sending it somewhere is a read of the file it is in,
/// so it works on all three. Editing it or removing it changes that file, so it works on the
/// live folder only: a backup edited in place is no longer a copy of anything.
/// </summary>
public class CampaignSourceTests
{
    [Fact]
    public void A_live_campaign_can_be_edited_and_taken()
    {
        CampaignViewModel campaign = Panels.Live(Panels.Slot(2)).Slots[0].Campaigns[0];

        Assert.True(campaign.CanEdit);
        Assert.True(campaign.CanBeTaken);
        Assert.True(campaign.HasActions);

        CampaignSource source = Assert.IsType<CampaignSource>(campaign.Source);
        Assert.Equal(Path.Combine(@"C:\saves", "sav2"), source.FilePath);
        Assert.Equal(new SaveSlotRef(SaveRealm.Local, 2), source.LiveSlot);
    }

    [Fact]
    public void A_campaign_in_a_backup_can_be_taken_but_not_edited()
    {
        using var root = new TempDirectory("panel");
        SnapshotDetailViewModel panel = Panels.Backup(root, Panels.Slot(2));

        CampaignViewModel campaign = panel.Slots[0].Campaigns[0];

        Assert.False(campaign.CanEdit);
        Assert.True(campaign.CanBeTaken);
        Assert.True(campaign.HasActions);

        CampaignSource source = Assert.IsType<CampaignSource>(campaign.Source);
        Assert.Null(source.LiveSlot);
        Assert.EndsWith(Path.Combine("2026-01-01_00-00-00", "sav2"), source.FilePath, StringComparison.Ordinal);
        Assert.Equal("backup 2026-01-01_00-00-00", source.Label);
    }

    /// <summary>
    /// A library save keeps a whole slot under the library's own storage name, so the file to go
    /// back to is that one and not the container it was taken from.
    /// </summary>
    [Fact]
    public void A_campaign_in_a_library_save_points_at_the_stored_copy()
    {
        using var root = new TempDirectory("panel");
        SnapshotDetailViewModel panel = Panels.Entry(root, Panels.Slot(2));

        CampaignViewModel campaign = panel.Slots[0].Campaigns[0];

        Assert.False(campaign.CanEdit);
        Assert.True(campaign.CanBeTaken);

        CampaignSource source = Assert.IsType<CampaignSource>(campaign.Source);
        Assert.Null(source.LiveSlot);
        Assert.EndsWith(LibraryEntry.SaveFileName, source.FilePath, StringComparison.Ordinal);
        Assert.Contains("a stored save", source.Label, StringComparison.Ordinal);
    }

    /// <summary>
    /// The compact rows in the live card are a summary, not something to act on, so their campaigns
    /// carry no source and no buttons.
    /// </summary>
    [Fact]
    public void A_campaign_with_nowhere_to_read_from_carries_no_buttons()
    {
        var campaign = new CampaignViewModel(
            Panels.Slot(2).Campaigns[0],
            new FakeIcons());

        Assert.False(campaign.CanEdit);
        Assert.False(campaign.CanBeTaken);
        Assert.False(campaign.HasActions);
        Assert.Null(campaign.Source);
    }

    /// <summary>
    /// A slot number outside 1 to 3 has no file the writer targets, so it is offered no Edit button
    /// even in the live folder. It can still be read and sent to a slot that is one.
    /// </summary>
    [Fact]
    public void A_save_outside_the_three_slots_can_be_taken_but_not_edited()
    {
        CampaignViewModel campaign = Panels
            .Live(Panels.Slot(0, fileName: "sav - Copy"))
            .Slots[0]
            .Campaigns[0];

        Assert.False(campaign.CanEdit);
        Assert.True(campaign.CanBeTaken);
        Assert.Equal(Path.Combine(@"C:\saves", "sav - Copy"), campaign.Source!.FilePath);
    }
}
