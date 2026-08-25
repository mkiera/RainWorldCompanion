using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.Core.Saves.Models;

namespace RainWorldCompanion.Tests;

/// <summary>
/// The detail inside a serialized creature or object. Every blob quoted here was taken verbatim
/// from the live save folder, so these assert against shapes the game really writes.
/// </summary>
public class EntityBlobReaderTests
{
    private const string TamedSpitLizard =
        "SpitLizard<cA>ID.2002.6583<cB>0<cA>VS_S01.0<cA>Social<cC>REL<rA>ID.-1.0<rA>L<rB>1<rA>K<rB>0.9705502<cB>";

    private const string DislikedPinkLizard =
        "PinkLizard<cA>ID.21039.8104<cB>0<cA>VS_S01.0<cA>Social<cC>REL<rA>ID.-1.0<rA>L<rB>0.07035797<rA>K<rB>0.08272603<cB>";

    private const string PartlyEatenPinkLizard =
        "PinkLizard<cA>ID.24.6784<cB>0<cA>VS_S01.0<cA>Social<cC>REL<rA>ID.-1.0<rA>L<rB>0.908127<rA>K<rB>0.870455<cB>MeatLeft<cC>5<cB>";

    private const string VultureGrubNoSocial = "VultureGrub<cA>ID.-1.6863<cB>0<cA>VS_S01.0<cA>";

    private const string LorePearl = "ID.-1.7549<oB>0<oA>DataPearl<oA>VS_S01.35.12.0<oA>662<oA>0<oA>UW";
    private const string MiscPearl = "ID.-1.7966<oB>0<oA>DataPearl<oA>VS_S01.35.12.0<oA>126<oA>6<oA>BroadcastMisc";
    private const string PebblesPearl =
        "ID.-1.7660<oB>0<oA>PebblesPearl<oA>VS_S01.35.12.0<oA>-1<oA>-1<oA>PebblesPearl<oA>0<oA>30";
    private const string PlainSpear =
        "ID.-153.7905<oB>0<oA>Spear<oA>VS_S01.35.12.0<oA>0<oA>0<oA>0<oA>0<oA>0<oA>0<oA>0<oA>0";
    private const string PlainRock = "ID.-2588.11856<oB>0<oA>Rock<oA>WRFA_S01.22.4.0";

    [Fact]
    public void A_creature_that_likes_the_player_reports_its_like_and_know()
    {
        DevourmentEntity entity = EntityBlobReader.ReadCreature(TamedSpitLizard, "ID.2002.6583", "SpitLizard");

        Assert.NotNull(entity.TowardPlayer);
        SocialRelationship toward = entity.TowardPlayer!;
        Assert.Equal(1f, toward.Like!.Value, 4);
        Assert.Equal(0.9705502f, toward.Know!.Value, 4);
        Assert.Null(toward.Fear);
        Assert.Equal(DevourmentEntity.PlayerEntityId, toward.SubjectId);
    }

    [Fact]
    public void A_creature_that_barely_knows_the_player_still_reports_a_low_like()
    {
        DevourmentEntity entity = EntityBlobReader.ReadCreature(DislikedPinkLizard, "ID.21039.8104", "PinkLizard");

        Assert.Equal(0.07035797f, entity.TowardPlayer!.Like!.Value, 5);
    }

    [Fact]
    public void A_creature_with_no_social_block_reports_nothing_rather_than_zero()
    {
        DevourmentEntity entity = EntityBlobReader.ReadCreature(VultureGrubNoSocial, "ID.-1.6863", "VultureGrub");

        Assert.Empty(entity.Social);
        Assert.Null(entity.TowardPlayer);
        Assert.Null(entity.MeatLeft);
    }

    [Fact]
    public void A_partly_eaten_creature_reports_the_meat_it_has_left()
    {
        DevourmentEntity entity = EntityBlobReader.ReadCreature(PartlyEatenPinkLizard, "ID.24.6784", "PinkLizard");

        Assert.Equal(5, entity.MeatLeft);
        Assert.Equal(0.908127f, entity.TowardPlayer!.Like!.Value, 5);
    }

    [Fact]
    public void A_creature_can_remember_more_than_one_entity()
    {
        // <smA> separates one relationship from the next. Two green lizards in sav3 carry two each.
        const string twoRelationships =
            "GreenLizard<cA>ID.5031.5490<cB>0<cA>SU_S04.0<cA>Social<cC>"
            + "REL<rA>ID.-1.0<rA>L<rB>1<rA>K<rB>0.9<smA>REL<rA>ID.5030.5489<rA>L<rB>-0.5<rA>F<rB>0.25<cB>";

        DevourmentEntity entity = EntityBlobReader.ReadCreature(twoRelationships, "ID.5031.5490", "GreenLizard");

        Assert.Equal(2, entity.Social.Count);
        Assert.Equal(1f, entity.TowardPlayer!.Like!.Value, 4);

        SocialRelationship other = entity.Social.Single(r => r.SubjectId == "ID.5030.5489");
        Assert.Equal(-0.5f, other.Like!.Value, 4);
        Assert.Equal(0.25f, other.Fear!.Value, 4);
        Assert.Null(other.Know);
    }

    [Fact]
    public void A_lore_pearl_reports_its_type_and_the_catalog_knows_its_colour()
    {
        DevourmentEntity entity = EntityBlobReader.ReadItem(LorePearl, "ID.-1.7549", "DataPearl");

        Assert.Equal("UW", entity.PearlType);

        Assert.NotNull(PearlCatalog.ForId(entity.PearlType));
        PearlCatalog.PearlInfo info = PearlCatalog.ForId(entity.PearlType)!;
        Assert.True(info.IsLore);
        Assert.Equal("#669966", info.ColorHex);
    }

