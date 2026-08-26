using RainWorldCompanion.Core.Mods;

namespace RainWorldCompanion.Tests;

/// <summary>
/// Comparing a recorded mod list against this machine. Nothing here refuses anything, it only
/// describes the difference.
/// </summary>
public class ModListDiffTests
{
    [Fact]
    public void An_unchanged_machine_matches()
    {
        ModListSnapshot recorded = ModLists.Snapshot("v1.11.8", ModLists.Mod("a", "1.0"), ModLists.Mod("b", "2.0"));
        CurrentMods now = ModLists.Current("v1.11.8", ModLists.Mod("a", "1.0"), ModLists.Mod("b", "2.0"));

        ModListDiff diff = ModListDiff.Compare(recorded, now);

        Assert.True(diff.Matches);
        Assert.True(diff.ListsMatch);
        Assert.Equal(2, diff.RecordedCount);
        Assert.Equal(2, diff.CurrentCount);
        Assert.Empty(diff.Notes);
    }

    /// <summary>
    /// Missing keeps the recorded entry whole rather than reducing it to an id, because its name
    /// and workshop id feed a "go and get it" action.
    /// </summary>
    [Fact]
    public void A_mod_that_is_gone_is_missing_and_keeps_what_was_recorded_about_it()
    {
        ModListSnapshot recorded = ModLists.Snapshot(
            null, ModLists.Mod("MapOptions", "2.3.3", workshopId: "2923374705", name: "Map Options"));

        ModListDiff diff = ModListDiff.Compare(recorded, ModLists.Current(null));

        ModEntry gone = Assert.Single(diff.Missing);
        Assert.Equal("Map Options", gone.Name);
        Assert.Equal("2923374705", gone.WorkshopId);
        Assert.False(diff.ListsMatch);
    }

    /// <summary>Installed but off is a different problem with a different fix, so it is its own list.</summary>
    [Fact]
    public void A_mod_that_is_installed_but_off_is_told_apart_from_one_that_is_gone()
    {
        ModListSnapshot recorded = ModLists.Snapshot(null, ModLists.Mod("off"), ModLists.Mod("gone"));
        var now = new CurrentMods(ModLists.Snapshot(null), new[] { ModLists.Mod("off") });

        ModListDiff diff = ModListDiff.Compare(recorded, now);

        Assert.Equal("off", Assert.Single(diff.TurnedOff).Id);
        Assert.Equal("gone", Assert.Single(diff.Missing).Id);
    }

    [Fact]
    public void A_mod_at_a_different_version_is_a_change()
    {
        ModListSnapshot recorded = ModLists.Snapshot(null, ModLists.Mod("a", "1.0"));

        ModVersionChange change = Assert.Single(
            ModListDiff.Compare(recorded, ModLists.Current(null, ModLists.Mod("a", "2.0"))).Changed);

        Assert.Equal("1.0", change.Recorded);
        Assert.Equal("2.0", change.Now);
    }

    [Fact]
    public void A_mod_that_is_on_now_but_was_not_recorded_is_extra()
    {
        ModListSnapshot recorded = ModLists.Snapshot(null, ModLists.Mod("a"));

        ModListDiff diff = ModListDiff.Compare(
            recorded, ModLists.Current(null, ModLists.Mod("a"), ModLists.Mod("new")));

        Assert.Equal("new", Assert.Single(diff.Extra).Id);
        Assert.Empty(diff.Missing);
    }

    /// <summary>
    /// devtools ships without a version. Calling that a change would invent a difference out of
    /// something nobody ever knew.
    /// </summary>
    [Fact]
    public void A_missing_version_on_either_side_is_not_a_change()
    {
        ModListSnapshot noneRecorded = ModLists.Snapshot(null, ModLists.Mod("a"));
        ModListSnapshot recorded = ModLists.Snapshot(null, ModLists.Mod("a", "1.0"));

        Assert.Empty(ModListDiff.Compare(noneRecorded, ModLists.Current(null, ModLists.Mod("a", "1.0"))).Changed);
        Assert.Empty(ModListDiff.Compare(recorded, ModLists.Current(null, ModLists.Mod("a"))).Changed);
    }

    [Fact]
    public void Whitespace_and_case_around_a_version_are_not_a_change()
    {
        ModListSnapshot recorded = ModLists.Snapshot(null, ModLists.Mod("a", "1.0-EA"));

        Assert.Empty(ModListDiff.Compare(recorded, ModLists.Current(null, ModLists.Mod("a", " 1.0-ea "))).Changed);
    }

    /// <summary>Mod ids are matched the way the game matches them, which is without case.</summary>
    [Fact]
    public void A_mod_id_is_matched_without_case()
    {
        ModListSnapshot recorded = ModLists.Snapshot(null, ModLists.Mod("MapOptions", "1.0"));

        ModListDiff diff = ModListDiff.Compare(recorded, ModLists.Current(null, ModLists.Mod("mapoptions", "1.0")));

        Assert.True(diff.ListsMatch);
    }

