using RainWorldCompanion.Core.Mods;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.App.Tests;

/// <summary>
/// The wording is most of what this feature is, because an empty list has four different
/// meanings and only one of them is "no mods".
/// </summary>
public class ModListPanelTests
{
    private static ModEntry Mod(string id, string? version = null, string? workshopId = null, string? name = null)
        => new()
        {
            Id = id,
            Name = name ?? id,
            Version = version,
            WorkshopId = workshopId,
            Origin = workshopId is null ? ModEntry.InstallOrigin : ModEntry.WorkshopOrigin,
        };

    private static ModListSnapshot Snapshot(string? gameVersion, params ModEntry[] mods)
        => new()
        {
            ReadTheEnabledList = true,
            CheckedTheInstall = true,
            CheckedTheWorkshop = true,
            GameVersion = gameVersion,
            Mods = mods.ToList(),
        };

    private static CurrentMods Current(string? gameVersion, params ModEntry[] mods)
    {
        ModListSnapshot enabled = Snapshot(gameVersion, mods);
        return new CurrentMods(enabled, enabled.Mods.ToList());
    }

    [Fact]
    public void No_comparison_at_all_draws_no_section()
    {
        var section = new ModListDiffViewModel(null);

        Assert.False(section.ShowSection);
        Assert.False(section.HasRows);
    }

    [Fact]
    public void A_machine_that_has_not_moved_says_so_with_a_count()
    {
        ModListDiff diff = ModListDiff.Compare(
            Snapshot("v1.11.8", Mod("a", "1.0"), Mod("b", "2.0")),
            Current("v1.11.8", Mod("a", "1.0"), Mod("b", "2.0")));

        var section = new ModListDiffViewModel(diff);

        Assert.True(section.ShowSection);
        Assert.False(section.HasRows);
        Assert.Contains("2 mods on now", section.HeadlineText);
    }

    [Fact]
    public void A_backup_from_before_mod_lists_says_it_predates_them()
    {
        var section = new ModListDiffViewModel(
            ModListDiff.Compare(null, Current("v1.11.8")), fromABackup: true);

        Assert.Contains("before this app recorded mod lists", section.HeadlineText);
        Assert.False(section.HasRows);
    }

    [Fact]
    public void A_library_save_from_before_mod_lists_is_worded_for_a_save()
    {
        var section = new ModListDiffViewModel(
            ModListDiff.Compare(null, Current("v1.11.8")), fromABackup: false);

        Assert.Contains("No mod list was recorded when this save was stored", section.HeadlineText);
    }

    [Fact]
    public void A_difference_says_it_still_loads()
    {
        ModListDiff diff = ModListDiff.Compare(Snapshot(null, Mod("gone")), Current(null));

        var section = new ModListDiffViewModel(diff);

        Assert.Contains("still load", section.HeadlineText);
        Assert.True(section.HasRows);
    }

    /// <summary>
    /// A workshop mod gets a page to open. A local mod gets a sentence instead, because there is
    /// nowhere to send anyone and a button that did nothing would be worse than none.
    /// </summary>
    [Fact]
    public void A_missing_workshop_mod_gets_a_page_and_a_local_one_gets_a_sentence()
    {
        ModListDiff diff = ModListDiff.Compare(
            Snapshot(null, Mod("fromsteam", workshopId: "2923374705", name: "From Steam"), Mod("local")),
            Current(null));

        var section = new ModListDiffViewModel(diff);

        ModDiffRowViewModel steam = section.Missing.Single(row => row.Name == "From Steam");
        Assert.True(steam.HasWorkshopPage);
        Assert.Equal(
            ModListDiffViewModel.WorkshopUrlPrefix + "2923374705",
            steam.WorkshopUrl);

        ModDiffRowViewModel local = section.Missing.Single(row => row.Name == "local");
        Assert.False(local.HasWorkshopPage);
        Assert.Contains("local mod", local.ActionText);
    }

    [Fact]
    public void A_mod_that_is_only_turned_off_is_pointed_at_the_mods_window()
    {
        var now = new CurrentMods(Snapshot(null), new[] { Mod("off") });
        ModListDiff diff = ModListDiff.Compare(Snapshot(null, Mod("off")), now);

        ModDiffRowViewModel row = Assert.Single(new ModListDiffViewModel(diff).TurnedOff);

        Assert.Contains("Mods window", row.ActionText);
        Assert.Contains("turned off", row.DetailText);
    }