    [Fact]
    public void A_broadcast_pearl_is_read_but_is_not_lore()
    {
        DevourmentEntity entity = EntityBlobReader.ReadItem(MiscPearl, "ID.-1.7966", "DataPearl");

        Assert.Equal("BroadcastMisc", entity.PearlType);
        Assert.False(PearlCatalog.ForId(entity.PearlType)!.IsLore);
    }

    [Fact]
    public void A_pebbles_pearl_reports_the_number_that_tells_one_from_another()
    {
        DevourmentEntity entity = EntityBlobReader.ReadItem(PebblesPearl, "ID.-1.7660", "PebblesPearl");

        Assert.Equal("PebblesPearl", entity.PearlType);
        Assert.Equal(30, entity.PebblesPearlNumber);
    }

    [Fact]
    public void An_ordinary_spear_reports_no_special_state()
    {
        DevourmentEntity entity = EntityBlobReader.ReadItem(PlainSpear, "ID.-153.7905", "Spear");

        Assert.NotNull(entity.Spear);
        SpearState spear = entity.Spear!;
        Assert.False(spear.IsSpecial);
        Assert.False(spear.Explosive);
        Assert.False(spear.Electric);
        Assert.False(spear.Needle);
    }

    [Theory]
    // stuckInWallCycles, explosive, hue, electric, electricCharge, needle, poison, poisonHue
    [InlineData("0<oA>1<oA>0<oA>0<oA>0<oA>0<oA>0<oA>0", true, false, false)]
    [InlineData("0<oA>0<oA>0<oA>1<oA>3<oA>0<oA>0<oA>0", false, true, false)]
    [InlineData("0<oA>0<oA>0<oA>0<oA>0<oA>1<oA>0<oA>0", false, false, true)]
    public void A_special_spear_reports_which_kind_it_is(
        string tail, bool explosive, bool electric, bool needle)
    {
        string blob = "ID.-1.1<oB>0<oA>Spear<oA>SU_S04.1.1.0<oA>" + tail;

        DevourmentEntity entity = EntityBlobReader.ReadItem(blob, "ID.-1.1", "Spear");
        Assert.NotNull(entity.Spear);
        SpearState spear = entity.Spear!;

        Assert.Equal(explosive, spear.Explosive);
        Assert.Equal(electric, spear.Electric);
        Assert.Equal(needle, spear.Needle);
        Assert.True(spear.IsSpecial);
    }

    [Fact]
    public void A_spear_written_by_an_older_version_that_stops_short_does_not_throw()
    {
        string blob = "ID.-1.1<oB>0<oA>Spear<oA>SU_S04.1.1.0<oA>0";

        DevourmentEntity entity = EntityBlobReader.ReadItem(blob, "ID.-1.1", "Spear");
        Assert.NotNull(entity.Spear);

        Assert.False(entity.Spear!.IsSpecial);
    }

    [Fact]
    public void An_item_that_is_neither_pearl_nor_spear_reports_neither()
    {
        DevourmentEntity entity = EntityBlobReader.ReadItem(PlainRock, "ID.-2588.11856", "Rock");

        Assert.Null(entity.PearlType);
        Assert.Null(entity.Spear);
        Assert.True(entity.IsItem);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("total garbage with no separators")]
    [InlineData("<cA><cA><cA>")]
    [InlineData("Social<cC>REL<rA>")]
    [InlineData("Social<cC>REL<rA>ID.1<rA>L<rB>not-a-number<cB>")]
    public void Malformed_input_yields_an_empty_entity_rather_than_throwing(string? blob)
    {
        DevourmentEntity creature = EntityBlobReader.ReadCreature(blob, "ID.1", "Thing");
        DevourmentEntity item = EntityBlobReader.ReadItem(blob, "ID.1", "Thing");

        Assert.NotNull(creature);
        Assert.NotNull(item);
        Assert.NotNull(creature.Social);
    }

    [Fact]
    public void Friend_ids_come_out_of_the_friends_field()
    {
        string friends = TamedSpitLizard + "<svC>" + PartlyEatenPinkLizard + "<svC>";

        var ids = EntityBlobReader.ReadFriendIds(friends);

        Assert.Equal(new[] { "ID.2002.6583", "ID.24.6784" }, ids.ToArray());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<svC><svC>")]
    public void An_empty_friends_field_yields_no_ids(string? friends) =>
        Assert.Empty(EntityBlobReader.ReadFriendIds(friends));

    [Fact]
    public void The_pearl_catalog_covers_every_pearl_with_a_real_colour()
    {
        Assert.Equal(52, PearlCatalog.All.Count);
        Assert.All(PearlCatalog.All, p => Assert.Matches("^#[0-9A-F]{6}$", p.ColorHex));

        // Spot checks against the values the game's own UniquePearlMainColor returned.
        Assert.Equal("#E5F233", PearlCatalog.ForId("SL_moon")!.ColorHex);
        Assert.Equal("#0232FF", PearlCatalog.ForId("HI")!.ColorHex);
        Assert.True(PearlCatalog.ForId("SL_moon")!.IsLore);
        Assert.False(PearlCatalog.ForId("Misc")!.IsLore);
    }

    [Fact]
    public void An_unknown_pearl_id_is_not_in_the_catalog_and_does_not_throw()
    {
        Assert.Null(PearlCatalog.ForId("SomeModPearl"));
        Assert.Null(PearlCatalog.ForId(null));
        Assert.Null(PearlCatalog.ForId("   "));
    }

    [Fact]
    public void Pearl_lookup_ignores_case()
    {
        Assert.Equal("SL_moon", PearlCatalog.ForId("sl_MOON")!.Id);
    }
}
