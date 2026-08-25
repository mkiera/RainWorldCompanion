using RainWorldSaveManager.Core.Saves;
using RainWorldSaveManager.ViewModels;

namespace RainWorldSaveManager.App.Tests;

/// <summary>
/// One slot section: the header line and the campaigns under it.
///
/// The two cases worth pinning are the header, which must not name a realm, and the line shown when
/// there is no campaign, because a Rain Meadow save routinely holds the explored map and the
/// progression record without one and reporting that as nothing is wrong.
/// </summary>
public class SlotSectionTests
{
    [Theory]
    [InlineData(SaveRealm.Local, 1, "SLOT 1")]
    [InlineData(SaveRealm.Local, 2, "SLOT 2")]
    [InlineData(SaveRealm.Online, 1, "SLOT 1")]
    [InlineData(SaveRealm.Online, 3, "SLOT 3")]
    public void A_header_names_the_slot_number_and_not_the_realm(SaveRealm realm, int slot, string expected)
    {
        var section = new SlotViewModel(Panels.Slot(slot, realm), new FakeIcons());

        Assert.Equal(expected, section.HeaderText);
    }

    [Fact]
    public void A_file_with_no_slot_number_falls_back_to_its_name()
    {
        var metadata = Panels.Slot(0, SaveRealm.Online, fileName: "online_sav-1");

        var section = new SlotViewModel(metadata, new FakeIcons());

        Assert.Equal("ONLINE_SAV-1", section.HeaderText);
        Assert.Equal("?", section.NumberText);
    }

    [Fact]
    public void An_overridden_name_replaces_the_one_the_metadata_carries()
    {
        // A library save is parsed out of the copy kept under the library's storage name, so the
        // name worth showing comes from the entry's manifest.
        var metadata = Panels.Slot(2, SaveRealm.Online, fileName: "save.bin");

        var section = new SlotViewModel(metadata, new FakeIcons(), "online_sav2");

        Assert.Equal("online_sav2", section.FileName);
    }

    [Fact]
    public void An_overridden_name_also_settles_the_header_of_a_file_with_no_slot_number()
    {
        var metadata = Panels.Slot(0, SaveRealm.Local, fileName: "save.bin");

        var section = new SlotViewModel(metadata, new FakeIcons(), "from a friend.sav");

        Assert.Equal("FROM A FRIEND.SAV", section.HeaderText);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void No_override_keeps_the_name_the_metadata_carries(string? given)
    {
        var section = new SlotViewModel(Panels.Slot(2), new FakeIcons(), given);

        Assert.Equal("sav2", section.FileName);
    }

    [Fact]
    public void A_save_with_records_but_no_campaign_says_what_it_still_holds()
    {
        // This is what a Rain Meadow online save looks like: 12 KB of explored map and a
        // progression record, with no campaign saved into it.
        var section = new SlotViewModel(
            Panels.Slot(1, SaveRealm.Online, campaigns: 0, recordCount: 4), new FakeIcons());

        Assert.True(section.HasNoCampaigns);
        Assert.Contains("map", section.EmptyText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("progression", section.EmptyText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_save_with_no_records_at_all_says_it_is_empty()
    {
        var section = new SlotViewModel(
            Panels.Slot(3, SaveRealm.Online, campaigns: 0, recordCount: 0), new FakeIcons());

        Assert.Contains("empty", section.EmptyText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("map", section.EmptyText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_save_that_could_not_be_read_says_so_instead()
    {
        var metadata = new Core.Saves.Models.SlotMetadata
        {
            Slot = 1,
            FileName = "sav",
            ParseError = "the closing tag was not found",
        };

        var section = new SlotViewModel(metadata, new FakeIcons());

        Assert.True(section.HasParseError);
        Assert.Contains("the closing tag was not found", section.EmptyText, StringComparison.Ordinal);
    }
}
