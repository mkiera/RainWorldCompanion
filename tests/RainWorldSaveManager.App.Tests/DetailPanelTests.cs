using RainWorldSaveManager.Core.Saves;
using RainWorldSaveManager.ViewModels;

namespace RainWorldSaveManager.App.Tests;

/// <summary>
/// One panel renders three different things: the live save folder, a backup, and a library save.
/// Two of them are folders holding up to six files, and the third is a single file, so the rules
/// that hold for a folder do not all hold for a library save.
///
/// The Rain Meadow block is the sharpest case. It pairs each online file with the local file of the
/// same slot number, which a folder has and one stored save does not.
/// </summary>
public class DetailPanelTests
{
    // ---- the Rain Meadow block ----

    [Fact]
    public void The_live_save_shows_the_meadow_block_when_the_mod_is_installed()
    {
        var panel = Panels.Live(Panels.Slot(1), Panels.Slot(1, SaveRealm.Online));
        panel.MeadowInstalled = true;

        Assert.True(panel.ShowMeadowSection);
    }

    [Fact]
    public void The_live_save_shows_the_meadow_block_even_with_no_online_file_yet()
    {
        // The rows are how a local save gets copied into an online slot that does not exist, so a
        // player who has the mod but has not played online still needs them.
        var panel = Panels.Live(Panels.Slot(1), Panels.Slot(2));
        panel.MeadowInstalled = true;

        Assert.True(panel.ShowMeadowSection);
        Assert.Equal(3, panel.SlotPairs.Count);
    }

    [Fact]
    public void Nothing_shows_the_meadow_block_when_the_mod_is_not_installed()
    {
        using var root = new TempDirectory("panel");

        var live = Panels.Live(Panels.Slot(1));
        var backup = Panels.Backup(root, Panels.Slot(1));
        var entry = Panels.Entry(root, Panels.Slot(1));

        Assert.False(live.ShowMeadowSection);
        Assert.False(backup.ShowMeadowSection);
        Assert.False(entry.ShowMeadowSection);
    }

    [Fact]
    public void A_backup_shows_the_meadow_block_when_the_mod_is_installed()
    {
        using var root = new TempDirectory("panel");
        var panel = Panels.Backup(root, Panels.Slot(1), Panels.Slot(1, SaveRealm.Online));
        panel.MeadowInstalled = true;

        Assert.True(panel.ShowMeadowSection);
    }

    [Fact]
    public void A_library_save_never_shows_the_meadow_block()
    {
        // The block pairs a slot's two halves. One stored save has no second half, so the block
        // would draw an empty row list and a paragraph about a command that cannot reach it.
        using var root = new TempDirectory("panel");
        var panel = Panels.Entry(root, Panels.Slot(1));
        panel.MeadowInstalled = true;

        Assert.False(panel.ShowMeadowSection);
    }

    [Fact]
    public void A_library_save_stored_from_an_online_slot_still_never_shows_the_block()
    {
        using var root = new TempDirectory("panel");
        var panel = Panels.Entry(root, Panels.Slot(2, SaveRealm.Online));
        panel.MeadowInstalled = true;

        Assert.False(panel.ShowMeadowSection);
        Assert.Empty(panel.SlotPairs);
    }

    // ---- a library save reads the same whichever slot it came from ----

    [Fact]
    public void A_library_save_from_an_online_slot_still_lists_its_campaigns()
    {
        // A folder keeps its online files out of Slots and puts them in the pair rows. Doing that
        // to a library save would leave the panel with nothing in it.
        using var root = new TempDirectory("panel");
        var panel = Panels.Entry(root, Panels.Slot(2, SaveRealm.Online, campaigns: 3));

        var section = Assert.Single(panel.Slots);
        Assert.Equal(3, section.Campaigns.Count);
    }

    [Fact]
    public void Both_realms_head_a_library_save_the_same_way()
    {
        using var root = new TempDirectory("panel");

        var local = Panels.Entry(root, Panels.Slot(2), folderNameSuffix: "local");
        var online = Panels.Entry(root, Panels.Slot(2, SaveRealm.Online), folderNameSuffix: "online");

        Assert.Equal("SLOT 2", Assert.Single(local.Slots).HeaderText);
        Assert.Equal("SLOT 2", Assert.Single(online.Slots).HeaderText);
    }

