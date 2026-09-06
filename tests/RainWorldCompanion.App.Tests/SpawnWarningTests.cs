using System.IO;
using RainWorldCompanion.Core.Editing;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.Core.Saves.Models;
using RainWorldCompanion.Services;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.App.Tests;

public class SpawnWarningTests : IDisposable
{
    private readonly TempDirectory _directory = new("spawn-warnings");

    [Theory]
    [InlineData("White", "SU_C04")]
    [InlineData("Yellow", "SU_C04")]
    [InlineData("Red", "LF_H01")]
    [InlineData("Gourmand", "SH_GOR02")]
    [InlineData("Artificer", "GW_A24")]
    [InlineData("Spear", "GATE_OE_SU")]
    [InlineData("Rivulet", "DS_RIVSTART")]
    [InlineData("Saint", "SI_SAINTINTRO")]
    [InlineData("Inv", "SH_E01")]
    [InlineData("Watcher", "HI_W14")]
    [InlineData("White", "OE_SEXTRA")]
    [InlineData("Yellow", "OE_SEXTRA")]
    [InlineData("Gourmand", "OE_SEXTRA")]
    [InlineData("Rivulet", "SL_AI")]
    [InlineData("Spear", "SI_A07")]
    [InlineData("Artificer", "LC_FINAL")]
    [InlineData("Watcher", "WAUA_TOYS")]
    [InlineData("Watcher", "WORA_AI")]
    [InlineData("Watcher", "WRSA_WEAVER02")]
    public void Official_story_spawn_rooms_open_without_unknown_shelter_warnings(string campaign, string room)
    {
        var (_, editor) = Open(campaign, room);
        Assert.DoesNotContain(editor.Warnings, w => w.Contains(room, StringComparison.Ordinal));
        Assert.Equal(room, editor.DenPos);
        Assert.Equal(room, editor.LastDenPos);
        Assert.True(editor.BuildWritePlan().CanWrite);
    }

    [Fact]
    public void Installed_ordinary_room_is_recognized_without_becoming_a_map_shelter()
    {
        var files = new Dictionary<string, string>
        {
            ["world/indexmaps/roomindexmap2.txt"] = "0 SI_C03\n1 SI_S03",
            ["world/si/world_si.txt"] = "ROOMS\nSI_C03 : SI_D01\nSI_S03 : SI_D01 : SHELTER\nEND ROOMS",
            ["world/si/properties.txt"] = "",
        };
        var world = DenWorldCatalog.Read(path => files.GetValueOrDefault(path));
        var (_, editor) = Open("Red", "SI_C03", new Picker(world));
        Assert.DoesNotContain(editor.Warnings, w => w.Contains("SI_C03", StringComparison.Ordinal));
        Assert.False(world.Check("SI_C03", "Red").Available);
        Assert.DoesNotContain(DenMapCatalog.Downpour.Dens, d => d.RoomId == "SI_C03");
        editor.DenPos = "SI_MISSING";
        Assert.Contains(editor.Warnings, w => w.Contains("SI_MISSING", StringComparison.Ordinal));
    }

    [Fact]
    public void Room_validation_does_not_depend_on_the_campaign_having_a_map()
    {
        var files = new Dictionary<string, string>
        {
            ["world/indexmaps/roomindexmap2.txt"] = "0 WRFA_A01\n1 WRFA_S01",
            ["world/wrfa/world_wrfa.txt"] = "ROOMS\nWRFA_A01 : WRFA_S01\nWRFA_S01 : WRFA_A01 : SHELTER\nEND ROOMS",
            ["world/wrfa/properties.txt"] = "",
        };
        var world = DenWorldCatalog.Read(path => files.GetValueOrDefault(path), false, true);
        var (_, editor) = Open("Watcher", "WRFA_A01", new Picker(world, false));
        Assert.False(editor.CanChooseDenOnMap);
        Assert.DoesNotContain(editor.Warnings, w => w.Contains("WRFA_A01", StringComparison.Ordinal));
    }

    private (SaveEditSession, CampaignEditViewModel) Open(string campaign, string room, IDenMapPicker? picker = null)
    {
        string path = Path.Combine(_directory.Path, "sav");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sav2.bin"), path);
        var session = SaveEditSession.Open(path);
        session.SetField(session.Campaigns[0], "SAV STATE NUMBER", campaign);
        var record = session.Campaigns[0];
        session.SetField(record, "TIMELINE", campaign);
        session.SetField(record, "DENPOS", room);
        session.SetField(record, "LASTVDENPOS", room);
        return (session, new CampaignEditViewModel(session, record, new CampaignSummary { SlugcatId = campaign }, denMapPicker: picker));
    }

    private sealed class Picker(DenWorldCatalog world, bool available = true) : IDenMapPicker
    {
        public DenMapAvailability GetAvailability(string slugcatId) => new(available, "Ready", true);
        public DenWorldCatalog LoadWorld() => world;
        public DenMapSelection? Pick(string currentRoomId, string fieldName, string timeline, DenWorldCatalog data) => null;
    }

    public void Dispose() => _directory.Dispose();
}
