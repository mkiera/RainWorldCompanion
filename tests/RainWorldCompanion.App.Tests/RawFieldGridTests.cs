using System.IO;
using RainWorldCompanion.Core.Editing;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.Core.Saves.Models;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.App.Tests;

/// <summary>
/// The two halves of the panel are two views of one record, so most of these check that a
/// change made in one shows up in the other. The rest check the thing the named boxes
/// deliberately do not do: write whatever was typed, including text a field is not supposed
/// to hold.
/// </summary>
public class RawFieldGridTests : IDisposable
{
    private readonly TempDirectory _directory = new("raw-fields");
    private readonly SaveEditSession _session;

    public RawFieldGridTests()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sav2.bin");
        var path = Path.Combine(_directory.Path, "sav2");
        File.Copy(fixture, path);
        _session = SaveEditSession.Open(path);
    }

    public void Dispose() => _directory.Dispose();

    private CampaignRecordRef Campaign => _session.Campaigns[0];

    private CampaignEditViewModel Editor()
    {
        CampaignRecordRef campaign = Campaign;

        return new CampaignEditViewModel(
            _session,
            campaign,
            new CampaignSummary
            {
                SlugcatId = campaign.SlugcatId,
                CycleNum = int.TryParse(_session.GetFieldValue(campaign, "CYCLENUM"), out var cycle) ? cycle : null,
            });
    }

    private string Field(string key, int occurrence = 0)
        => _session.GetFieldValue(Campaign, key, occurrence) ?? "";

    private static RawFieldRow Row(CampaignEditViewModel editor, string key, int occurrence = 0)
        => editor.RawFields.Single(row => row.Key == key && row.Occurrence == occurrence);

    [Fact]
    public void Every_field_the_record_holds_gets_a_row_in_the_order_the_file_holds_them()
    {
        var editor = Editor();

        Assert.Equal(
            _session.EnumerateFields(Campaign).Select(field => field.Key),
            editor.RawFields.Select(row => row.Key));
    }

    [Fact]
    public void A_row_opens_holding_what_the_field_holds()
    {
        var editor = Editor();

        Assert.Equal(Field("VERSION"), Row(editor, "VERSION").Value);
        Assert.Equal(Field("DENPOS"), Row(editor, "DENPOS").Value);
        Assert.Equal(Field("DEATHPERSISTENTSAVEDATA"), Row(editor, "DEATHPERSISTENTSAVEDATA").Value);
    }

    [Fact]
    public void Fields_the_named_boxes_never_mention_are_in_the_list()
    {
        var editor = Editor();

        Assert.Contains(editor.RawFields, row => row.Key == "RESPAWNS");
        Assert.Contains(editor.RawFields, row => row.Key == "COMMUNITIES");
        Assert.Contains(editor.RawFields, row => row.Key == "MISCWORLDSAVEDATA");
    }

    [Fact]
    public void The_search_box_narrows_the_list_by_name()
    {
        var editor = Editor();

        editor.RawSearch = "DENPOS";

        Assert.Equal(
            new[] { "DENPOS", "LASTVDENPOS" },
            editor.VisibleRawFields.Select(row => row.Key).OrderBy(key => key));
    }

    [Fact]
    public void The_search_box_narrows_the_list_by_what_a_field_holds()
    {
        var editor = Editor();
        string den = Field("DENPOS");

        editor.RawSearch = den;

        Assert.Contains(editor.VisibleRawFields, row => row.Key == "DENPOS");
        Assert.DoesNotContain(editor.VisibleRawFields, row => row.Key == "VERSION");
    }

    [Fact]
    public void Emptying_the_search_box_shows_everything_again()
    {
        var editor = Editor();
        int all = editor.RawFields.Count;

        editor.RawSearch = "DENPOS";
        editor.RawSearch = "";

        Assert.Equal(all, editor.VisibleRawFields.Count);
        Assert.Equal($"{all} fields", editor.RawFieldCountText);
    }

    [Fact]
    public void The_count_says_how_much_of_the_list_the_search_is_showing()
    {
        var editor = Editor();

        editor.RawSearch = "DENPOS";

        Assert.Equal($"2 of {editor.RawFields.Count} fields", editor.RawFieldCountText);
    }

    [Fact]
    public void Typing_into_a_row_writes_the_field()
    {
        var editor = Editor();

        Row(editor, "RESPAWNS").Value = "1.2.3";

        Assert.Equal("1.2.3", Field("RESPAWNS"));
        Assert.True(editor.IsDirty);
    }

    /// <summary>
    /// The one thing the named boxes will not do. CYCLENUM is a number to the game, so the cycle box
    /// leaves text alone and says where to write it. This is where.
    /// </summary>
    [Fact]
    public void The_list_writes_text_into_a_field_the_named_box_reads_as_a_number()
    {
        var editor = Editor();

        editor.Cycle = "abc";

        Assert.NotEqual("abc", Field("CYCLENUM"));
        Assert.Contains(editor.Warnings, warning => warning.Contains("not a whole number"));

        Row(editor, "CYCLENUM").Value = "abc";

        Assert.Equal("abc", Field("CYCLENUM"));
    }

    [Fact]
    public void Typing_a_field_out_character_by_character_counts_as_one_change()
    {
        var editor = Editor();
        RawFieldRow row = Row(editor, "RESPAWNS");

        row.Value = "1";
        row.Value = "12";
        row.Value = "123";

        Assert.Single(editor.Changes);
        Assert.Equal("123", Field("RESPAWNS"));
    }

    [Fact]
    public void Typing_a_field_back_to_where_it_started_stops_being_a_change()
    {
        var editor = Editor();
        RawFieldRow row = Row(editor, "RESPAWNS");
        string before = row.Value;

        row.Value = "999";
        row.Value = before;

        Assert.Empty(editor.Changes);
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public void Reverting_a_row_puts_the_field_back()
    {
        var editor = Editor();
        RawFieldRow row = Row(editor, "VERSION");
        string before = row.Value;

        row.Value = "999";
        Assert.True(row.IsChanged);

        editor.RevertRawFieldCommand.Execute(row);

        Assert.False(row.IsChanged);
        Assert.Equal(before, Field("VERSION"));
        Assert.Empty(editor.Changes);
    }

    [Fact]
    public void A_raw_edit_of_a_named_field_moves_the_box_that_names_it()
    {
        var editor = Editor();

        Row(editor, "CYCLENUM").Value = "412";

        Assert.Equal("412", editor.Cycle);
        Assert.Equal("412", Field("CYCLENUM"));
    }

    [Fact]
    public void An_edit_in_a_named_box_moves_the_row_beside_it()
    {
        var editor = Editor();

        editor.Cycle = "77";
        editor.DenPos = "HI_S03";

        Assert.Equal("77", Row(editor, "CYCLENUM").Value);
        Assert.Equal("HI_S03", Row(editor, "DENPOS").Value);
    }

    /// <summary>
    /// DEATHPERSISTENTSAVEDATA is one field holding karma, every echo and every gate, and the rows
    /// showing those are built once from what it held. A raw edit of it has to build them again, or
    /// half the panel carries on describing a value that has gone.
    /// </summary>
    [Fact]
    public void A_raw_edit_of_the_death_persistent_field_rebuilds_everything_read_out_of_it()
    {
        var editor = Editor();

        string blob = Field("DEATHPERSISTENTSAVEDATA");
        blob = DeathPersistentEditor.SetInt(blob, DeathPersistentEditor.KarmaField, 9);
        blob = DeathPersistentEditor.SetEcho(blob, "SI", DeathPersistentEditor.EchoTalkedTo);
        blob = DeathPersistentEditor.SetGate(blob, "GATE_SU_HI", true);

        Row(editor, "DEATHPERSISTENTSAVEDATA").Value = blob;

        Assert.Equal("9", editor.Karma);
        Assert.Equal(
            DeathPersistentEditor.EchoTalkedTo,
            editor.Echoes.Single(echo => echo.RegionCode == "SI").State);
        Assert.True(editor.Gates.Single(gate => gate.Name == "GATE_SU_HI").UnlockedField);
    }

    [Fact]
    public void A_raw_edit_that_clears_a_named_field_leaves_its_box_empty()
    {
        var editor = Editor();

        editor.RemoveRawFieldCommand.Execute(Row(editor, "DENPOS"));

        Assert.Equal("", editor.DenPos);
        Assert.False(_session.HasField(Campaign, "DENPOS"));
    }

    [Fact]
    public void Removing_a_field_takes_it_out_of_the_save_and_off_the_list()
    {
        var editor = Editor();

        editor.RemoveRawFieldCommand.Execute(Row(editor, "RESPAWNS"));

        Assert.False(_session.HasField(Campaign, "RESPAWNS"));
        Assert.DoesNotContain(editor.RawFields, row => row.Key == "RESPAWNS");
        Assert.Contains(editor.Changes, change => change.Contains("removed RESPAWNS"));
    }

    [Fact]
    public void Removing_a_field_leaves_every_other_field_where_it_was()
    {
        var editor = Editor();
        string den = Field("DENPOS");
        string version = Field("VERSION");

        editor.RemoveRawFieldCommand.Execute(Row(editor, "RESPAWNS"));

        Assert.Equal(den, Field("DENPOS"));
        Assert.Equal(version, Field("VERSION"));
    }

    [Fact]
    public void A_field_the_save_never_carried_can_be_added()
    {
        var editor = Editor();

        editor.NewFieldKey = " SOMEMODFIELD ";
        editor.NewFieldValue = "17";
        editor.AddRawFieldCommand.Execute(null);

        Assert.Equal("17", Field("SOMEMODFIELD"));
        Assert.Contains(editor.RawFields, row => row.Key == "SOMEMODFIELD" && row.Value == "17");
        Assert.Equal("", editor.NewFieldKey);
        Assert.Equal("", editor.NewFieldValue);
    }

    [Fact]
    public void A_field_can_be_added_as_a_name_with_no_value_beside_it()
    {
        var editor = Editor();

        editor.NewFieldKey = "SOMEMODFLAG";
        editor.NewFieldIsFlag = true;
        editor.AddRawFieldCommand.Execute(null);

        RawFieldRow row = Row(editor, "SOMEMODFLAG");

        Assert.True(row.IsFlag);
        Assert.True(_session.HasField(Campaign, "SOMEMODFLAG"));
        Assert.Null(_session.GetFieldValue(Campaign, "SOMEMODFLAG"));
    }

    [Fact]
    public void Adding_nothing_does_nothing()
    {
        var editor = Editor();
        int before = editor.RawFields.Count;

        editor.NewFieldKey = "   ";
        editor.AddRawFieldCommand.Execute(null);

        Assert.Equal(before, editor.RawFields.Count);
        Assert.False(editor.IsDirty);
    }

    /// <summary>
    /// The game writes some keys more than once, so the same key twice is a real record rather than
    /// a mistake to fold together. Each one is addressed by position and edited on its own.
    /// </summary>
    [Fact]
    public void A_name_the_record_already_carries_can_be_added_a_second_time_and_edited_apart()
    {
        var editor = Editor();
        string first = Field("RESPAWNS");

        editor.NewFieldKey = "RESPAWNS";
        editor.NewFieldValue = "9";
        editor.AddRawFieldCommand.Execute(null);

        Assert.Equal(new[] { 0, 1 }, editor.RawFields.Where(r => r.Key == "RESPAWNS").Select(r => r.Occurrence));
        Assert.Equal("9", Field("RESPAWNS", 1));

        Row(editor, "RESPAWNS", 1).Value = "42";

        Assert.Equal(first, Field("RESPAWNS"));
        Assert.Equal("42", Field("RESPAWNS", 1));
    }

    [Fact]
    public void Removing_the_first_of_two_leaves_the_second_editable()
    {
        var editor = Editor();

        editor.NewFieldKey = "RESPAWNS";
        editor.NewFieldValue = "9";
        editor.AddRawFieldCommand.Execute(null);

        editor.RemoveRawFieldCommand.Execute(Row(editor, "RESPAWNS"));

        RawFieldRow left = Row(editor, "RESPAWNS");
        Assert.Equal("9", left.Value);

        left.Value = "10";

        Assert.Equal("10", Field("RESPAWNS"));
        Assert.Equal("", Field("RESPAWNS", 1));
    }

    /// <summary>
    /// Typing the string the file splits fields on is not refused. It genuinely makes another
    /// field, so the list shows two afterwards and a line of advice says that is what happened.
    /// </summary>
    [Fact]
    public void A_value_given_a_field_separator_becomes_two_fields_and_says_so()
    {
        var editor = Editor();
        int before = editor.RawFields.Count;

        Row(editor, "VERSION").Value =
            "1" + SavePayloadReader.FieldSeparator + "MODTHING" + SavePayloadReader.ValueSeparator + "7";

        Assert.Equal(before + 1, editor.RawFields.Count);
        Assert.Equal("1", Field("VERSION"));
        Assert.Equal("7", Field("MODTHING"));
        Assert.Contains(editor.Warnings, warning => warning.Contains("more than one field"));
    }

    /// <summary>
    /// The advice stands until the next raw edit, because it describes something that already
    /// happened and the split value it describes is gone by the time it is shown.
    /// </summary>
    [Fact]
    public void The_advice_stands_until_the_next_raw_edit()
    {
        var editor = Editor();

        Row(editor, "VERSION").Value = "1" + SavePayloadReader.FieldSeparator + "MODTHING";

        editor.Cycle = "5";
        Assert.Contains(editor.Warnings, warning => warning.Contains("more than one field"));

        Row(editor, "VERSION").Value = "2";
        Assert.DoesNotContain(editor.Warnings, warning => warning.Contains("more than one field"));
    }

    [Fact]
    public void A_raw_edit_writes_a_save_the_game_would_accept()
    {
        var editor = Editor();

        Row(editor, "RESPAWNS").Value = "1.2.3";
        editor.NewFieldKey = "SOMEMODFIELD";
        editor.NewFieldValue = "17";
        editor.AddRawFieldCommand.Execute(null);

        SaveWritePlan plan = editor.BuildWritePlan();

        Assert.Empty(plan.Problems);
        Assert.False(plan.IsNoOp);
    }
}
