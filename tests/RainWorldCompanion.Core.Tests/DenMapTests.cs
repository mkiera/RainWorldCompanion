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
    [InlineData("white")]
    public void Supported_campaigns_require_installed_and_enabled_Downpour(string slugcat)
    {
        Assert.True(DenMapAvailability.Check(slugcat, new(true, false, true), Enabled("moreslugcats")).Available);
        Assert.False(DenMapAvailability.Check(slugcat, new(true, false, true), Enabled("rwremix")).Available);
        Assert.False(DenMapAvailability.Check(slugcat, new(false, false, true), Enabled("moreslugcats")).Available);
        Assert.False(DenMapAvailability.Check(slugcat, ExpansionPresence.Unknown, Enabled("moreslugcats")).Available);
        Assert.False(DenMapAvailability.Check(slugcat, new(true, false, true), OptionsRead.Failed("unreadable")).Available);
    }

    [Theory]
    [InlineData("Artificer")]
    [InlineData("Rivulet")]
    [InlineData("Spear")]
    [InlineData("Saint")]
    [InlineData("Watcher")]
    [InlineData("Inv")]
    [InlineData("CustomSlugcat")]
    public void Other_campaigns_keep_the_typed_editor(string slugcat) => Assert.False(
        DenMapAvailability.Check(slugcat, new(true, true, true), Enabled("moreslugcats")).Available);

    [Fact]
    public void Catalog_has_one_entry_per_depicted_den_across_fifteen_regions()
    {
        Assert.Equal(113, DenMapCatalog.All.Count);
        Assert.Equal(113, DenMapCatalog.All.Select(d => (d.X, d.Y)).Distinct().Count());
        Assert.Equal(15, DenMapCatalog.All.Select(d => d.RegionCode).Distinct().Count());
        Assert.All(DenMapCatalog.All, den =>
        {
            Assert.True(ShelterCatalog.IsKnown(den.RoomId), den.RoomId);
            Assert.True(RegionCatalog.IsKnown(den.RegionCode));
            Assert.StartsWith(den.RegionCode + "_", den.RoomId);
        });
        Assert.Null(DenMapCatalog.Find("VS_S20"));
        Assert.Null(DenMapCatalog.Find("SL_STOP"));
        Assert.Null(DenMapCatalog.Find("SL_SCRUSHED"));
        Assert.Null(DenMapCatalog.Find("SU_A01"));
    }

    [Theory]
    [InlineData(" su_s01 ", 4635, 2608)]
    [InlineData("SH_S06", 7580, 2477)]
    [InlineData("MS_LAB5", 10524, 3350)]
    [InlineData("SL_S07", 7911, 3454)]
    [InlineData("OE_S02", 2604, 3299)]
    [InlineData("OE_S05x", 1370, 3273)]
    [InlineData("OE_SEXTRA", 419, 3183)]
    public void Landmarks_keep_their_verified_image_positions(string room, double x, double y)
    {
        var den = Assert.IsType<MappedDen>(DenMapCatalog.Find(room));
        Assert.Equal(x, den.X);
        Assert.Equal(y, den.Y);
    }

    [Fact]
    public void Outer_Expanse_includes_both_ordinary_and_ancient_shelters()
    {
        string[] expected = ["OE_EXSHELTER", "OE_MIDSHELTER", "OE_S01", "OE_S02", "OE_S03", "OE_S04", "OE_S05x", "OE_S06", "OE_SEXTRA", "OE_SFINAL"];
        Assert.Equal(expected, DenMapCatalog.All.Where(den => den.RegionCode == "OE").Select(den => den.RoomId));
        Assert.Equal(expected, ShelterCatalog.ForRegion("OE"));
    }
}
