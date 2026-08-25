using RainWorldCompanion.Core.Editing;
using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Tests;

/// <summary>
/// Building the strings the game writes for one abstract object.
///
/// The blobs quoted here are the game's own, taken from a played save. That matters more for items
/// than it did for creatures: the game's reader special-cases twenty types, each taking its own
/// fields at its own indices, and the whole method sits inside a try. A blob short of a field is
/// not reported, it is dropped, so the only way to know a tail is right is to compare it against
/// one the game wrote.
/// </summary>
public class ItemBlobBuilderTests
{
    // ---- real blobs, from a played save ----

    private const string RealRock = "ID.-1759.8243<oB>0<oA>Rock<oA>SL_S06.23.18.0";

    private const string RealPearl = "ID.-1.7631<oB>0<oA>PebblesPearl<oA>SL_S06.23.18.0<oA>-1<oA>-1<oA>PebblesPearl<oA>0<oA>1";

    private const string RealSpear = "ID.-153.7905<oB>0<oA>Spear<oA>SL_S06.23.18.0<oA>0<oA>0<oA>0<oA>0<oA>0<oA>0<oA>0<oA>0";

    private const string RealWaterNut = "ID.-1.9110<oB>0<oA>WaterNut<oA>SL_S06.23.18.0<oA>534<oA>5<oA>0";

    // ---- building ----

    [Fact]
    public void An_item_with_nothing_after_its_position_is_built_the_way_the_game_writes_a_rock()
        => Assert.Equal(
            "ID.-1759.8243<oB>0<oA>Rock<oA>SL_S06.23.18.0",
            ItemBlobBuilder.Build("Rock", "ID.-1759.8243", "SL_S06", x: 23, y: 18));

    /// <summary>
    /// A spear carries eight fields the reader takes one at a time, and every one of them has to be
    /// there or it throws while unpacking. A real save writes them all as zero for a plain spear.
    /// </summary>
    [Fact]
    public void A_spear_is_built_with_all_eight_of_the_fields_the_reader_takes()
    {
        string blob = ItemBlobBuilder.Build("Spear", "ID.-153.7905", "SL_S06", x: 23, y: 18);

        Assert.Equal(RealSpear, blob);
        Assert.Equal(8, ItemBlobBuilder.Parse(blob)!.Tail.Count);
    }

    [Fact]
    public void A_pebbles_pearl_is_built_with_the_field_the_reader_skips_still_in_place()
    {
        string blob = ItemBlobBuilder.Build("PebblesPearl", "ID.-1.7631", "SL_S06", x: 23, y: 18);

        Assert.Equal(RealPearl, blob);

        // The reader takes colour at 6 and number at 7, so the slot at 5 has to be filled even
        // though nothing reads it for this type.
        Assert.Equal(5, ItemBlobBuilder.Parse(blob)!.Tail.Count);
    }

    [Fact]
    public void An_item_never_placed_in_a_level_says_so_the_way_a_carried_pearl_does()
    {
        string blob = ItemBlobBuilder.Build("DataPearl", "ID.-1.9", "SU_S01");

        Assert.Equal("ID.-1.9<oB>0<oA>DataPearl<oA>SU_S01.-1.-1.0<oA>-1<oA>-1<oA>Misc", blob);
    }

    [Fact]
    public void A_consumable_gets_the_two_fields_its_branch_of_the_reader_takes()
    {
        string blob = ItemBlobBuilder.Build("PuffBall", "ID.-1.4354", "SI_C03");

        Assert.Equal(new[] { "-1", "-1" }, ItemBlobBuilder.Parse(blob)!.Tail);
    }

    [Fact]
    public void An_item_this_app_has_never_heard_of_is_built_as_the_base_alone()
    {
        string blob = ItemBlobBuilder.Build("SomeModItem", "ID.-1.4", "SU_S01");

        Assert.Equal("ID.-1.4<oB>0<oA>SomeModItem<oA>SU_S01.-1.-1.0", blob);
        Assert.Empty(ItemBlobBuilder.Parse(blob)!.Tail);
    }

    [Fact]
    public void An_item_is_put_where_a_creature_is()
    {
        CreatureBlob predator = CreatureBlobBuilder.Parse(
            CreatureBlobBuilder.Build("Slugcat", "ID.-1.0", "HI_S03", node: 2))!;

        string blob = ItemBlobBuilder.BuildBeside("Rock", "ID.-1.5", predator);

        Assert.Equal("ID.-1.5<oB>0<oA>Rock<oA>HI_S03.-1.-1.2", blob);
    }

    // ---- reading it back ----

    [Fact]
    public void A_real_blob_taken_apart_and_put_back_together_is_the_same_string()
    {
        foreach (string blob in new[] { RealRock, RealPearl, RealSpear, RealWaterNut })
        {
            Assert.Equal(blob, ItemBlobBuilder.ToBlob(ItemBlobBuilder.Parse(blob)!));
        }
    }