    [Fact]
    public void The_game_version_is_only_a_difference_when_both_are_known()
    {
        ModListSnapshot under1104 = ModLists.Snapshot("v1.10.4");

        Assert.True(ModListDiff.Compare(under1104, ModLists.Current("v1.11.8")).GameVersionDiffers);
        Assert.False(ModListDiff.Compare(under1104, ModLists.Current("v1.10.4")).GameVersionDiffers);
        Assert.False(ModListDiff.Compare(under1104, ModLists.Current(null)).GameVersionDiffers);
        Assert.False(ModListDiff.Compare(ModLists.Snapshot(null), ModLists.Current("v1.11.8")).GameVersionDiffers);
    }

    /// <summary>The lists can match while the game underneath them has moved.</summary>
    [Fact]
    public void A_matching_list_under_a_different_game_is_not_a_match()
    {
        ModListDiff diff = ModListDiff.Compare(
            ModLists.Snapshot("v1.10.4", ModLists.Mod("a", "1.0")),
            ModLists.Current("v1.11.8", ModLists.Mod("a", "1.0")));

        Assert.True(diff.ListsMatch);
        Assert.False(diff.Matches);
    }

    [Fact]
    public void A_snapshot_with_no_recording_compares_nothing_and_still_says_what_is_on_now()
    {
        ModListDiff diff = ModListDiff.Compare(null, ModLists.Current("v1.11.8", ModLists.Mod("a")));

        Assert.True(diff.NothingWasRecorded);
        Assert.False(diff.Compared);
        Assert.False(diff.Matches);
        Assert.Empty(diff.Missing);
        Assert.Empty(diff.Extra);
        Assert.Equal("v1.11.8", diff.CurrentGameVersion);
        Assert.Equal(1, diff.CurrentCount);
    }

    /// <summary>
    /// A recording that could not be read is not the same as one that read zero mods, so this
    /// must not report every current mod as removed.
    /// </summary>
    [Fact]
    public void A_recording_that_could_not_look_compares_nothing()
    {
        var couldNotLook = new ModListSnapshot { ReadTheEnabledList = false };

        ModListDiff diff = ModListDiff.Compare(couldNotLook, ModLists.Current(null, ModLists.Mod("a"), ModLists.Mod("b")));

        Assert.True(diff.RecordedCouldNotLook);
        Assert.False(diff.NothingWasRecorded);
        Assert.Empty(diff.Extra);
        Assert.False(diff.Matches);
    }

    [Fact]
    public void A_machine_that_cannot_be_read_now_compares_nothing()
    {
        ModListDiff diff = ModListDiff.Compare(
            ModLists.Snapshot(null, ModLists.Mod("a")), ModLists.CouldNotLook());

        Assert.True(diff.CurrentCouldNotLook);
        Assert.Empty(diff.Missing);
        Assert.False(diff.Matches);
        Assert.Equal(1, diff.RecordedCount);
    }

    /// <summary>
    /// Without the install nothing can tell "gone" from merely "off", so this lands in Missing
    /// (the harder-sounding list) and adds a note rather than guessing.
    /// </summary>
    [Fact]
    public void Without_the_install_a_mod_that_is_off_cannot_be_told_from_one_that_is_gone()
    {
        ModListSnapshot recorded = ModLists.Snapshot(null, ModLists.Mod("a"));
        var now = new CurrentMods(
            new ModListSnapshot { ReadTheEnabledList = true, CheckedTheInstall = false },
            new[] { ModLists.Mod("a") });

        ModListDiff diff = ModListDiff.Compare(recorded, now);

        Assert.Single(diff.Missing);
        Assert.Empty(diff.TurnedOff);
        Assert.Contains(diff.Notes, note => note.Contains("turned off"));
    }

    [Fact]
    public void A_recording_taken_without_the_install_says_its_versions_are_unknown()
    {
        var recorded = new ModListSnapshot
        {
            ReadTheEnabledList = true,
            CheckedTheInstall = false,
            Mods = new List<ModEntry> { ModLists.Mod("a") },
        };

        ModListDiff diff = ModListDiff.Compare(recorded, ModLists.Current(null, ModLists.Mod("a", "1.0")));

        Assert.True(diff.ListsMatch);
        Assert.Contains(diff.Notes, note => note.Contains("No versions were recorded"));
    }

    /// <summary>Two openings of the same dialog have to read the same way.</summary>
    [Fact]
    public void Every_list_is_ordered_by_name_then_id()
    {
        ModListSnapshot recorded = ModLists.Snapshot(
            null,
            ModLists.Mod("z", name: "Alpha"),
            ModLists.Mod("a", name: "Zulu"),
            ModLists.Mod("m", name: "Alpha"));

        ModListDiff diff = ModListDiff.Compare(recorded, ModLists.Current(null));

        Assert.Equal(new[] { "m", "z", "a" }, diff.Missing.Select(mod => mod.Id));
    }
}
