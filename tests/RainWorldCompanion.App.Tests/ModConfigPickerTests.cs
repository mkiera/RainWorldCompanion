using RainWorldCompanion.Core.Library;
using RainWorldCompanion.Core.Mods;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.App.Tests;

/// <summary>
/// The picker that decides whose mod settings a load takes. Every tick starts clear, and the
/// wording is most of the rest: a row has to say what taking it would replace and what it would
/// bring across besides the tuning.
/// </summary>
public class ModConfigPickerTests
{
    private const string Devourment = "devourment";

    private static ModConfigFile File(string relativePath, string modId, long sizeBytes = 64)
        => new() { RelativePath = relativePath, ModId = modId, SizeBytes = sizeBytes, Sha256 = new string('a', 64) };

    private static ModConfigSet Set(params ModConfigFile[] files)
        => new() { ReadTheFolder = true, Files = files.ToList() };

    private static ModEntry Mod(string id, string? version = null, string? name = null)
        => new() { Id = id, Name = name ?? id, Version = version, Origin = ModEntry.InstallOrigin };

    private static ModListSnapshot Snapshot(params ModEntry[] mods)
        => new()
        {
            ReadTheEnabledList = true,
            CheckedTheInstall = true,
            CheckedTheWorkshop = true,
            Mods = mods.ToList(),
        };

    private static CurrentMods Installed(params ModEntry[] mods)
        => new(Snapshot(mods), mods.ToList());

    /// <summary>One mod's settings, recorded with a save that knew what that mod was.</summary>
    private static ModConfigOffer Offer(
        ModConfigSet? recorded = null,
        ModListSnapshot? recordedMods = null,
        ModConfigSet? live = null,
        CurrentMods? current = null)
        => new(
            recorded ?? Set(File(@"ModConfigs\devourment.txt", Devourment)),
            recordedMods ?? Snapshot(Mod(Devourment, "0.1.11-ea", "Devourment")),
            live,
            current);

    private static ModConfigRowViewModel Row(ModConfigPickerViewModel picker, string modId)
        => picker.Rows.Single(row => row.ModId == modId);

    // ---- the decision behind the feature ----

    /// <summary>
    /// Somebody else's settings are not what a player asked for by asking to load a save. This must
    /// never quietly change.
    /// </summary>
    [Fact]
    public void Every_row_starts_unticked()
    {
        var picker = new ModConfigPickerViewModel(Offer(
            Set(File(@"ModConfigs\devourment.txt", Devourment), File(@"ModConfigs\other.txt", "other"))));

        Assert.Equal(2, picker.Rows.Count);
        Assert.All(picker.Rows, row => Assert.False(row.Take));
    }

    [Fact]
    public void Nothing_ticked_chooses_nothing()
    {
        Assert.Empty(new ModConfigPickerViewModel(Offer()).Chosen);
    }

    [Fact]
    public void Ticking_a_row_chooses_that_mod()
    {
        var picker = new ModConfigPickerViewModel(Offer(
            Set(File(@"ModConfigs\devourment.txt", Devourment), File(@"ModConfigs\other.txt", "other"))));

        Row(picker, Devourment).Take = true;

        Assert.Equal(new[] { Devourment }, picker.Chosen);
    }

    // ---- taking every mod at once ----

    /// <summary>The one control that turns ticks on in bulk still starts clear, like the rows.</summary>
    [Fact]
    public void Take_all_starts_off()
    {
        var picker = new ModConfigPickerViewModel(Offer(
            Set(File(@"ModConfigs\devourment.txt", Devourment), File(@"ModConfigs\other.txt", "other"))));

        Assert.False(picker.TakeAll);
        Assert.Empty(picker.Chosen);
    }

    [Fact]
    public void Taking_all_ticks_every_row()
    {
        var picker = new ModConfigPickerViewModel(Offer(
            Set(File(@"ModConfigs\devourment.txt", Devourment), File(@"ModConfigs\other.txt", "other"))));

        picker.TakeAll = true;

        Assert.All(picker.Rows, row => Assert.True(row.Take));
        Assert.Equal(new[] { Devourment, "other" }, picker.Chosen);
    }

