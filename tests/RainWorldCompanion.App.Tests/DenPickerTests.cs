using System.IO;
using System.Resources;
using System.Security.Cryptography;
using System.Windows;
using RainWorldCompanion.Controls;
using RainWorldCompanion.Core.Editing;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.Core.Saves.Models;
using RainWorldCompanion.Services;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.App.Tests;

public class DenPickerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Disabling_Downpour_while_the_map_is_open_applies_neither_change(bool last)
    {
        using var directory = new TempDirectory("den-expansion-recheck");
        string path = Path.Combine(directory.Path, "sav2");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sav2.bin"), path);
        var session = SaveEditSession.Open(path);
        var record = session.Campaigns[0];
        var before = session.EnumerateFields(record).ToArray();
        var editor = new CampaignEditViewModel(session, record, new CampaignSummary(), denMapPicker: new FakePicker
        {
            Result = "LC_A05", ResultTimeline = "Artificer", DisableExpansionAfterPick = true,
        });
        (last ? editor.ChooseLastShelterOnMapCommand : editor.ChooseShelterOnMapCommand).Execute(null);
        Assert.Equal(before, session.EnumerateFields(record));
        Assert.False(session.IsDirty);
    }

    [Fact]
    public void Unreadable_world_keeps_typed_entry_and_suggestions_available()
    {
        using var directory = new TempDirectory("den-world-fallback");
        string path = Path.Combine(directory.Path, "sav2");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sav2.bin"), path);
        var session = SaveEditSession.Open(path);
        var editor = new CampaignEditViewModel(session, session.Campaigns[0], new CampaignSummary(),
            denMapPicker: new FakePicker { WorldUnavailable = true });
        Assert.False(editor.CanChooseDenOnMap);
        editor.DenPos = "SU_";
        Assert.Contains("SU_S01", editor.ShelterMatches);
        editor.UseShelterCommand.Execute("SU_S01");
        Assert.Equal("SU_S01", editor.DenPos);
        Assert.True(editor.BuildWritePlan().CanWrite);
    }

    public static IEnumerable<object[]> CampaignMapCases => DenWorldCatalog.Timelines.SelectMany(campaign =>
        new[] { false, true }.Select(last => new object[] { campaign, last, true }))
        .Concat(new[] { "White", "Yellow", "Red" }.SelectMany(campaign =>
            new[] { false, true }.Select(last => new object[] { campaign, last, false })));

    [Theory]
    [MemberData(nameof(CampaignMapCases))]
    public void Every_campaign_saves_a_selected_timeline_and_den_without_changing_character(string campaign, bool last, bool expanded)
    {
        using var directory = new TempDirectory("den-maps-save");
        string path = Path.Combine(directory.Path, "sav2");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sav2.bin"), path);
        var session = SaveEditSession.Open(path);
        session.SetField(session.Campaigns[0], "SAV STATE NUMBER", campaign);
        var record = session.Campaigns[0];
        session.SetField(record, "TIMELINE", campaign);
        var before = session.EnumerateFields(record).ToArray();
        var timelines = expanded ? DenWorldCatalog.Timelines.ToArray() : new[] { "White", "Yellow", "Red" };
        string target = timelines[(Array.IndexOf(timelines, campaign) + 1) % timelines.Length];
        string den = target switch
        {
            "Spear" => "DM_LAB5", "Artificer" => "LC_A05", "Rivulet" => "RM_LCS1", "Saint" => "CL_LCS2", _ => "SU_S01",
        };
        var editor = new CampaignEditViewModel(session, record, new CampaignSummary { SlugcatId = campaign },
            denMapPicker: new FakePicker { Result = den, ResultTimeline = target, Expanded = expanded });
        Assert.Equal(campaign, editor.DenTimeline);
        (last ? editor.ChooseLastShelterOnMapCommand : editor.ChooseShelterOnMapCommand).Execute(null);
        Assert.Equal(target, editor.Timeline);
        Assert.Equal(den, last ? editor.LastDenPos : editor.DenPos);
        var plan = editor.BuildWritePlan();
        Assert.True(plan.CanWrite);
        string written = Path.Combine(directory.Path, "written");
        File.WriteAllBytes(written, plan.NewBytes);
        var reloaded = SaveEditSession.Open(written);
        Assert.Equal(campaign, reloaded.Campaigns[0].SlugcatId);
        var after = reloaded.EnumerateFields(reloaded.Campaigns[0]).ToArray();
        string field = last ? "LASTVDENPOS" : "DENPOS";
        Assert.Equal(target, after.Single(f => f.Key == "TIMELINE").Value);
        Assert.Equal(den, after.Single(f => f.Key == field).Value);
        Assert.Equal(before.Where(f => f.Key != "TIMELINE" && f.Key != field),
            after.Where(f => f.Key != "TIMELINE" && f.Key != field));
    }

    [Fact]
    public void Timeline_switches_move_shared_selection_and_current_den_to_each_map()
    {
        var view = new DenMapViewModel("SU_S01", "Shelter", "White", TestWorld());
        int prompts = 0;
        foreach (string timeline in DenWorldCatalog.Timelines)
        {
            Assert.True(view.TryChangeTimeline(timeline, () => { prompts++; return true; }));
            var expected = DenMapCatalog.ForTimeline(timeline, true)!;
            Assert.Same(expected, view.Map);
            Assert.Equal(expected.Title, view.MapTitle);
            Assert.Equal(expected.Find("SU_S01"), view.CurrentDen);
            Assert.Equal(expected.Find("SU_S01"), view.SelectedDen);
            Assert.True(view.CanUseDen);
            Assert.All(view.Matches, den => Assert.Contains(den, expected.Dens));
        }
        Assert.Equal(1, prompts);
    }

    [Fact]
    public void Switching_away_from_a_unique_region_clears_selection_and_search_results()
    {
        var view = new DenMapViewModel("LC_A05", "Shelter", "Artificer", TestWorld());
        Assert.True(view.CanUseDen);
        view.Search = "LC_A05";
        Assert.Single(view.Matches);
        Assert.True(view.TryChangeTimeline("White", () => true));
        Assert.False(view.HasCurrentDen);
        Assert.Null(view.SelectedDen);
        Assert.Empty(view.Matches);
        Assert.False(view.CanUseDen);
        Assert.Contains("not on this map", view.CurrentText);
    }

    [Fact]
    public void Vanilla_timeline_choices_stay_on_the_vanilla_map()
    {
        var view = new DenMapViewModel("SU_S01", "Shelter", "White", TestWorld(false));
        Assert.Same(DenMapCatalog.Vanilla, view.Map);
        Assert.Equal(new[] { "Red", "White", "Yellow" }, view.TimelineOptions.Select(t => t.Id));
        Assert.False(view.TryChangeTimeline("Rivulet", () => throw new Exception("Unavailable expansion")));
        Assert.Equal("White", view.Timeline);
        Assert.True(view.TryChangeTimeline("Red", () => true));
        Assert.Same(DenMapCatalog.Vanilla, view.Map);
        Assert.True(view.CanUseDen);
    }

    [Fact]
    public void Fit_uses_the_new_images_dimensions_after_a_map_switch()
    {
        var viewport = new DenMapViewport();
        viewport.Resize(new Size(900, 600));
        foreach (var map in DenMapCatalog.Maps)
        {
            viewport.Zoom(3, new Point(450, 300));
            viewport.SetMap(map);
            Assert.True(viewport.IsFitted);
            Assert.Equal(Math.Min(900d / map.ImageWidth, 600d / map.ImageHeight), viewport.Scale, 8);
            Assert.Equal(new Point(450, 300), viewport.ToScreen(new Point(map.ImageWidth / 2d, map.ImageHeight / 2d)));
        }
    }

    [Fact]
    public void Unavailable_marker_requires_confirmation_each_time_even_after_dropdown_confirmation()
    {
        var view = new DenMapViewModel("SU_S01", "Shelter", "White", TestWorld());
        int dropdownPrompts = 0;
        int markerPrompts = 0;
        bool Dropdown() { dropdownPrompts++; return true; }
        bool Marker(string target) { Assert.Equal("Rivulet", target); markerPrompts++; return true; }
        Assert.True(view.TryChangeTimeline("Gourmand", Dropdown));
        var exclusive = DenMapCatalog.Downpour.Find("MS_S04")!;
        Assert.True(view.TrySelectDen(exclusive, Marker));
        Assert.Equal("Rivulet", view.Timeline);
        Assert.Equal("MS_S04", view.SelectedDen?.RoomId);
        Assert.Equal(DenMapCatalog.Rivulet.Find("MS_S04"), view.SelectedDen);
        Assert.True(view.TryChangeTimeline("White", Dropdown));
        Assert.True(view.TrySelectDen(exclusive, Marker));
        Assert.Equal(2, markerPrompts);
        Assert.Equal(1, dropdownPrompts);
    }

    [Fact]
    public void Declining_marker_switch_preserves_selection_and_does_not_authorize_dropdown_changes()
    {
        var view = new DenMapViewModel("SU_S01", "Shelter", "White", TestWorld());
        var previous = view.SelectedDen;
        var exclusive = DenMapCatalog.Downpour.Find("MS_S04")!;
        Assert.False(view.TrySelectDen(exclusive, _ => false));
        Assert.Equal("White", view.Timeline);
        Assert.Equal(previous, view.SelectedDen);
        Assert.False(view.NeedsTimelineChoice);
        Assert.True(view.TrySelectDen(exclusive, _ => true));
        int prompts = 0;
        Assert.True(view.TryChangeTimeline("White", () => { prompts++; return true; }));
        Assert.Equal(1, prompts);
    }

    [Fact]
    public void Marker_with_multiple_timelines_offers_compatible_choices_before_switching()
    {
        var view = new DenMapViewModel("SU_S01", "Shelter", "Yellow", TestWorld());
        var den = DenMapCatalog.Downpour.Find("OE_S06")!;
        Assert.False(view.TrySelectDen(den, _ => throw new Exception("A timeline has not been chosen")));
        Assert.Equal("Yellow", view.Timeline);
        Assert.True(view.NeedsTimelineChoice);
        Assert.True(view.TryChangeTimeline("White", () => true));
        Assert.Equal(den, view.SelectedDen);
        Assert.True(view.CanUseDen);
        Assert.False(view.NeedsTimelineChoice);
        Assert.False(new DenMapViewModel("SU_S01", "Shelter", "White", DenWorldCatalog.Unknown)
            .TrySelectDen(den, _ => throw new Exception("No verified timeline")));
    }

    [Fact]
    public void Declining_a_marker_timeline_choice_restores_the_full_dropdown()
    {
        var view = new DenMapViewModel("SU_S01", "Shelter", "Yellow", TestWorld());
        var previous = view.SelectedDen;
        var den = view.Map.Find("OE_S06")!;
        Assert.False(view.TrySelectDen(den, _ => throw new Exception("Choose a timeline first")));
        Assert.True(view.NeedsTimelineChoice);
        Assert.False(view.TryChangeTimeline("White", () => false));
        Assert.Equal("Yellow", view.Timeline);
        Assert.Equal(previous, view.SelectedDen);
        Assert.False(view.NeedsTimelineChoice);
        Assert.Equal(DenWorldCatalog.Timelines, view.TimelineOptions.Select(t => t.Id));
    }

    [Fact]
    public void Timeline_confirmation_is_once_per_map_open_and_declining_preserves_the_timeline()
    {
        int prompts = 0;
        bool Confirm() { prompts++; return true; }
        var view = new DenMapViewModel("SU_S01", "Shelter", "White", TestWorld());
        Assert.True(view.TryChangeTimeline("White", Confirm));
        Assert.Equal(0, prompts);
        Assert.True(view.TryChangeTimeline("Rivulet", Confirm));
        Assert.True(view.TryChangeTimeline("Yellow", Confirm));
        Assert.Equal(1, prompts);
        var reopened = new DenMapViewModel("SU_S01", "Shelter", "White", TestWorld());
        Assert.True(reopened.TryChangeTimeline("Rivulet", Confirm));
        Assert.Equal(2, prompts);
        var declined = new DenMapViewModel("SU_S01", "Shelter", "White", TestWorld());
        Assert.False(declined.TryChangeTimeline("Rivulet", () => { prompts++; return false; }));
        Assert.Equal(3, prompts);
        Assert.Equal("White", declined.Timeline);
        Assert.True(declined.TryChangeTimeline("Yellow", Confirm));
        Assert.Equal(4, prompts);
        Assert.Equal("Yellow", declined.Timeline);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Den_and_timeline_are_pending_together_until_confirmation_and_round_trip_together(bool last, bool cancel)
    {
        using var directory = new TempDirectory("den-combined");
        string path = Path.Combine(directory.Path, "sav2");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sav2.bin"), path);
        byte[] original = File.ReadAllBytes(path);
        var session = SaveEditSession.Open(path);
        var record = session.Campaigns.Single(c => c.SlugcatId == "White");
        var before = session.EnumerateFields(record).ToArray();
        CampaignEditViewModel? editor = null;
        var picker = new FakePicker
        {
            OnPick = (timeline, world) =>
            {
                Assert.Equal("White", timeline);
                var view = new DenMapViewModel("SU_S01", "Shelter", timeline, world);
                Assert.True(view.TryChangeTimeline("Rivulet", () => true));
                view.SelectedDen = view.Map.Find("MS_S04");
                Assert.True(view.CanUseDen);
                Assert.Equal("White", editor!.DenTimeline);
                Assert.False(session.IsDirty);
                Assert.Equal(original, File.ReadAllBytes(path));
                return cancel ? null : new DenMapSelection(view.SelectedDen!.RoomId, view.Timeline);
            },
        };
        editor = new CampaignEditViewModel(session, record, new CampaignSummary(), denMapPicker: picker);
        (last ? editor.ChooseLastShelterOnMapCommand : editor.ChooseShelterOnMapCommand).Execute(null);
        Assert.Equal(original, File.ReadAllBytes(path));
        if (cancel)
        {
            Assert.Equal(before, session.EnumerateFields(record));
            Assert.False(session.IsDirty);
            return;
        }
        Assert.Equal("Rivulet", editor.Timeline);
        Assert.Equal("MS_S04", last ? editor.LastDenPos : editor.DenPos);
        var plan = editor.BuildWritePlan();
        Assert.True(plan.CanWrite);
        string written = Path.Combine(directory.Path, "written");
        File.WriteAllBytes(written, plan.NewBytes);
        var reloaded = SaveEditSession.Open(written);
        var after = reloaded.EnumerateFields(reloaded.Campaigns.Single(c => c.SlugcatId == "White")).ToArray();
        string denField = last ? "LASTVDENPOS" : "DENPOS";
        Assert.Equal("Rivulet", after.Single(f => f.Key == "TIMELINE").Value);
        Assert.Equal("MS_S04", after.Single(f => f.Key == denField).Value);
        Assert.Equal(before.Where(f => f.Key != "TIMELINE" && f.Key != denField),
            after.Where(f => f.Key != "TIMELINE" && f.Key != denField));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_final_compatibility_check_applies_neither_den_nor_timeline(bool lostWorld)
    {
        using var directory = new TempDirectory("den-recheck");
        string path = Path.Combine(directory.Path, "sav2");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sav2.bin"), path);
        var session = SaveEditSession.Open(path);
        var record = session.Campaigns.Single(c => c.SlugcatId == "White");
        var before = session.EnumerateFields(record).ToArray();
        var editor = new CampaignEditViewModel(session, record, new CampaignSummary(), denMapPicker: new FakePicker
        {
            Result = lostWorld ? "MS_S04" : "SS_S01",
            ResultTimeline = "Rivulet",
            LoseWorldAfterPick = lostWorld,
        });
        editor.ChooseShelterOnMapCommand.Execute(null);
        Assert.Equal(before, session.EnumerateFields(record));
        Assert.False(session.IsDirty);
    }

    [Fact]
    public void Survivor_selection_preserves_the_game_room_index_casing()
    {
        using var directory = new TempDirectory("den-campaign");
        string path = Path.Combine(directory.Path, "sav2");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sav2.bin"), path);
        var session = SaveEditSession.Open(path);
        var record = session.Campaigns.Single(c => c.SlugcatId == "White");
        var editor = new CampaignEditViewModel(session, record, new CampaignSummary(),
            denMapPicker: new FakePicker { Result = "MS_BITTERSHELTER" });

        editor.ChooseShelterOnMapCommand.Execute(null);

        Assert.Equal("MS_bittershelter", editor.DenPos);
        var plan = editor.BuildWritePlan();
        Assert.True(plan.CanWrite);
        File.WriteAllBytes(Path.Combine(directory.Path, "written"), plan.NewBytes);
        var reloaded = SaveEditSession.Open(Path.Combine(directory.Path, "written"));
        Assert.Equal("MS_bittershelter", reloaded.GetFieldValue(reloaded.Campaigns.Single(c => c.SlugcatId == "White"), "DENPOS"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Confirmation_changes_only_the_target_field_in_the_pending_save(bool last)
    {
        using var directory = new TempDirectory("den-picker");
        string path = Path.Combine(directory.Path, "sav2");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sav2.bin"), path);
        byte[] original = File.ReadAllBytes(path);
        var session = SaveEditSession.Open(path);
        var record = session.Campaigns[0];
        var before = session.EnumerateFields(record).ToArray();
        var picker = new FakePicker { Result = "SU_S05" };
        var editor = new CampaignEditViewModel(session, record, new CampaignSummary { SlugcatId = record.SlugcatId }, denMapPicker: picker);
        string unchanged = last ? editor.DenPos : editor.LastDenPos;
        string previous = last ? editor.LastDenPos : editor.DenPos;

        (last ? editor.ChooseLastShelterOnMapCommand : editor.ChooseShelterOnMapCommand).Execute(null);

        Assert.Equal(previous, picker.Current);
        Assert.Equal(last ? "Last shelter" : "Shelter", picker.Field);
        Assert.Equal("SU_S05", last ? editor.LastDenPos : editor.DenPos);
        Assert.Equal(unchanged, last ? editor.DenPos : editor.LastDenPos);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.DoesNotContain(editor.Warnings, warning => warning.Contains("SU_S05"));
        var plan = editor.BuildWritePlan();
        Assert.True(plan.CanWrite, string.Join("\n", plan.Problems));
        string written = Path.Combine(directory.Path, "written");
        File.WriteAllBytes(written, plan.NewBytes);
        var reloaded = SaveEditSession.Open(written);
        Assert.Equal("SU_S05", reloaded.GetFieldValue(reloaded.Campaigns[0], last ? "LASTVDENPOS" : "DENPOS"));
        Assert.Equal(unchanged, reloaded.GetFieldValue(reloaded.Campaigns[0], last ? "DENPOS" : "LASTVDENPOS") ?? "");
        Assert.Equal(before.Where(f => f.Key != (last ? "LASTVDENPOS" : "DENPOS")),
            reloaded.EnumerateFields(reloaded.Campaigns[0]).Where(f => f.Key != (last ? "LASTVDENPOS" : "DENPOS")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cancel_and_lost_availability_preserve_the_session(bool disableBeforeOpen)
    {
        using var directory = new TempDirectory("den-cancel");
        string path = Path.Combine(directory.Path, "sav2");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sav2.bin"), path);
        var session = SaveEditSession.Open(path);
        var picker = new FakePicker();
        var record = session.Campaigns[0];
        var editor = new CampaignEditViewModel(session, record, new CampaignSummary(), denMapPicker: picker);
        if (disableBeforeOpen) picker.Available = false;
        editor.ChooseShelterOnMapCommand.Execute(null);
        Assert.False(session.IsDirty);
        Assert.Equal(disableBeforeOpen ? 0 : 1, picker.Calls);
    }

    [Fact]
    public void Map_failure_keeps_typed_entry_usable()
    {
        using var directory = new TempDirectory("den-failure");
        string path = Path.Combine(directory.Path, "sav2");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sav2.bin"), path);
        var session = SaveEditSession.Open(path);
        var editor = new CampaignEditViewModel(session, session.Campaigns[0], new CampaignSummary(),
            denMapPicker: new FakePicker { ThrowOnPick = true });
        editor.ChooseShelterOnMapCommand.Execute(null);
        Assert.Contains("could not be loaded", editor.DenMapStatus);
        Assert.False(session.IsDirty);
        editor.DenPos = "CUSTOM_ROOM";
        Assert.Equal("CUSTOM_ROOM", session.GetFieldValue(session.Campaigns[0], "DENPOS"));
        Assert.Contains(editor.Warnings, warning => warning.Contains("CUSTOM_ROOM"));
    }

    [Fact]
    public void Search_covers_all_regions_and_does_not_select_unknown_current_rooms()
    {
        var view = new DenMapViewModel("CUSTOM_ROOM", "Shelter", "White", TestWorld());
        Assert.False(view.HasCurrentDen);
        Assert.False(view.CanUseDen);
        Assert.Equal(DenMapCatalog.Downpour.Dens.Count - 1, view.Matches.Count);
        view.Search = "outskirts";
        Assert.Equal(4, view.Matches.Count);
        Assert.All(view.Matches, d => Assert.Equal("SU", d.RegionCode));
        view.Search = " ms_bittershelter ";
        var match = Assert.Single(view.Matches);
        view.SelectedDen = match;
        Assert.True(view.CanUseDen);
        Assert.Equal("MS_bittershelter", view.SelectedRoomText);
        view.Search = "not a room";
        Assert.Empty(view.Matches);
    }

    [Fact]
    public void Zoom_keeps_the_pointer_on_the_same_image_position()
    {
        var viewport = new DenMapViewport();
        viewport.Resize(new Size(900, 600));
        viewport.Focus(DenMapCatalog.Downpour.Find("SU_S01")!);
        var pointer = new Point(320, 240);
        Point before = viewport.ToImage(pointer);
        viewport.Zoom(1.4, pointer);
        Assert.Equal(before.X, viewport.ToImage(pointer).X, 8);
        Assert.Equal(before.Y, viewport.ToImage(pointer).Y, 8);
    }

    [Fact]
    public void Every_den_can_be_hit_after_zoom_pan_and_resize()
    {
        var viewport = new DenMapViewport();
        viewport.Resize(new Size(900, 600));
        foreach (var map in DenMapCatalog.Maps)
        {
            viewport.SetMap(map);
            foreach (var den in map.Dens)
            {
                viewport.Focus(den);
                viewport.Zoom(1.7, new Point(450, 300));
                viewport.Pan(new Vector(20, -15));
                viewport.Resize(new Size(1000, 650));
                Point point = viewport.ToScreen(new Point(den.X, den.Y));
                Assert.Equal(den, viewport.HitTest(point, map.Dens));
            }
        }
        viewport.Fit();
        viewport.Zoom(2, new Point(500, 325));
        viewport.Pan(new Vector(1_000_000, 1_000_000));
        Assert.True(viewport.OffsetX <= 40);
        Assert.True(viewport.OffsetY <= 40);
    }

    [Theory]
    [InlineData("downpour", "DA6A137E63EA9206AAC046F559B75FBEFB42F2EBAE0E72A715CB31D2079E6632")]
    [InlineData("vanilla", "485CD780D3EE3C53F3FB26E8EE39EED94820C9C2CD82351EBCFED3EB4D04287B")]
    [InlineData("artificer", "B1D59714E743DAD9B682BD4B8F62D3009B0FACFC16F9E746792B317C40FA398F")]
    [InlineData("spearmaster", "BEA2284ECA99525444E1B3778FC791B667BD85A659CA2C03C2C700FD192A8488")]
    [InlineData("rivulet", "F8BADA12AB28BC575A5AEAC0918D139F95D90082A18710FEC75B9E54BABA755E")]
    [InlineData("saint", "763DD943306F98EE85BF210A3AA3D02EAFE3106F400AE607C8E1F5332E085536")]
    public void Published_resource_is_the_exact_image_used_for_the_catalog(string id, string hash)
    {
        var resources = new ResourceManager("RainWorldCompanion.g", typeof(DenMapCanvas).Assembly);
        using var stream = resources.GetStream($"assets/maps/{id}.png");
        Assert.NotNull(stream);
        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(stream)));
    }

    private sealed class FakePicker : IDenMapPicker
    {
        public bool Available { get; set; } = true;
        public bool ThrowOnPick { get; init; }
        public string? Result { get; init; }
        public string? ResultTimeline { get; init; }
        public Func<string, DenWorldCatalog, DenMapSelection?>? OnPick { get; init; }
        public bool LoseWorldAfterPick { get; init; }
        public bool DisableExpansionAfterPick { get; init; }
        public bool WorldUnavailable { get; init; }
        public bool Expanded { get; init; } = true;
        public string? Current { get; private set; }
        public string? Field { get; private set; }
        public int Calls { get; private set; }
        public DenMapAvailability GetAvailability(string slugcatId) => new(Available, Available ? "Ready" : "Downpour is disabled.");
        public DenWorldCatalog LoadWorld() => WorldUnavailable || LoseWorldAfterPick && Calls > 0
            ? DenWorldCatalog.Unknown : TestWorld(Expanded && !(DisableExpansionAfterPick && Calls > 0));
        public DenMapSelection? Pick(string currentRoomId, string fieldName, string timeline, DenWorldCatalog world)
        {
            Calls++;
            Current = currentRoomId;
            Field = fieldName;
            if (ThrowOnPick) throw new IOException("Missing map");
            return OnPick is not null ? OnPick(timeline, world) : Result is null ? null : new(Result, ResultTimeline ?? timeline);
        }
    }

    private static DenWorldCatalog TestWorld(bool downpourEnabled = true)
    {
        var files = new Dictionary<string, string>();
        var dens = (downpourEnabled ? DenMapCatalog.Maps : new[] { DenMapCatalog.Vanilla })
            .SelectMany(m => m.Dens).DistinctBy(d => d.RoomId).ToArray();
        files["world/indexmaps/roomindexmap2.txt"] = string.Join("\n", dens.Select((d, i) => $"{i} {d.RoomId}"));
        foreach (var region in dens.GroupBy(d => d.RegionCode.ToLowerInvariant()))
        {
            files[$"world/{region.Key}/world_{region.Key}.txt"] = "ROOMS\n" + string.Join("\n", region.Select(d => $"{d.RoomId} : ROOM : SHELTER")) + "\nEND ROOMS";
            files[$"world/{region.Key}/properties.txt"] = "";
        }
        files["world/ms/properties.txt"] = string.Join("\n", DenWorldCatalog.Timelines.Where(t => t != "Rivulet")
            .Select(t => $"Broken Shelters: {t}: MS_S04"));
        files["world/oe/properties.txt"] = "Broken Shelters: Yellow: OE_S06";
        return DenWorldCatalog.Read(path => files.GetValueOrDefault(path), downpourEnabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Broken_shelter_is_rejected_for_both_fields_and_timeline_changes_are_respected(bool last)
    {
        using var directory = new TempDirectory("den-timeline");
        string path = Path.Combine(directory.Path, "sav2");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sav2.bin"), path);
        var session = SaveEditSession.Open(path);
        var record = session.Campaigns.Single(c => c.SlugcatId == "White");
        var editor = new CampaignEditViewModel(session, record, new CampaignSummary(),
            denMapPicker: new FakePicker { Result = "MS_S04" });
        string previous = last ? editor.LastDenPos : editor.DenPos;
        var command = last ? editor.ChooseLastShelterOnMapCommand : editor.ChooseShelterOnMapCommand;
        command.Execute(null);
        Assert.Equal(previous, last ? editor.LastDenPos : editor.DenPos);
        Assert.False(session.IsDirty);
        Assert.Contains("broken", editor.DenMapStatus);
        editor.Timeline = "Rivulet";
        command.Execute(null);
        Assert.Equal("MS_S04", last ? editor.LastDenPos : editor.DenPos);
        Assert.Equal("Rivulet", editor.Timeline);
    }

    [Fact]
    public void Switching_timeline_refreshes_unavailable_dens()
    {
        var view = new DenMapViewModel("SU_S01", "Shelter", "White", TestWorld());
        Assert.DoesNotContain(view.VisibleDens, d => d.RoomId == "MS_S04");
        view.Search = "MS_S04";
        Assert.Empty(view.Matches);
        Assert.True(view.TryChangeTimeline("Rivulet", () => true));
        view.SelectedDen = Assert.Single(view.Matches);
        Assert.True(view.CanUseDen);
        Assert.True(view.TryChangeTimeline("White", () => throw new Exception("Repeated confirmation")));
        Assert.Null(view.SelectedDen);
        Assert.Empty(view.Matches);
        var unknown = new DenMapViewModel("MS_S04", "Shelter", "CustomTimeline", TestWorld());
        Assert.False(unknown.CanUseDen);
    }

    [Fact]
    public void Typed_dens_keep_advisories_and_suggestions_use_only_available_canonical_names()
    {
        using var directory = new TempDirectory("den-advisory");
        string path = Path.Combine(directory.Path, "sav2");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sav2.bin"), path);
        var session = SaveEditSession.Open(path);
        var editor = new CampaignEditViewModel(session, session.Campaigns.Single(c => c.SlugcatId == "White"),
            new CampaignSummary(), denMapPicker: new FakePicker());
        editor.DenPos = "MS_";
        Assert.DoesNotContain("MS_S04", editor.ShelterMatches);
        Assert.Contains("MS_bittershelter", editor.ShelterMatches);
        editor.DenPos = "MS_S04";
        Assert.Contains(editor.Warnings, w => w.Contains("broken"));
        Assert.True(editor.BuildWritePlan().CanWrite);
        editor.DenPos = "MS_BITTERSHELTER";
        Assert.Contains(editor.Warnings, w => w.Contains("case-sensitive"));
    }
}

