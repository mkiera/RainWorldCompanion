using RainWorldSaveManager.Core.Editing;

namespace RainWorldSaveManager.Core.Tests;

/// <summary>
/// Handing out entity ids that do not collide with one already in the campaign.
///
/// The number half of an id is the whole of its identity to the game, so these are mostly about
/// what the counter alone would miss.
/// </summary>
public class EntityIdAllocatorTests
{
    private const string Body = "SAV STATE NUMBER<svB>White<svA>NEXTID<svB>4210<svA>DENPOS<svB>SU_S01<svA>";

    [Fact]
    public void The_first_id_is_one_past_the_counter_the_game_left()
    {
        var allocator = EntityIdAllocator.ForRecord(Body);

        Assert.Equal("ID.-1.4211", allocator.Allocate());
        Assert.Equal(4210, allocator.StoredNextId);
    }

    [Fact]
    public void Each_id_is_one_past_the_last()
    {
        var allocator = EntityIdAllocator.ForRecord(Body);

        Assert.Equal(
            new[] { "ID.-1.4211", "ID.-1.4212", "ID.-1.4213" },
            new[] { allocator.Allocate(), allocator.Allocate(), allocator.Allocate() });

        Assert.Equal(3, allocator.Issued);
    }

    /// <summary>
    /// The counter is what the game left, not a promise about what is in the record. A save another
    /// tool has touched can hold a number above it, and issuing that number again would give two
    /// entities one identity.
    /// </summary>
    [Fact]
    public void An_id_already_in_the_record_is_never_handed_out_again()
    {
        string body = Body
            + "DEVOURMENTSTATE<svB>Slugcat<cA>ID.-1.0<cB>0<cA>SU_S01.0<cA><dvD>PinkLizard<cA>ID.9999.9998<cB>0"
            + "<cA>SU_S01.0<cA><dvD>Held<dvD>7<svA>";

        var allocator = EntityIdAllocator.ForRecord(body);

        Assert.Equal(9998, allocator.HighestSeen);
        Assert.Equal("ID.-1.9999", allocator.Allocate());
    }

    [Fact]
    public void Ids_are_found_in_any_field_not_only_the_ones_this_app_models()
    {
        string body = Body + "SOMEMODFIELD<svB>whatever ID.7.55000 whatever<svA>";

        Assert.Equal(55000, EntityIdAllocator.ForRecord(body).HighestSeen);
    }

    [Fact]
    public void The_counter_wins_when_it_is_above_everything_in_the_record()
    {
        string body = Body + "FRIENDS<svB>PinkLizard<cA>ID.4.12<cB>0<cA>SU_S01.0<cA><svC><svA>";

        var allocator = EntityIdAllocator.ForRecord(body);

        Assert.Equal(12, allocator.HighestSeen);
        Assert.Equal("ID.-1.4211", allocator.Allocate());
    }

    /// <summary>
    /// A campaign with no counter still gets ids, counting up from the highest one it holds. That
    /// is unique within this save, which is what stops an edit corrupting the campaign. It is not
    /// agreement with the game, which picks a random counter when it finds none, so the caller is
    /// told and can say so.
    /// </summary>
    [Fact]
    public void A_campaign_with_no_counter_still_gets_ids_and_says_the_counter_was_missing()
    {
        var allocator = EntityIdAllocator.ForRecord("SAV STATE NUMBER<svB>White<svA>FOOD<svB>4<svA>");

        Assert.True(allocator.CounterWasMissing);
        Assert.Null(allocator.StoredNextId);
        Assert.Equal("ID.-1.1", allocator.Allocate());
    }

    [Fact]
    public void A_counter_that_is_not_a_number_counts_as_missing()
    {
        var allocator = EntityIdAllocator.ForRecord("NEXTID<svB>abc<svA>DENPOS<svB>SU_S01<svA>");

        Assert.True(allocator.CounterWasMissing);
        Assert.Equal("ID.-1.1", allocator.Allocate());
    }

    // ---- writing the counter back ----

    [Fact]
    public void The_counter_written_back_is_where_the_game_should_carry_on_from()
    {
        var allocator = EntityIdAllocator.ForRecord(Body);
        allocator.Allocate();
        allocator.Allocate();

        string body = allocator.WriteCounter(Body);

        Assert.Equal("4212", DelimitedFields.Record.GetValue(body, "NEXTID"));
        Assert.Equal(4212, allocator.NextIdToWrite);
    }

    [Fact]
    public void Writing_the_counter_leaves_every_other_field_where_it_was()
    {
        var allocator = EntityIdAllocator.ForRecord(Body);
        allocator.Allocate();

        string body = allocator.WriteCounter(Body);

        Assert.Equal("White", DelimitedFields.Record.GetValue(body, "SAV STATE NUMBER"));
        Assert.Equal("SU_S01", DelimitedFields.Record.GetValue(body, "DENPOS"));
    }

    [Fact]
    public void A_campaign_with_no_counter_gets_one_written_into_it()
    {
        string start = "SAV STATE NUMBER<svB>White<svA>FOOD<svB>4<svA>";
        var allocator = EntityIdAllocator.ForRecord(start);
        allocator.Allocate();

        Assert.Equal("1", DelimitedFields.Record.GetValue(allocator.WriteCounter(start), "NEXTID"));
    }

    [Fact]
    public void Handing_out_nothing_leaves_the_counter_where_it_was()
    {
        var allocator = EntityIdAllocator.ForRecord(Body);

        Assert.Equal(Body, allocator.WriteCounter(Body));
    }
}