    [Fact]
    public void The_button_that_opens_the_mods_window_appears_only_when_there_is_one_to_open()
    {
        var now = new CurrentMods(Snapshot(null), new[] { Mod("off") });
        ModListDiff diff = ModListDiff.Compare(Snapshot(null, Mod("off")), now);

        Assert.False(new ModListDiffViewModel(diff).CanFixMods);
        Assert.True(new ModListDiffViewModel(diff) { FixMods = () => null }.CanFixMods);
    }

    [Fact]
    public void Nothing_to_sort_out_means_no_button_even_when_one_could_be_opened()
    {
        ModListDiff diff = ModListDiff.Compare(Snapshot(null, Mod("a")), Current(null, Mod("a")));

        Assert.False(new ModListDiffViewModel(diff) { FixMods = () => null }.CanFixMods);
    }

    [Fact]
    public void A_version_change_names_both_versions()
    {
        ModListDiff diff = ModListDiff.Compare(
            Snapshot(null, Mod("a", "1.0")), Current(null, Mod("a", "2.0")));

        ModDiffRowViewModel row = Assert.Single(new ModListDiffViewModel(diff).Changed);

        Assert.Contains("1.0", row.DetailText);
        Assert.Contains("2.0", row.DetailText);
    }

    [Fact]
    public void Every_header_carries_its_count()
    {
        ModListDiff diff = ModListDiff.Compare(
            Snapshot(null, Mod("gone"), Mod("moved", "1.0")),
            Current(null, Mod("moved", "2.0"), Mod("new")));

        var section = new ModListDiffViewModel(diff);

        Assert.Equal("Not installed now (1)", section.MissingHeader);
        Assert.Equal("At a different version (1)", section.ChangedHeader);
        Assert.Equal("On now, but not recorded (1)", section.ExtraHeader);
    }

    [Fact]
    public void A_game_version_that_has_moved_is_its_own_note()
    {
        ModListDiff diff = ModListDiff.Compare(Snapshot("v1.10.4"), Current("v1.11.8"));

        var section = new ModListDiffViewModel(diff);

        Assert.Contains(section.GroupNotes, note => note.Contains("v1.10.4") && note.Contains("v1.11.8"));
    }

    [Fact]
    public void The_live_panel_lists_the_mods_with_versions_and_where_they_came_from()
    {
        ModListSectionViewModel section = ModListSectionViewModel.ForCurrent(
            Current("v1.11.8", Mod("a", "1.0"), Mod("b", "2.3.3", workshopId: "2923374705")));

        Assert.Equal("2 mods on", section.CountText);
        Assert.Equal("game v1.11.8", section.GameVersionText);
        Assert.Equal("local mod", section.Rows[0].OriginText);
        Assert.Equal("workshop 2923374705", section.Rows[1].OriginText);
        Assert.Equal("1.0", section.Rows[0].VersionText);
        Assert.False(section.HasEmptyText);
    }

    /// <summary>A vanilla install is a real answer and reads as one.</summary>
    [Fact]
    public void A_machine_with_no_mods_on_says_none_are_on()
    {
        ModListSectionViewModel section = ModListSectionViewModel.ForCurrent(Current("v1.11.8"));

        Assert.Equal("No mods on", section.CountText);
        Assert.False(section.HasRows);
        Assert.False(section.HasEmptyText);
    }

    /// <summary>
    /// The case this section exists for: not knowing must not be drawn the same way as knowing
    /// there is nothing.
    /// </summary>
    [Fact]
    public void A_machine_that_could_not_be_read_says_that_rather_than_no_mods()
    {
        ModListSectionViewModel section = ModListSectionViewModel.ForCurrent(
            CurrentMods.NothingRead("The save folder holds no options file."));

        Assert.Equal("", section.CountText);
        Assert.Contains("no options file", section.EmptyText);
        Assert.False(section.HasRows);
    }

