using RainWorldCompanion.Core.Mods;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.Core.System;

namespace RainWorldCompanion.Tests;

public class DenMapTests
{
    private static OptionsRead Enabled(params string[] ids) => new(true, null, ids, new Dictionary<string, int>(), null);

    [Theory]
    [InlineData("White")]
    [InlineData("Yellow")]
    [InlineData("Red")]
    [InlineData("Gourmand")]
    [InlineData("Artificer")]
    [InlineData("Rivulet")]
    [InlineData("Spear")]
    [InlineData("Saint")]
    [InlineData("Inv")]
    [InlineData("white")]
    public void Expanded_maps_require_verified_Downpour_state(string slugcat)
    {
        Assert.True(DenMapAvailability.Check(slugcat, new(true, false, true), Enabled("moreslugcats")).Available);
        Assert.True(DenMapAvailability.Check(slugcat, new(true, false, true), Enabled("moreslugcats")).DownpourEnabled);
        Assert.False(DenMapAvailability.Check(slugcat, ExpansionPresence.Unknown, Enabled("moreslugcats")).Available);
        Assert.False(DenMapAvailability.Check(slugcat, new(true, false, true), OptionsRead.Failed("unreadable")).Available);
    }

    [Theory]
    [InlineData("Watcher")]
    [InlineData("CustomSlugcat")]
    public void Other_campaigns_keep_the_typed_editor(string slugcat) => Assert.False(
        DenMapAvailability.Check(slugcat, new(true, true, true), Enabled("moreslugcats")).Available);

    [Theory]
    [InlineData("White", true)]
    [InlineData("Yellow", true)]
    [InlineData("Red", true)]
    [InlineData("Gourmand", false)]
    [InlineData("Artificer", false)]
    [InlineData("Spear", false)]
    [InlineData("Rivulet", false)]
    [InlineData("Saint", false)]
    [InlineData("Inv", false)]
    public void Disabled_or_absent_Downpour_offers_only_base_campaign_maps(string slugcat, bool available)
    {
        var disabled = DenMapAvailability.Check(slugcat, new(true, false, true), Enabled("rwremix"));
        var absent = DenMapAvailability.Check(slugcat, new(false, false, true), OptionsRead.Failed("No options"));
        Assert.Equal(available, disabled.Available);
        Assert.Equal(available, absent.Available);
        Assert.False(disabled.DownpourEnabled);
        Assert.False(absent.DownpourEnabled);
    }

    [Theory]
    [InlineData("White", "Downpour")]
    [InlineData("Yellow", "Downpour")]
    [InlineData("Red", "Downpour")]
    [InlineData("Gourmand", "Downpour")]
    [InlineData("Inv", "Downpour")]
    [InlineData("Artificer", "Artificer")]
    [InlineData("Spear", "Spearmaster")]
    [InlineData("Rivulet", "Rivulet")]
    [InlineData("Saint", "Saint")]
    public void Every_supported_timeline_has_an_explicit_map(string timeline, string map)
    {
        Assert.Equal(map, DenMapCatalog.ForTimeline(timeline, true)?.Id);
        Assert.Equal(timeline is "White" or "Yellow" or "Red" ? "Vanilla" : null,
            DenMapCatalog.ForTimeline(timeline, false)?.Id);
        Assert.Null(DenMapCatalog.ForTimeline("Watcher", true));
        Assert.Null(DenMapCatalog.ForTimeline("Custom", true));
    }

    [Fact]
    public void Every_map_has_unique_canonical_dens_within_its_image()
    {
        Assert.Equal(6, DenMapCatalog.Maps.Count);
        foreach (var map in DenMapCatalog.Maps)
        {
            Assert.NotEmpty(map.Dens);
            Assert.Equal(map.Dens.Count, map.Dens.Select(d => d.RoomId.ToUpperInvariant()).Distinct().Count());
            Assert.Equal(map.Dens.Count, map.Dens.Select(d => (d.X, d.Y)).Distinct().Count());
            Assert.All(map.Dens, den =>
            {
                Assert.True(ShelterCatalog.IsKnown(den.RoomId), den.RoomId);
                Assert.True(RegionCatalog.IsKnown(den.RegionCode), den.RegionCode);
                Assert.StartsWith(den.RegionCode + "_", den.RoomId);
                Assert.InRange(den.X, 0, map.ImageWidth - 1);
                Assert.InRange(den.Y, 0, map.ImageHeight - 1);
            });
        }
    }

    [Fact]
    public void Catalog_has_one_entry_per_depicted_den_across_fifteen_regions()
    {
        Assert.Equal(115, DenMapCatalog.Downpour.Dens.Count);
        Assert.Equal(115, DenMapCatalog.Downpour.Dens.Select(d => (d.X, d.Y)).Distinct().Count());
        Assert.Equal(15, DenMapCatalog.Downpour.Dens.Select(d => d.RegionCode).Distinct().Count());
        Assert.All(DenMapCatalog.Downpour.Dens, den =>
        {
            Assert.True(ShelterCatalog.IsKnown(den.RoomId), den.RoomId);
            Assert.True(RegionCatalog.IsKnown(den.RegionCode));
            Assert.StartsWith(den.RegionCode + "_", den.RoomId);
        });
        Assert.False(DenMapCatalog.Downpour.Find("VS_S20")!.HasMapIcon);
        Assert.False(DenMapCatalog.Downpour.Find("SL_STOP")!.HasMapIcon);
        Assert.Null(DenMapCatalog.Downpour.Find("SL_SCRUSHED"));
        Assert.Null(DenMapCatalog.Downpour.Find("SU_A01"));
    }

    [Theory]
    [InlineData(" su_s01 ", 4635, 2608)]
    [InlineData("SH_S06", 7580, 2477)]
    [InlineData("MS_LAB5", 10524, 3350)]
    [InlineData("SL_S07", 7911, 3454)]
    [InlineData("OE_S02", 2604, 3299)]
    [InlineData("OE_S05x", 1370, 3273)]
    [InlineData("OE_SEXTRA", 419, 3183)]
    [InlineData("SL_STOP", 9785, 3128)]
    [InlineData("VS_S20", 6630, 2792)]
    public void Landmarks_keep_their_verified_image_positions(string room, double x, double y)
    {
        var den = Assert.IsType<MappedDen>(DenMapCatalog.Downpour.Find(room));
        Assert.Equal(x, den.X);
        Assert.Equal(y, den.Y);
    }

    [Fact]
    public void Outer_Expanse_includes_both_ordinary_and_ancient_shelters()
    {
        string[] expected = ["OE_EXSHELTER", "OE_MIDSHELTER", "OE_S01", "OE_S02", "OE_S03", "OE_S04", "OE_S05x", "OE_S06", "OE_SEXTRA", "OE_SFINAL"];
        Assert.Equal(expected, DenMapCatalog.Downpour.Dens.Where(den => den.RegionCode == "OE").Select(den => den.RoomId));
        Assert.Equal(expected, ShelterCatalog.ForRegion("OE"));
    }
}