    [Fact]
    public void Clearing_it_unticks_every_row()
    {
        var picker = new ModConfigPickerViewModel(Offer(
            Set(File(@"ModConfigs\devourment.txt", Devourment), File(@"ModConfigs\other.txt", "other"))));

        picker.TakeAll = true;
        picker.TakeAll = false;

        Assert.Empty(picker.Chosen);
    }

    /// <summary>Neither on nor off, which is the indeterminate a three state box draws.</summary>
    [Fact]
    public void Some_but_not_all_reads_as_neither()
    {
        var picker = new ModConfigPickerViewModel(Offer(
            Set(File(@"ModConfigs\devourment.txt", Devourment), File(@"ModConfigs\other.txt", "other"))));

        Row(picker, Devourment).Take = true;

        Assert.Null(picker.TakeAll);
    }

    [Fact]
    public void Ticking_the_last_row_by_hand_turns_it_on()
    {
        var picker = new ModConfigPickerViewModel(Offer(
            Set(File(@"ModConfigs\devourment.txt", Devourment), File(@"ModConfigs\other.txt", "other"))));

        foreach (ModConfigRowViewModel row in picker.Rows)
        {
            row.Take = true;
        }

        Assert.True(picker.TakeAll);
    }

    /// <summary>
    /// Indeterminate is a state the box reports, not one to apply: there is no sensible set of rows
    /// to leave behind for it, so setting it changes nothing.
    /// </summary>
    [Fact]
    public void Setting_it_to_neither_changes_no_row()
    {
        var picker = new ModConfigPickerViewModel(Offer(
            Set(File(@"ModConfigs\devourment.txt", Devourment), File(@"ModConfigs\other.txt", "other"))));

        Row(picker, Devourment).Take = true;
        picker.TakeAll = null;

        Assert.Equal(new[] { Devourment }, picker.Chosen);
    }