    [Fact]
    public void Without_the_game_folder_the_rows_are_ids_and_the_note_says_why()
    {
        var enabled = new ModListSnapshot
        {
            ReadTheEnabledList = true,
            CheckedTheInstall = false,
            Mods = new List<ModEntry> { new() { Id = "some.mod", Name = "some.mod" } },
        };

        ModListSectionViewModel section = ModListSectionViewModel.ForCurrent(
            new CurrentMods(enabled, Array.Empty<ModEntry>()));

        Assert.Equal("some.mod", Assert.Single(section.Rows).Name);
        Assert.Equal("", section.Rows[0].VersionText);
        Assert.Equal("", section.Rows[0].OriginText);
    }

    [Fact]
    public void A_backup_that_recorded_no_mods_says_it_predates_them()
    {
        ModListSectionViewModel section = ModListSectionViewModel.ForRecorded(null, fromABackup: true);

        Assert.Contains("before this app recorded mod lists", section.EmptyText);
        Assert.False(section.HasRows);
    }

    [Fact]
    public void A_library_save_that_recorded_no_mods_is_worded_for_a_save()
    {
        ModListSectionViewModel section = ModListSectionViewModel.ForRecorded(null, fromABackup: false);

        Assert.Contains("No mod list was recorded", section.EmptyText);
    }

    [Fact]
    public void A_recorded_list_reads_in_the_past_tense()
    {
        ModListSectionViewModel section = ModListSectionViewModel.ForRecorded(
            Snapshot("v1.9.15", Mod("a", "1.0")), fromABackup: true);

        Assert.Equal("1 mod on", section.CountText);
        Assert.Equal("game v1.9.15", section.GameVersionText);
    }

    [Fact]
    public void A_panel_built_before_anything_was_read_draws_no_lines()
    {
        ModListSectionViewModel section = ModListSectionViewModel.ForCurrent(null);

        Assert.False(section.HasRows);
        Assert.False(section.HasEmptyText);
        Assert.False(section.HasCount);
        Assert.False(section.HasGameVersion);
    }

    [Fact]
    public void The_live_panel_carries_the_mods_it_was_built_with()
    {
        SnapshotDetailViewModel panel = SnapshotDetailViewModel.ForLive(
            new[] { Panels.Slot(1) },
            @"C:saves",
            1024,
            1,
            null,
            new FakeIcons(),
            Current("v1.11.8", Mod("a", "1.0")));

        Assert.Equal("1 mod on", panel.Mods.CountText);
    }

    /// <summary>
    /// The manifests these panels read are the ones already on disk, none of which carry a mod
    /// list, so this is the wording every existing backup and library save will show.
    /// </summary>
    [Fact]
    public void A_backup_and_a_library_panel_word_a_missing_record_for_what_they_are()
    {
        using var root = new TempDirectory("modpanel");

        SnapshotDetailViewModel backup = Panels.Backup(root, Panels.Slot(1));
        SnapshotDetailViewModel entry = Panels.Entry(root, Panels.Slot(1));

        Assert.Contains("This backup was taken before", backup.Mods.EmptyText);
        Assert.Contains("No mod list was recorded when this save was stored", entry.Mods.EmptyText);
    }

    [Fact]
    public void A_difference_has_to_be_acknowledged_before_the_save_can_be_written()
    {
        var now = new CurrentMods(Snapshot(null), new[] { Mod("off") });
        var view = new ModListDiffViewModel(ModListDiff.Compare(Snapshot(null, Mod("off")), now));

        Assert.True(view.NeedsAcknowledgement);
        Assert.False(view.Settled);

        view.Acknowledged = true;

        Assert.True(view.Settled);
    }

    [Fact]
    public void A_machine_that_matches_needs_no_acknowledgement()
    {
        var view = new ModListDiffViewModel(ModListDiff.Compare(Snapshot(null, Mod("a")), Current(null, Mod("a"))));

        Assert.False(view.NeedsAcknowledgement);
        Assert.True(view.Settled);
    }

    [Fact]
    public void Turning_the_mod_on_and_reloading_drops_the_acknowledgement_and_the_button()
    {
        var now = new CurrentMods(Snapshot(null), new[] { Mod("off") });
        var view = new ModListDiffViewModel(ModListDiff.Compare(Snapshot(null, Mod("off")), now))
        {
            FixMods = () => null,
        };

        Assert.True(view.CanFixMods);

        view.Reload(ModListDiff.Compare(Snapshot(null, Mod("off")), Current(null, Mod("off"))));

        Assert.False(view.NeedsAcknowledgement);
        Assert.True(view.Settled);
        Assert.False(view.CanFixMods);
    }
}
