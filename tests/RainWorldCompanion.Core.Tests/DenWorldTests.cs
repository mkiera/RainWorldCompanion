using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Tests;

public class DenWorldTests
{
    [Fact]
    public void Vanilla_reader_ignores_installed_and_stale_merged_Downpour_files()
    {
        using var install = new TempDirectory("den-vanilla");
        string assets = Path.Combine(install.Path, "RainWorld_Data", "StreamingAssets");
        void Write(string relative, string value)
        {
            string path = Path.Combine(assets, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, value);
        }
        Write("world/indexmaps/roomindexmap2.txt", "0 SU_S01\n1 SU_S05");
        Write("world/su/world_su.txt", "ROOMS\nSU_S01 : ROOM : SHELTER\nEND ROOMS");
        Write("world/su/properties.txt", "");
        Write("mergedmods/world/su/world_su.txt", "ROOMS\nSU_S05 : ROOM : SHELTER\nEND ROOMS");
        Write("mods/moreslugcats/world/su/properties.txt", "Broken Shelters: White: SU_S01");
        var vanilla = DenWorldCatalog.Load(install.Path, false);
        Assert.True(vanilla.Check("SU_S01", "White").Available);
        Assert.False(vanilla.Check("SU_S05", "White").Available);
        Assert.False(vanilla.Check("SU_S01", "Saint").Available);
        Assert.Equal(new[] { "Red", "White", "Yellow" }, vanilla.SupportedTimelines);
        Assert.False(vanilla.DownpourEnabled);
        Assert.True(DenWorldCatalog.Load(install.Path, true).Check("SU_S05", "White").Available);
    }

    [Fact]
    public void Installed_reader_uses_merged_rules_and_does_not_guess_when_merges_are_missing()
    {
        using var install = new TempDirectory("den-world");
        string assets = Path.Combine(install.Path, "RainWorld_Data", "StreamingAssets");
        void Write(string relative, string value)
        {
            string path = Path.Combine(assets, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, value);
        }
        Write("world/indexmaps/roomindexmap2.txt", "0 OE_S06");
        Write("mods/moreslugcats/world/oe/world_oe.txt", "ROOMS\nOE_S06 : ROOM : ANCIENTSHELTER\nEND ROOMS");
        Write("mods/moreslugcats/world/oe/properties.txt", "");
        Assert.True(DenWorldCatalog.Load(install.Path).Check("OE_S06", "Yellow").Available);
        Write("mergedmods/world/oe/properties.txt", "Broken Shelters: Yellow: OE_S06");
        Assert.False(DenWorldCatalog.Load(install.Path).Check("OE_S06", "Yellow").Available);
        Write("mods/moreslugcats/modify/world/oe/properties-white.txt", "[MERGE]\nBroken Shelters: White: OE_S06");
        Assert.False(DenWorldCatalog.Load(install.Path).Check("OE_S06", "White").Available);
    }

    [Fact]
    public void World_rules_cover_exclusive_hidden_conditional_and_ancient_shelters()
    {
        var files = Files("""
            CONDITIONAL LINKS
            Red : EXCLUSIVEROOM : OE_S01
            Gourmand : EXCLUSIVEROOM : OE_S01
            White : HIDEROOM : OE_S02
            Saint : REPLACEROOM : OE_S06 : OE_S06_saint
            END CONDITIONAL LINKS
            ROOMS
            OE_S01 : ROOM : SHELTER
            OE_S02 : ROOM : ANCIENTSHELTER
            (X-White,Yellow)OE_S03 : ROOM : SHELTER
            (0,1)OE_S04 : ROOM : SHELTER
            OE_S05x : ROOM : ANCIENTSHELTER
            OE_S06 : ROOM : SHELTER
            END ROOMS
            """);
        var world = DenWorldCatalog.Read(path => files.GetValueOrDefault(path));
        Assert.False(world.Check("OE_S01", "White").Available);
        Assert.True(world.Check("OE_S01", "Red").Available);
        Assert.True(world.Check("OE_S01", "Gourmand").Available);
        Assert.False(world.Check("OE_S02", "White").Available);
        Assert.True(world.Check("OE_S02", "Yellow").Available);
        Assert.False(world.Check("OE_S03", "Yellow").Available);
        Assert.True(world.Check("OE_S03", "Red").Available);
        Assert.True(world.Check("OE_S04", "White").Available);
        Assert.False(world.Check("OE_S04", "Red").Available);
        Assert.True(world.Check("oe_s05x", "White").Available);
        Assert.Equal("OE_S05x", world.Check("OE_S05X", "White").RoomId);
        Assert.True(world.Check("OE_S06", "Saint").Available);
    }

    [Fact]
    public void Broken_shelters_use_timeline_properties_before_general_properties()
    {
        var files = Files("ROOMS\nOE_S06 : ROOM : SHELTER\nEND ROOMS");
        files["world/oe/properties.txt"] = "Broken Shelters: Yellow: OE_S06";
        var world = DenWorldCatalog.Read(path => files.GetValueOrDefault(path));
        Assert.False(world.Check("OE_S06", "Yellow").Available);
        Assert.True(world.Check("OE_S06", "White").Available);
        files["world/oe/properties-yellow.txt"] = "Palette: 0";
        world = DenWorldCatalog.Read(path => files.GetValueOrDefault(path));
        Assert.True(world.Check("OE_S06", "Yellow").Available);
    }

    [Fact]
    public void Unverified_data_unknown_timelines_and_unindexed_rooms_are_not_offered()
    {
        var files = Files("ROOMS\nOE_S06 : ROOM : SHELTER\nEND ROOMS");
        files["world/indexmaps/roomindexmap2.txt"] = "0 OE_S01";
        var world = DenWorldCatalog.Read(path => files.GetValueOrDefault(path));
        Assert.False(world.Check("OE_S06", "White").Available);
        Assert.False(world.Check("OE_S06", "white").Available);
        Assert.False(world.Check("OE_S06", "Custom").Available);
        Assert.False(DenWorldCatalog.Unknown.Check("OE_S01", "White").Available);
        files.Remove("world/oe/properties.txt");
        world = DenWorldCatalog.Read(path => files.GetValueOrDefault(path));
        Assert.Contains("could not be verified", world.Check("OE_S01", "White").Reason);
    }

    [Fact]
    public void Changed_timeline_does_not_offer_rooms_in_replaced_map_regions()
    {
        var files = new Dictionary<string, string>
        {
            ["world/indexmaps/roomindexmap2.txt"] = "0 SS_S01",
            ["world/ss/world_ss.txt"] = "ROOMS\nSS_S01 : ROOM : SHELTER\nEND ROOMS",
            ["world/ss/properties.txt"] = "",
        };
        var world = DenWorldCatalog.Read(path => files.GetValueOrDefault(path));
        Assert.True(world.Check("SS_S01", "White").Available);
        Assert.False(world.Check("SS_S01", "Saint").Available);
        Assert.False(world.Check("SS_S01", "Rivulet").Available);
        Assert.Equal("White", DenWorldCatalog.EffectiveTimeline("White", ""));
        Assert.Equal("Saint", DenWorldCatalog.EffectiveTimeline("White", "Saint"));
    }

    private static Dictionary<string, string> Files(string world) => new()
    {
        ["world/indexmaps/roomindexmap2.txt"] = "0 OE_S01\n1 OE_S02\n2 OE_S03\n3 OE_S04\n4 OE_S05x\n5 OE_S06",
        ["world/oe/world_oe.txt"] = world,
        ["world/oe/properties.txt"] = "",
    };
}