    /// <summary>
    /// A two state box answers an indeterminate reading by writing back the opposite. Announcing
    /// the sweep row by row would report exactly that half way through, and the write back would
    /// land here and undo the sweep still running, leaving the box ticked over clear rows.
    /// </summary>
    [Fact]
    public void Taking_all_never_reports_indeterminate_part_way_through()
    {
        var picker = new ModConfigPickerViewModel(Offer(Set(
            File(@"ModConfigs.txt", "a"), File(@"ModConfigs.txt", "b"), File(@"ModConfigs\c.txt", "c"))));

        var seen = new List<bool?>();
        picker.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ModConfigPickerViewModel.TakeAll))
            {
                seen.Add(picker.TakeAll);
            }
        };

        picker.TakeAll = true;

        Assert.Equal(new bool?[] { true }, seen);
        Assert.Equal(3, picker.Chosen.Count);
    }

    /// <summary>What the box does when the write back arrives anyway: nothing.</summary>
    [Fact]
    public void A_write_back_arriving_during_the_sweep_does_not_undo_it()
    {
        var picker = new ModConfigPickerViewModel(Offer(Set(
            File(@"ModConfigs.txt", "a"), File(@"ModConfigs.txt", "b"))));

        picker.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ModConfigPickerViewModel.TakeAll))
            {
                picker.TakeAll = false;
            }
        };

        picker.TakeAll = true;

        Assert.Equal(2, picker.Chosen.Count);
    }

    /// <summary>One row is its own select all, so the control would be noise beside it.</summary>
    [Fact]
    public void One_row_is_offered_no_take_all()
    {
        Assert.False(new ModConfigPickerViewModel(Offer()).HasSeveralRows);
    }

    [Fact]
    public void Several_rows_are()
    {
        var picker = new ModConfigPickerViewModel(Offer(
            Set(File(@"ModConfigs\devourment.txt", Devourment), File(@"ModConfigs\other.txt", "other"))));

        Assert.True(picker.HasSeveralRows);
    }

    /// <summary>A reloaded picker must not still be answering for the rows it threw away.</summary>
    [Fact]
    public void Take_all_follows_the_rows_a_reload_left_behind()
    {
        var picker = new ModConfigPickerViewModel(Offer(
            Set(File(@"ModConfigs\devourment.txt", Devourment), File(@"ModConfigs\other.txt", "other"))));

        picker.TakeAll = true;
        picker.Reload(Offer(Set(File(@"ModConfigs\devourment.txt", Devourment))));

        Assert.True(picker.TakeAll);
        Assert.Equal(new[] { Devourment }, picker.Chosen);
    }

    // ---- a row is a mod, not a file ----

    /// <summary>Devourment owns both its settings file and its whole preset folder, and ticking
    /// Devourment means both.</summary>
    [Fact]
    public void A_mod_that_owns_several_files_is_one_row()
    {
        var picker = new ModConfigPickerViewModel(Offer(Set(
            File(@"ModConfigs\devourment.txt", Devourment, 2048),
            File(@"ModConfigs\DvrmentConfs\current.json", Devourment, 1024))));

        ModConfigRowViewModel row = Assert.Single(picker.Rows);
        Assert.Contains("2 files", row.DetailText);
        Assert.Contains("3.0 KB", row.DetailText);
    }

    [Fact]
    public void A_row_is_named_by_the_mod_and_its_version_where_the_save_recorded_them()
    {
        var picker = new ModConfigPickerViewModel(Offer());

        Assert.Equal("Devourment  0.1.11-ea", Assert.Single(picker.Rows).Name);
    }

    /// <summary>
    /// A settings file sits in ModConfigs whether its mod is on or not, so a save can carry
    /// settings for a mod its own list never named. The row says so rather than dropping the file.
    /// </summary>
    [Fact]
    public void A_mod_the_recorded_list_never_named_is_still_offered_and_said_to_be_missing_from_it()
    {
        var picker = new ModConfigPickerViewModel(Offer(
            Set(File(@"ModConfigs\stranger.txt", "stranger")),
            recordedMods: Snapshot(Mod(Devourment))));

        ModConfigRowViewModel row = Assert.Single(picker.Rows);
        Assert.Equal("stranger", row.Name);
        Assert.Contains(row.Notes, note => note.Contains("not in the list recorded"));
    }

    /// <summary>Said once in the headline rather than on every row, where it would be the same
    /// sentence repeated.</summary>
    [Fact]
    public void A_save_with_no_recorded_mod_list_says_so_once_and_not_on_each_row()
    {
        var picker = new ModConfigPickerViewModel(new ModConfigOffer(
            Set(File(@"ModConfigs\devourment.txt", Devourment), File(@"ModConfigs\other.txt", "other")),
            RecordedMods: null,
            Live: null,
            Current: null));

        Assert.Contains("No mod list was recorded", picker.HeadlineText);
        Assert.All(picker.Rows, row => Assert.DoesNotContain(
            row.Notes, note => note.Contains("not in the list recorded")));
    }

    // ---- what a row warns about ----

    [Fact]
    public void A_mod_you_already_have_settings_for_says_they_are_replaced()
    {
        var picker = new ModConfigPickerViewModel(Offer(
            live: Set(File(@"ModConfigs\devourment.txt", Devourment))));

        Assert.Contains(Assert.Single(picker.Rows).Notes, note => note.Contains("Replaces the settings you have"));
    }

    [Fact]
    public void A_mod_you_have_no_settings_for_says_nothing_about_replacing()
    {
        var picker = new ModConfigPickerViewModel(Offer(live: Set()));

        Assert.DoesNotContain(Assert.Single(picker.Rows).Notes, note => note.Contains("Replaces"));
    }

    [Fact]
    public void A_mod_that_is_not_installed_here_says_so()
    {
        var picker = new ModConfigPickerViewModel(Offer(current: Installed(Mod("somethingelse"))));

        Assert.Contains(Assert.Single(picker.Rows).Notes, note => note.Contains("not installed here"));
    }

    /// <summary>
    /// Following the rule the mods panel follows: an install nobody could read is not a mod nobody
    /// has, and saying otherwise would be a claim about the user's machine that nothing checked.
    /// </summary>
    [Fact]
    public void An_install_that_was_never_looked_at_makes_no_claim_about_what_is_installed()
    {
        var current = new CurrentMods(
            new ModListSnapshot { ReadTheEnabledList = true, CheckedTheInstall = false },
            Array.Empty<ModEntry>());

        var picker = new ModConfigPickerViewModel(Offer(current: current));

        Assert.DoesNotContain(Assert.Single(picker.Rows).Notes, note => note.Contains("not installed"));
    }

    /// <summary>
    /// The trap: a camera mod keeps a window size in the same file as its gameplay options, and
    /// taking that from somebody else pushes their screen onto yours.
    /// </summary>
    [Fact]
    public void A_settings_file_holding_a_screen_size_names_the_keys_it_would_bring_across()
    {
        var offer = Offer(Set(File(@"ModConfigs\SBCameraScroll.txt", "SBCameraScroll"))) with
        {
            MachineSpecific = new Dictionary<string, IReadOnlyList<string>>
            {
                [@"ModConfigs\SBCameraScroll.txt"] = new[] { "customResolution", "resolution" },
            },
        };

        ModConfigRowViewModel row = Assert.Single(new ModConfigPickerViewModel(offer).Rows);

        Assert.Contains(row.Notes, note => note.Contains("customResolution, resolution"));
    }

    [Fact]
    public void An_ordinary_settings_file_carries_no_notes_at_all()
    {
        Assert.False(Assert.Single(new ModConfigPickerViewModel(Offer()).Rows).HasNotes);
    }

    // ---- when there is nothing to pick ----

    [Fact]
    public void A_save_carrying_no_settings_hides_the_section()
    {
        var picker = new ModConfigPickerViewModel(null);

        Assert.False(picker.ShowSection);
        Assert.Empty(picker.Rows);
        Assert.Empty(picker.Chosen);
    }

    [Fact]
    public void A_save_carrying_settings_shows_it()
    {
        Assert.True(new ModConfigPickerViewModel(Offer()).ShowSection);
    }

    // ---- reloading after the Mods window ----

    /// <summary>
    /// The Mods window opens over the same dialog and can turn a mod on. What a row says about that
    /// mod changes, but what the user asked for does not.
    /// </summary>
    [Fact]
    public void Reloading_keeps_what_was_ticked()
    {
        var picker = new ModConfigPickerViewModel(Offer(
            Set(File(@"ModConfigs\devourment.txt", Devourment), File(@"ModConfigs\other.txt", "other")),
            current: Installed(Mod("other"))));

        Row(picker, Devourment).Take = true;
        Assert.Contains(Row(picker, Devourment).Notes, note => note.Contains("not installed here"));

        picker.Reload(Offer(
            Set(File(@"ModConfigs\devourment.txt", Devourment), File(@"ModConfigs\other.txt", "other")),
            current: Installed(Mod(Devourment), Mod("other"))));

        Assert.Equal(new[] { Devourment }, picker.Chosen);
        Assert.DoesNotContain(Row(picker, Devourment).Notes, note => note.Contains("not installed here"));
    }

    [Fact]
    public void Reloading_onto_nothing_leaves_no_rows_and_chooses_nothing()
    {
        var picker = new ModConfigPickerViewModel(Offer());
        Row(picker, Devourment).Take = true;

        picker.Reload(null);

        Assert.Empty(picker.Chosen);
        Assert.False(picker.ShowSection);
    }

    /// <summary>A mod that is no longer offered cannot stay ticked, or a load would be asked for
    /// settings that are not there.</summary>
    [Fact]
    public void Reloading_drops_a_tick_for_a_mod_that_is_no_longer_offered()
    {
        var picker = new ModConfigPickerViewModel(Offer());
        Row(picker, Devourment).Take = true;

        picker.Reload(Offer(Set(File(@"ModConfigs\other.txt", "other"))));

        Assert.Empty(picker.Chosen);
    }
}
