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
        var view = new DenMapViewModel("CUSTOM_ROOM", "Shelter");
        Assert.False(view.HasCurrentDen);
        Assert.False(view.CanUseDen);
        Assert.Equal(110, view.Matches.Count);
        view.Search = "outskirts";
        Assert.Equal(4, view.Matches.Count);
        Assert.All(view.Matches, d => Assert.Equal("SU", d.RegionCode));
        view.Search = " ms_bittershelter ";
        var match = Assert.Single(view.Matches);
        view.SelectedDen = match;
        Assert.True(view.CanUseDen);
        Assert.Equal("MS_BITTERSHELTER", view.SelectedRoomText);
        view.Search = "not a room";
        Assert.Empty(view.Matches);
    }

    [Fact]
    public void Zoom_keeps_the_pointer_on_the_same_image_position()
    {
        var viewport = new DenMapViewport();
        viewport.Resize(new Size(900, 600));
        viewport.Focus(DenMapCatalog.Find("SU_S01")!);
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
        foreach (var den in DenMapCatalog.All)
        {
            viewport.Focus(den);
            viewport.Zoom(1.7, new Point(450, 300));
            viewport.Pan(new Vector(20, -15));
            viewport.Resize(new Size(1000, 650));
            Point point = viewport.ToScreen(new Point(den.X, den.Y));
            Assert.Equal(den, viewport.HitTest(point, DenMapCatalog.All));
        }
        viewport.Fit();
        viewport.Zoom(2, new Point(500, 325));
        viewport.Pan(new Vector(1_000_000, 1_000_000));
        Assert.True(viewport.OffsetX <= 40);
        Assert.True(viewport.OffsetY <= 40);
    }

    [Fact]
    public void Published_resource_is_the_exact_image_used_for_the_catalog()
    {
        var resources = new ResourceManager("RainWorldCompanion.g", typeof(DenMapCanvas).Assembly);
        using var stream = resources.GetStream("assets/maps/downpour.png");
        Assert.NotNull(stream);
        Assert.Equal("DA6A137E63EA9206AAC046F559B75FBEFB42F2EBAE0E72A715CB31D2079E6632",
            Convert.ToHexString(SHA256.HashData(stream)));
    }

    private sealed class FakePicker : IDenMapPicker
    {
        public bool Available { get; set; } = true;
        public bool ThrowOnPick { get; init; }
        public string? Result { get; init; }
        public string? Current { get; private set; }
        public string? Field { get; private set; }
        public int Calls { get; private set; }
        public DenMapAvailability GetAvailability(string slugcatId) => new(Available, Available ? "Ready" : "Downpour is disabled.");
        public string? Pick(string currentRoomId, string fieldName)
        {
            Calls++;
            Current = currentRoomId;
            Field = fieldName;
            if (ThrowOnPick) throw new IOException("Missing map");
            return Result;
        }
    }
}

