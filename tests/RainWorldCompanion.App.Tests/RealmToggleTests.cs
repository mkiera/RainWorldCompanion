using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.App.Tests;

/// <summary>
/// A library save is the case that needs guarding. It is one file, the toggle is not drawn for
/// it, and the window carries the realm from one selection to the next, so a panel that
/// honoured the realm there would show nothing at all.
/// </summary>
public class RealmToggleTests
{
    [Fact]
    public void The_sections_start_on_the_local_saves()
    {
        var panel = Panels.Live(Panels.Slot(1), Panels.Slot(1, SaveRealm.Online));

        Assert.False(panel.ShowOnline);
        Assert.True(panel.ShowLocal);
        Assert.Equal("sav", Assert.Single(panel.Slots).FileName);
    }

    [Fact]
    public void Turning_the_toggle_swaps_which_saves_the_sections_show()
    {
        var panel = Panels.Live(Panels.Slot(1), Panels.Slot(2, SaveRealm.Online));

        panel.ShowOnline = true;

        Assert.Equal("online_sav2", Assert.Single(panel.Slots).FileName);
    }

    [Fact]
    public void Setting_either_half_of_the_toggle_moves_the_other()
    {
        // Both halves bind two way against these two properties, so they have to stay opposites
        // however either one is set.
        var panel = Panels.Live(Panels.Slot(1));

        panel.ShowOnline = true;
        Assert.False(panel.ShowLocal);

        panel.ShowLocal = true;
        Assert.False(panel.ShowOnline);

        panel.ShowLocal = false;
        Assert.True(panel.ShowOnline);
    }

    [Fact]
    public void Turning_the_toggle_raises_the_properties_the_sections_are_bound_to()
    {
        var panel = Panels.Live(Panels.Slot(1), Panels.Slot(1, SaveRealm.Online));
        var raised = new List<string>();
        panel.PropertyChanged += (_, args) => raised.Add(args.PropertyName ?? "");

        panel.ShowOnline = true;

        Assert.Contains(nameof(panel.Slots), raised);
        Assert.Contains(nameof(panel.ShowLocal), raised);
        Assert.Contains(nameof(panel.HasSlots), raised);
        Assert.Contains(nameof(panel.HasNoSlots), raised);
        Assert.Contains(nameof(panel.ActiveEmptyText), raised);
        Assert.Contains(nameof(panel.HasNoOnlineSlots), raised);
    }

    [Fact]
    public void The_empty_line_names_the_realm_the_toggle_is_on()
    {
        var panel = Panels.Live(Panels.Slot(1));

        Assert.False(panel.HasNoSlots);

        panel.ShowOnline = true;

        Assert.True(panel.HasNoSlots);
        Assert.Contains("No online saves", panel.ActiveEmptyText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_backup_names_the_realm_that_came_up_short()
    {
        using var root = new TempDirectory("realm");
        var panel = Panels.Backup(root, Panels.Slot(1, SaveRealm.Online));

        // Only an online file, so it is the local sections that have nothing to draw. Saying "no
        // save files" here would be wrong: the snapshot holds one.
        Assert.True(panel.HasNoSlots);
        Assert.Contains("no local saves", panel.ActiveEmptyText, StringComparison.Ordinal);

        panel.ShowOnline = true;

        Assert.False(panel.HasNoSlots);
    }

    [Fact]
    public void The_pair_rows_stop_repeating_the_line_the_sections_are_already_showing()
    {
        var panel = Panels.Live(Panels.Slot(1));

        Assert.True(panel.HasNoOnlineSlots);

        panel.ShowOnline = true;

        Assert.False(panel.HasNoOnlineSlots);
    }

    [Fact]
    public void A_library_save_keeps_its_one_file_on_screen_whatever_the_realm_says()
    {
        using var root = new TempDirectory("realm");
        var panel = Panels.Entry(root, Panels.Slot(2, SaveRealm.Online));

        panel.ShowOnline = true;

        Assert.Single(panel.Slots);
        Assert.False(panel.HasNoSlots);
        Assert.False(panel.ShowMeadowSection);
    }

    [Fact]
    public void A_library_save_from_a_local_slot_is_on_screen_either_way_too()
    {
        using var root = new TempDirectory("realm");
        var panel = Panels.Entry(root, Panels.Slot(1));

        Assert.Single(panel.Slots);

        panel.ShowOnline = true;

        Assert.Single(panel.Slots);
    }

    [Fact]
    public void A_section_header_says_which_realm_it_is()
    {
        // sav2 and online_sav2 share a slot number, so the number on its own does not say which of
        // the two the section is showing.
        var panel = Panels.Live(Panels.Slot(2), Panels.Slot(2, SaveRealm.Online));

        Assert.Equal("SLOT 2", Assert.Single(panel.Slots).HeaderText);

        panel.ShowOnline = true;

        Assert.Equal("ONLINE SLOT 2", Assert.Single(panel.Slots).HeaderText);
    }

    [Fact]
    public void A_library_save_header_leaves_the_realm_out()
    {
        // One section, and the file it came from is named beside the title, so a realm in the
        // header here would only repeat it.
        using var root = new TempDirectory("realm");
        var panel = Panels.Entry(root, Panels.Slot(2, SaveRealm.Online));

        Assert.Equal("SLOT 2", Assert.Single(panel.Slots).HeaderText);
    }
}
