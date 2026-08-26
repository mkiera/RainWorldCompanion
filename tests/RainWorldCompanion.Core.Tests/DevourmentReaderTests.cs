using RainWorldCompanion.Core.Saves.Models;

namespace RainWorldCompanion.Tests;

/// <summary>
/// DEVOURMENTSTATE is the mod's own field: one per relationship, four parts split on &lt;dvD&gt;.
/// The prey is a serialised creature or item, told apart by the "ID." prefix.
/// </summary>
public class DevourmentReaderTests
{
    private const string Predator = "Slugcat<cA>ID.-1.0<cB>0<cA>VS_S01.0<cA>";

    [Fact]
    public void A_creature_prey_reports_the_type_before_the_first_creature_separator()
    {
        var relationship = Only(
            Predator,
            "PinkLizard<cA>ID.5030.5489<cB>0<cA>SU_S04.0<cA>Social<cC>REL<rA>ID.-1.0<rA>L<rB>1<cB>",
            "Held",
            "6");

        Assert.Equal("Slugcat", relationship.PredatorType);
        Assert.Equal("PinkLizard", relationship.PreyType);
        Assert.Equal("Held", relationship.Status);
        Assert.Equal(6, relationship.FoodValue);
        Assert.False(relationship.PreyIsItem);
    }

    [Fact]
    public void An_item_prey_reports_the_type_at_index_one_of_the_object_split()
    {
        var relationship = Only(
            Predator,
            "ID.-2588.11856<oB>0<oA>DataPearl<oA>WRFA_S01.22.4.0",
            "Held",
            "-1");

        Assert.Equal("Slugcat", relationship.PredatorType);
        Assert.Equal("DataPearl", relationship.PreyType);
        Assert.Equal("Held", relationship.Status);
        Assert.Equal(-1, relationship.FoodValue);
        Assert.True(relationship.PreyIsItem);
    }

    [Fact]
    public void A_held_spear_is_an_item_as_well()
    {
        var relationship = Only(
            Predator,
            "ID.-2588.11857<oB>0<oA>Spear<oA>WRFA_S01.22.4.0<oA>-1<oA>-1",
            "Held",
            "-1");

        Assert.Equal("Spear", relationship.PreyType);
        Assert.Equal(-1, relationship.FoodValue);
        Assert.True(relationship.PreyIsItem);
    }

    [Fact]
    public void The_prey_shape_alone_decides_whether_it_is_an_item()
    {
        var campaign = CampaignFixture.Campaign(
            CampaignFixture.Field("SAV STATE NUMBER", "Yellow"),
            CampaignFixture.Devourment(Predator, CampaignFixture.Creature("PinkLizard", "ID.5030.5489"), "Held", "6"),
            CampaignFixture.Devourment(Predator, CampaignFixture.Item("Rock", "ID.-2588.11856"), "Held", "-1"));

        Assert.Equal(2, campaign.DevourmentStates.Count);
        Assert.False(campaign.DevourmentStates[0].PreyIsItem);
        Assert.True(campaign.DevourmentStates[1].PreyIsItem);
    }

    [Fact]
    public void A_value_that_does_not_split_into_four_parts_is_skipped_rather_than_thrown_on()
    {
        var campaign = CampaignFixture.Campaign(
            CampaignFixture.Field("SAV STATE NUMBER", "Yellow"),
            CampaignFixture.Field("DEVOURMENTSTATE", Predator + CampaignFixture.DevourmentSeparator + "PinkLizard<cA>ID.1.2<cB>"),
            CampaignFixture.Devourment(Predator, CampaignFixture.Creature("PinkLizard", "ID.5030.5489"), "Held", "6"));

        var relationship = Assert.Single(campaign.DevourmentStates);
        Assert.Equal("PinkLizard", relationship.PreyType);
        Assert.Equal(6, relationship.FoodValue);
    }

    /// <summary>
    /// The count and the list are different numbers: when every field fails to parse the record
    /// still holds Devourment state, this build just cannot read its shape.
    /// </summary>
    [Fact]
    public void Fields_that_all_fail_to_parse_still_count_towards_the_recorded_total()
    {
        var unreadable = Predator + CampaignFixture.DevourmentSeparator + "PinkLizard<cA>ID.1.2<cB>";

        var campaign = CampaignFixture.Campaign(
            CampaignFixture.Field("SAV STATE NUMBER", "Yellow"),
            CampaignFixture.Field("DEVOURMENTSTATE", unreadable),
            CampaignFixture.Field("DEVOURMENTSTATE", unreadable));

        Assert.Equal(2, campaign.DevourmentStateCount);
        Assert.Empty(campaign.DevourmentStates);
    }

    [Fact]
    public void An_empty_devourment_value_is_skipped()
    {
        var campaign = CampaignFixture.Campaign(
            CampaignFixture.Field("SAV STATE NUMBER", "Yellow"),
            CampaignFixture.Field("DEVOURMENTSTATE", ""),
            "DEVOURMENTSTATE");

        Assert.Empty(campaign.DevourmentStates);
    }

    [Fact]
    public void Every_belly_status_the_mod_writes_survives_the_round_trip()
    {
        foreach (var status in CampaignDetailTests.BellyStatusNames)
        {
            var relationship = Only(
                Predator,
                CampaignFixture.Creature("PinkLizard", "ID.5030.5489"),
                status,
                "6");

            Assert.Equal(status, relationship.Status);
        }
    }

    [Fact]
    public void A_predator_that_is_not_the_slugcat_is_reported_as_itself()
    {
        var relationship = Only(
            CampaignFixture.Creature("GreenLizard", "ID.5031.5490"),
            CampaignFixture.Creature("PinkLizard", "ID.5030.5489"),
            "Healing",
            "4");

        Assert.Equal("GreenLizard", relationship.PredatorType);
        Assert.Equal("PinkLizard", relationship.PreyType);
    }

    private static DevourmentRelationship Only(string predator, string prey, string status, string foodValue)
    {
        var campaign = CampaignFixture.Campaign(
            CampaignFixture.Field("SAV STATE NUMBER", "Yellow"),
            CampaignFixture.Devourment(predator, prey, status, foodValue));

        Assert.Equal(1, campaign.DevourmentStateCount);
        return Assert.Single(campaign.DevourmentStates);
    }
}
