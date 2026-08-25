using System.IO;
using RainWorldCompanion.Core.Editing;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.Core.Saves.Models;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.App.Tests;

/// <summary>
/// The editing panel, driven the way the window drives it, against a real save.
///
/// The rule these hold it to: an edit is written the moment it is made, and nothing is refused. A
/// value the game would find strange produces a line of advice and is written anyway, so every
/// test that checks a warning also checks the value went through.
/// </summary>
public class CampaignEditTests : IDisposable
{
    private readonly TempDirectory _directory = new("edit");
    private readonly SaveEditSession _session;

    public CampaignEditTests()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sav2.bin");
        var path = Path.Combine(_directory.Path, "sav2");
        File.Copy(fixture, path);
        _session = SaveEditSession.Open(path);
    }

    public void Dispose() => _directory.Dispose();

    private CampaignEditViewModel Editor()
    {
        var campaign = _session.Campaigns[0];
        return new CampaignEditViewModel(_session, campaign, Summary(campaign.SlugcatId));
    }

    /// <summary>Rebuilds the editor after the slugcat id is changed, for the Hunter-only behaviour.</summary>
    private CampaignEditViewModel EditorAs(string slugcatId)
    {
        var campaign = _session.Campaigns[0];
        _session.SetField(campaign, "SAV STATE NUMBER", slugcatId);

        var renamed = _session.Campaigns[0];
        return new CampaignEditViewModel(_session, renamed, Summary(renamed.SlugcatId));
    }

    private CampaignSummary Summary(string slugcatId)
    {
        var campaign = _session.Campaigns[0];

        return new CampaignSummary
        {
            SlugcatId = slugcatId,
            CycleNum = int.TryParse(_session.GetFieldValue(campaign, "CYCLENUM"), out var cycle) ? cycle : null,
        };
    }

    private string Field(string key) => _session.GetFieldValue(_session.Campaigns[0], key) ?? "";

    private DeathPersistentData Death()
        => DeathPersistentReader.Read(_session.GetFieldValue(_session.Campaigns[0], "DEATHPERSISTENTSAVEDATA"));

    // ---- loading ----

    [Fact]
    public void The_boxes_open_holding_what_the_save_holds()
    {
        var editor = Editor();

        Assert.Equal(Field("CYCLENUM"), editor.Cycle);
        Assert.Equal("SU_S04", editor.DenPos);
        Assert.Equal("White", _session.Campaigns[0].SlugcatId);
    }

    [Fact]
    public void Opening_an_editor_changes_nothing()
    {
        _ = Editor();

        Assert.False(_session.IsDirty);
        Assert.Empty(_session.Changes);
    }

    // ---- numbers ----

    [Fact]
    public void Typing_a_cycle_writes_it_into_the_save()
    {
        var editor = Editor();

        editor.Cycle = "1234";

        Assert.Equal("1234", Field("CYCLENUM"));
        Assert.True(editor.IsDirty);
        Assert.Equal("1 change", editor.ChangeCountText);
    }

    /// <summary>
    /// The boxes write on every keystroke, so the suggestions and the warnings keep up as the user
    /// types. What they must not do is count each keystroke as an edit of its own.
    /// </summary>
    [Fact]
    public void Typing_into_a_box_one_character_at_a_time_is_one_change()
    {
        var editor = Editor();

        editor.Cycle = "1";
        editor.Cycle = "12";
        editor.Cycle = "123";

        Assert.Equal("1 change", editor.ChangeCountText);
        Assert.Equal("123", Field("CYCLENUM"));
    }

    [Fact]
    public void Backspacing_a_box_back_to_where_it_started_leaves_no_change()
    {
        var editor = Editor();
        var before = editor.Cycle;

        editor.Cycle = before + "9";
        Assert.Equal("1 change", editor.ChangeCountText);

        editor.Cycle = before;

        Assert.Equal("No changes yet", editor.ChangeCountText);
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public void Editing_several_things_counts_each_of_them_once()
    {
        var editor = Editor();

        editor.Cycle = "40";
        editor.Cycle = "41";
        editor.Karma = "7";
        editor.Karma = "8";
        editor.Echoes.First(e => e.RegionCode == "SH").TalkedTo = true;
        editor.Gates.First(g => g.Name == "GATE_SU_HI").UnlockedField = true;

        // One for the cycle, one for karma, one for the echo, one for the gate.
        Assert.Equal("4 changes", editor.ChangeCountText);
        Assert.Contains(editor.Changes, c => c.Contains("Echo SH", StringComparison.Ordinal));
        Assert.Contains(editor.Changes, c => c.Contains("Gate GATE_SU_HI", StringComparison.Ordinal));
        Assert.Contains(editor.Changes, c => c.Contains("KARMA", StringComparison.Ordinal));
    }

    [Fact]
    public void Clearing_a_box_takes_the_field_out_of_the_save()
    {
        var editor = Editor();

        editor.Food = "";

        Assert.Null(_session.GetFieldValue(_session.Campaigns[0], "FOOD"));
    }

    /// <summary>
    /// The one edit this panel does not make, because there is nowhere useful to put it. The
    /// warning points at the raw field list, which writes anything.
    /// </summary>
    [Fact]
    public void Text_that_is_not_a_number_warns_and_leaves_the_field_alone()
    {
        var editor = Editor();
        var before = Field("CYCLENUM");

        editor.Cycle = "abc";

        Assert.Equal(before, Field("CYCLENUM"));
        Assert.Contains(editor.Warnings, w => w.Contains("not a whole number", StringComparison.Ordinal));
        Assert.Contains(editor.Warnings, w => w.Contains("raw field list", StringComparison.Ordinal));
    }

    // ---- karma ----

    [Fact]
    public void Karma_above_the_cap_is_written_and_explained_rather_than_refused()
    {
        var editor = Editor();

        editor.KarmaCap = "4";
        editor.Karma = "9";

        // Written, because the user asked for it.
        Assert.Equal(9, Death().Karma);
        Assert.Equal(4, Death().KarmaCap);

        // And explained, because the game will not play it that way.
        Assert.Contains(editor.Warnings, w => w.Contains("clamps", StringComparison.Ordinal));
    }

    [Fact]
    public void Karma_inside_the_cap_says_nothing()
    {
        var editor = Editor();

        editor.KarmaCap = "9";
        editor.Karma = "4";

        Assert.DoesNotContain(editor.Warnings, w => w.Contains("clamps", StringComparison.Ordinal));
    }

    // ---- hunter ----

    /// <summary>
    /// Hunter's cycle is not only a counter: the game reads the death flag from it. The edit goes
    /// through, and the panel says what it did.
    /// </summary>
    [Fact]
    public void Pushing_hunter_past_the_limit_warns_and_still_writes()
    {
        var editor = EditorAs("Red");

        editor.Cycle = "900";

        Assert.Equal("900", Field("CYCLENUM"));
        Assert.Contains(editor.Warnings, w => w.Contains("died of the rot", StringComparison.Ordinal));
    }

    [Fact]
    public void Another_slugcat_gets_no_hunter_warning()
    {
        var editor = Editor();

        editor.Cycle = "900";

        Assert.False(editor.IsHunter);
        Assert.DoesNotContain(editor.Warnings, w => w.Contains("rot", StringComparison.Ordinal));
    }

    [Fact]
    public void Hunter_is_offered_the_flags_only_hunter_has()
    {
        // Read the other slugcat first: renaming the campaign is what makes it Hunter, and one
        // session cannot be both at once.
        Assert.DoesNotContain(Editor().Flags, f => f.Label == "Hunter's death");

        Assert.Contains(EditorAs("Red").Flags, f => f.Label == "Hunter's death");
    }

    // ---- shelter ----

    [Fact]
    public void A_shelter_can_be_overwritten_and_is_suggested_as_it_is_typed()
    {
        var editor = Editor();

        editor.DenPos = "HI_S";

        Assert.Contains("HI_S01", editor.ShelterMatches);
        Assert.Equal("HI_S", Field("DENPOS"));
    }

    [Fact]
    public void Choosing_a_suggested_shelter_puts_it_in_the_box()
    {
        var editor = Editor();
        editor.DenPos = "HI_S";

        editor.UseShelterCommand.Execute("HI_S03");

        Assert.Equal("HI_S03", editor.DenPos);
        Assert.Equal("HI_S03", Field("DENPOS"));
    }

    [Fact]
    public void A_shelter_the_catalog_knows_stops_being_suggested_at()
    {
        var editor = Editor();

        editor.DenPos = "HI_S03";

        Assert.Empty(editor.ShelterMatches);
        Assert.DoesNotContain(editor.Warnings, w => w.Contains("not a shelter", StringComparison.Ordinal));
    }

    [Fact]
    public void A_room_that_is_not_a_shelter_is_written_and_warned_about()
    {
        var editor = Editor();

        editor.DenPos = "SU_A17";

        Assert.Equal("SU_A17", Field("DENPOS"));
        Assert.Contains(editor.Warnings, w => w.Contains("not a shelter", StringComparison.Ordinal));
    }

    // ---- echoes ----

    [Fact]
    public void Every_echo_is_offered_even_though_this_save_has_met_none()
    {
        var editor = Editor();

        Assert.Empty(Death().Echoes);
        Assert.Contains(editor.Echoes, e => e.RegionCode == "SH");
        Assert.Contains(editor.Echoes, e => e.RegionCode == "UW");
        Assert.All(editor.Echoes, e => Assert.True(e.NeverSeen));
    }

    [Fact]
    public void Marking_an_echo_as_talked_to_writes_it()
    {
        var editor = Editor();
        var shaded = editor.Echoes.First(e => e.RegionCode == "SH");

        shaded.TalkedTo = true;

        var echo = Assert.Single(Death().Echoes);
        Assert.Equal("SH", echo.RegionCode);
        Assert.Equal(EchoRecord.TalkedTo, echo.State);
    }

    [Fact]
    public void Setting_an_echo_back_to_never_seen_removes_it()
    {
        var editor = Editor();
        var shaded = editor.Echoes.First(e => e.RegionCode == "SH");

        shaded.Sensed = true;
        Assert.Single(Death().Echoes);

        shaded.NeverSeen = true;
        Assert.Empty(Death().Echoes);
    }

    [Fact]
    public void One_echo_can_be_moved_from_sensed_to_talked_to()
    {
        var editor = Editor();
        var shaded = editor.Echoes.First(e => e.RegionCode == "SH");

        shaded.Sensed = true;
        shaded.TalkedTo = true;

        var echo = Assert.Single(Death().Echoes);
        Assert.Equal(EchoRecord.TalkedTo, echo.State);
    }

    // ---- gates ----

    [Fact]
    public void Every_gate_is_offered_and_none_are_open_yet()
    {
        var editor = Editor();

        Assert.Contains(editor.Gates, g => g.Name == "GATE_SU_HI");
        Assert.All(editor.Gates, g => Assert.False(g.UnlockedField));
    }

    [Fact]
    public void Opening_a_gate_writes_it()
    {
        var editor = Editor();

        editor.Gates.First(g => g.Name == "GATE_SU_HI").UnlockedField = true;

        Assert.Equal(new[] { "GATE_SU_HI" }, Death().UnlockedGates);
    }

    [Fact]
    public void Closing_the_last_gate_leaves_none_recorded()
    {
        var editor = Editor();
        var gate = editor.Gates.First(g => g.Name == "GATE_SU_HI");

        gate.UnlockedField = true;
        gate.UnlockedField = false;

        Assert.Empty(Death().UnlockedGates);
    }

    [Fact]
    public void A_gate_from_a_mod_can_be_typed_in_and_opened()
    {
        var editor = Editor();

        editor.NewGateName = "GATE_ZZ_YY";
        editor.AddGateCommand.Execute(null);

        Assert.Contains(editor.Gates, g => g.Name == "GATE_ZZ_YY" && !g.KnownToTheGame);
        Assert.Contains("GATE_ZZ_YY", Death().UnlockedGates);
        Assert.Equal("", editor.NewGateName);
    }

    [Fact]
    public void Typing_a_gate_that_is_already_listed_just_opens_it()
    {
        var editor = Editor();
        var before = editor.Gates.Count;

        editor.NewGateName = "GATE_SU_HI";
        editor.AddGateCommand.Execute(null);

        Assert.Equal(before, editor.Gates.Count);
        Assert.Contains("GATE_SU_HI", Death().UnlockedGates);
    }

    // ---- flags ----

    [Fact]
    public void A_flag_in_the_record_can_be_turned_on_and_off()
    {
        var editor = Editor();
        var glow = editor.Flags.First(f => f.Label == "The glow");

        glow.IsOn = true;
        Assert.True(_session.HasField(_session.Campaigns[0], "HASTHEGLOW"));

        glow.IsOn = false;
        Assert.False(_session.HasField(_session.Campaigns[0], "HASTHEGLOW"));
    }

    [Fact]
    public void A_flag_in_the_death_persistent_blob_can_be_turned_on()
    {
        var editor = Editor();

        editor.Flags.First(f => f.Label == "Mark of communication").IsOn = true;

        Assert.True(Death().HasTheMark);
    }

    // ---- the whole thing ----

    /// <summary>
    /// Everything the panel writes has to end up as a save the game would take. This is the same
    /// check the writer makes, run over the edits the panel itself produced.
    /// </summary>
    [Fact]
    public void The_edits_a_panel_makes_produce_a_plan_with_no_problems()
    {
        var editor = Editor();

        editor.Cycle = "40";
        editor.Karma = "6";
        editor.DenPos = "HI_S03";
        editor.Echoes.First(e => e.RegionCode == "SH").TalkedTo = true;
        editor.Gates.First(g => g.Name == "GATE_SU_HI").UnlockedField = true;
        editor.Flags.First(f => f.Label == "The glow").IsOn = true;

        var plan = _session.BuildWritePlan();

        Assert.Empty(plan.Problems);
        Assert.True(plan.CanWrite);

        // Every edit landed where it was aimed. The count of changes is deliberately not asserted:
        // an edit that sets a value the save already held is not a change, and which of these that
        // is depends on the fixture rather than on the panel.
        Assert.Equal("40", Field("CYCLENUM"));
        Assert.Equal("HI_S03", Field("DENPOS"));
        Assert.Equal(6, Death().Karma);
        Assert.Contains(Death().Echoes, e => e.RegionCode == "SH" && e.State == EchoRecord.TalkedTo);
        Assert.Contains("GATE_SU_HI", Death().UnlockedGates);
        Assert.True(_session.HasField(_session.Campaigns[0], "HASTHEGLOW"));
    }
}