    [Fact]
    public void A_real_blob_is_taken_apart_into_the_pieces_the_game_wrote()
    {
        ItemBlob parsed = ItemBlobBuilder.Parse(RealWaterNut)!;

        Assert.Equal("WaterNut", parsed.Type);
        Assert.Equal("ID.-1.9110", parsed.EntityId);
        Assert.Equal(0, parsed.RippleLayer);
        Assert.Equal("SL_S06.23.18.0", parsed.Position);
        Assert.Equal(new[] { "534", "5", "0" }, parsed.Tail);
    }

    [Fact]
    public void What_is_built_reads_back_as_the_item_it_was_built_from()
    {
        string blob = ItemBlobBuilder.Build("Rock", "ID.-2588.11856", "WRFA_S01");

        Assert.Equal("Rock", DevourmentReader.ItemTypeOf(blob));
        Assert.Equal("ID.-2588.11856", DevourmentReader.ItemIdOf(blob));
    }

    [Fact]
    public void A_blob_too_short_for_the_games_own_reader_is_left_alone()
    {
        Assert.Null(ItemBlobBuilder.Parse("ID.-1.4<oB>0<oA>Rock"));
        Assert.Null(ItemBlobBuilder.Parse(""));
        Assert.Null(ItemBlobBuilder.Parse(null));
        Assert.Equal("nonsense", ItemBlobBuilder.WithRoom("nonsense", "HI_S03"));
    }

    // ---- moving ----

    [Fact]
    public void Moving_an_item_keeps_the_rest_of_where_it_was()
    {
        string moved = ItemBlobBuilder.WithRoom(RealWaterNut, "HI_S01");

        Assert.Equal("HI_S01.23.18.0", ItemBlobBuilder.Parse(moved)!.Position);
        Assert.Equal(new[] { "534", "5", "0" }, ItemBlobBuilder.Parse(moved)!.Tail);
    }

    [Fact]
    public void The_room_is_read_off_an_item_without_its_three_numbers()
    {
        Assert.Equal("SL_S06", ItemBlobBuilder.RoomOf(RealRock));
        Assert.Equal("", ItemBlobBuilder.RoomOf("nonsense"));
    }

    /// <summary>
    /// A creature writes its room and node as two parts while an item writes four, so a room name
    /// carrying a dot has to be told from the coordinates by counting from the end.
    /// </summary>
    [Fact]
    public void A_room_name_with_a_dot_in_it_survives()
    {
        string blob = ItemBlobBuilder.Build("Rock", "ID.-1.4", "SOME.ROOM", x: 1, y: 2, node: 3);

        Assert.Equal("SOME.ROOM", ItemBlobBuilder.RoomOf(blob));
        Assert.Equal("OTHER.ROOM.1.2.3", ItemBlobBuilder.Parse(ItemBlobBuilder.WithRoom(blob, "OTHER.ROOM"))!.Position);
    }

    // ---- the catalog behind it ----

    [Fact]
    public void The_items_a_played_save_actually_holds_are_all_buildable()
    {
        foreach (string type in new[] { "Rock", "Spear", "DataPearl", "PebblesPearl", "WaterNut", "PuffBall", "NSHSwarmer" })
        {
            Assert.True(ObjectCatalog.IsKnown(type), type);
            Assert.True(ObjectCatalog.ForName(type).CanBuild, type);
        }
    }

    /// <summary>
    /// Two types read their own fields somewhere this app has not followed, so it says it cannot
    /// build one rather than writing something short and letting the game drop it in silence.
    /// </summary>
    [Fact]
    public void The_two_types_whose_fields_are_not_pinned_say_they_cannot_be_built()
    {
        Assert.False(ObjectCatalog.ForName("CollisionField").CanBuild);
        Assert.False(ObjectCatalog.ForName("PrinceBulb").CanBuild);
    }

    [Fact]
    public void An_item_the_catalog_has_never_heard_of_cannot_be_built_either()
    {
        ObjectKind kind = ObjectCatalog.ForName("SomeModItem");

        Assert.Equal("SomeModItem", kind.Name);
        Assert.False(kind.CanBuild);
        Assert.False(ObjectCatalog.IsKnown("SomeModItem"));
    }

    [Fact]
    public void The_expansions_items_are_there_with_the_expansion_named()
    {
        Assert.Equal(CreatureSource.Downpour, ObjectCatalog.ForName("GooieDuck").Source);
        Assert.Equal(CreatureSource.Downpour, ObjectCatalog.ForName("Spearmasterpearl").Source);
        Assert.Equal(CreatureSource.Watcher, ObjectCatalog.ForName("Boomerang").Source);
    }

    [Fact]
    public void Searching_puts_what_starts_with_the_query_first()
    {
        var matches = ObjectCatalog.Search("Pearl").ToList();

        Assert.Contains(matches, kind => kind.Name == "PebblesPearl");
        Assert.Contains(matches, kind => kind.Name == "DataPearl");
    }

    [Fact]
    public void An_empty_search_offers_the_whole_list()
        => Assert.Equal(ObjectCatalog.Known.Count, ObjectCatalog.Search("").Count());

    [Fact]
    public void No_name_is_in_the_list_twice()
        => Assert.Equal(
            ObjectCatalog.Known.Count,
            ObjectCatalog.Known.Select(kind => kind.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
}