    [Fact]
    public void Both_realms_count_a_library_save_the_same_way()
    {
        using var root = new TempDirectory("panel");

        var local = Panels.Entry(root, Panels.Slot(2, campaigns: 2), folderNameSuffix: "local");
        var online = Panels.Entry(root, Panels.Slot(2, SaveRealm.Online, campaigns: 2), folderNameSuffix: "online");

        // "+2 online" belongs to a folder that shows its two realms in two places. One stored save
        // has one section.
        Assert.Equal("2 campaigns", local.CampaignCountText);
        Assert.Equal("2 campaigns", online.CampaignCountText);
    }

    [Fact]
    public void A_library_save_names_the_container_it_came_from()
    {
        // The metadata was parsed out of the library's own copy, so it says save.bin. Only the
        // manifest knows the file the save was taken from.
        using var root = new TempDirectory("panel");

        var panel = Panels.Entry(root, Panels.Slot(2, SaveRealm.Online));

        Assert.Equal("online_sav2", Assert.Single(panel.Slots).FileName);
    }

    // ---- the live save and a backup are unchanged ----

    [Fact]
    public void The_live_save_still_keeps_its_online_files_out_of_the_slot_sections()
    {
        var panel = Panels.Live(
            Panels.Slot(1),
            Panels.Slot(2),
            Panels.Slot(2, SaveRealm.Online));

        Assert.Equal(2, panel.Slots.Count);
        Assert.All(panel.Slots, slot => Assert.Equal(SaveRealm.Local, slot.Realm));
        Assert.Contains(panel.SlotPairs, pair => pair.Online.Exists);
    }

    [Fact]
    public void The_live_save_still_counts_its_two_realms_apart()
    {
        var panel = Panels.Live(
            Panels.Slot(1, campaigns: 4),
            Panels.Slot(2, SaveRealm.Online, campaigns: 1));

        Assert.Equal("4 campaigns +1 online", panel.CampaignCountText);
    }

    [Fact]
    public void The_live_save_heads_its_sections_by_slot_number()
    {
        var panel = Panels.Live(Panels.Slot(1), Panels.Slot(3));

        Assert.Equal(new[] { "SLOT 1", "SLOT 3" }, panel.Slots.Select(slot => slot.HeaderText).ToArray());
    }

    [Fact]
    public void A_backup_pairs_all_three_slots()
    {
        using var root = new TempDirectory("panel");
        var panel = Panels.Backup(root, Panels.Slot(1), Panels.Slot(1, SaveRealm.Online));

        Assert.Equal(3, panel.SlotPairs.Count);
        Assert.Equal(new[] { 1, 2, 3 }, panel.SlotPairs.Select(pair => pair.SlotNumber).ToArray());
    }

    // ---- which kind of thing the panel is showing ----

    [Fact]
    public void Each_kind_reports_only_itself()
    {
        using var root = new TempDirectory("panel");

        var live = Panels.Live(Panels.Slot(1));
        var backup = Panels.Backup(root, Panels.Slot(1));
        var entry = Panels.Entry(root, Panels.Slot(1));

        Assert.True(live.IsLive);
        Assert.False(live.HasBackup);
        Assert.False(live.HasEntry);

        Assert.False(backup.IsLive);
        Assert.True(backup.HasBackup);
        Assert.False(backup.HasEntry);

        Assert.False(entry.IsLive);
        Assert.False(entry.HasBackup);
        Assert.True(entry.HasEntry);
    }

    [Fact]
    public void A_library_save_says_where_it_came_from_in_its_subtitle()
    {
        using var root = new TempDirectory("panel");
        var panel = Panels.Entry(root, Panels.Slot(2, SaveRealm.Online));

        Assert.Contains("from online_sav2", panel.Subtitle, StringComparison.Ordinal);
    }
}
