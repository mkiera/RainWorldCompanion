using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.Core.Saves.Models;

namespace RainWorldCompanion.Tests;

public class DevourmentTreeTests
{
    private static DevourmentRelationship Rel(
        string predType,
        string predId,
        string preyType,
        string preyId,
        bool preyIsItem = false,
        string status = "Held",
        int? food = 1) =>
        new(predType, preyType, status, food, preyIsItem, predId, preyId);

    [Fact]
    public void An_empty_list_builds_no_chains()
    {
        Assert.Empty(DevourmentTree.Build(Array.Empty<DevourmentRelationship>()));
        Assert.Empty(DevourmentTree.Build(null));
    }

    [Fact]
    public void A_predator_holding_two_things_is_one_root_with_two_contents()
    {
        var tree = DevourmentTree.Build(new[]
        {
            Rel("Slugcat", "ID.-1.0", "DataPearl", "ID.-1.10", preyIsItem: true, food: -1),
            Rel("Slugcat", "ID.-1.0", "PinkLizard", "ID.5.20", food: 6),
        });

        DevourmentNode root = Assert.Single(tree);
        Assert.Equal("Slugcat", root.Type);
        Assert.Equal("ID.-1.0", root.EntityId);
        Assert.Null(root.Status);
        Assert.Null(root.FoodValue);
        Assert.Equal(2, root.Contents.Count);
        Assert.Equal("DataPearl", root.Contents[0].Type);
        Assert.True(root.Contents[0].IsItem);
        Assert.Equal("PinkLizard", root.Contents[1].Type);
        Assert.Equal(6, root.Contents[1].FoodValue);
        Assert.Equal("Held", root.Contents[1].Status);
    }

    [Fact]
    public void Prey_that_is_itself_a_predator_becomes_a_branch()
    {
        var tree = DevourmentTree.Build(new[]
        {
            Rel("Slugcat", "ID.-1.0", "GreenLizard", "ID.5031.5490", food: 16),
            Rel("GreenLizard", "ID.5031.5490", "PinkLizard", "ID.5030.5489", status: "Healing", food: 4),
        });

        DevourmentNode root = Assert.Single(tree);
        Assert.Equal("Slugcat", root.Type);

        DevourmentNode lizard = Assert.Single(root.Contents);
        Assert.Equal("GreenLizard", lizard.Type);
        Assert.Equal(16, lizard.FoodValue);

        DevourmentNode inner = Assert.Single(lizard.Contents);
        Assert.Equal("PinkLizard", inner.Type);
        Assert.Equal("Healing", inner.Status);
        Assert.Equal(4, inner.FoodValue);
    }

    [Fact]
    public void The_root_is_whatever_nothing_else_is_holding_even_when_that_is_not_the_player()
    {
        // Hunter in the live save: a CyanLizard has swallowed the player, and the player is still
        // carrying things. The player must not be the root.
        var tree = DevourmentTree.Build(new[]
        {
            Rel("CyanLizard", "ID.4015.4466", "Slugcat", "ID.-1.0", food: 3),
            Rel("Slugcat", "ID.-1.0", "DataPearl", "ID.-1.4893", preyIsItem: true, food: -1),
            Rel("Slugcat", "ID.-1.0", "Spear", "ID.-301.4975", preyIsItem: true, food: -1),
        });

        DevourmentNode root = Assert.Single(tree);
        Assert.Equal("CyanLizard", root.Type);

        DevourmentNode player = Assert.Single(root.Contents);
        Assert.Equal("Slugcat", player.Type);
        Assert.Equal(2, player.Contents.Count);
    }

    [Fact]
    public void Several_unrelated_predators_each_get_their_own_root()
    {
        var tree = DevourmentTree.Build(new[]
        {
            Rel("Slugcat", "ID.-1.0", "Rock", "ID.-9.1", preyIsItem: true, food: -1),
            Rel("Vulture", "ID.7.7", "Spear", "ID.-9.2", preyIsItem: true, food: -1),
        });

        Assert.Equal(2, tree.Count);
        Assert.Equal("Slugcat", tree[0].Type);
        Assert.Equal("Vulture", tree[1].Type);
    }

    [Fact]
    public void Every_relationship_appears_exactly_once_however_deep_it_sits()
    {
        var rows = new[]
        {
            Rel("Slugcat", "ID.-1.0", "SpitLizard", "ID.2002.6583", food: 6),
            Rel("Slugcat", "ID.-1.0", "PinkLizard", "ID.24.6784", food: 4),
            Rel("SpitLizard", "ID.2002.6583", "PebblesPearl", "ID.-1.7660", preyIsItem: true, food: -1),
            Rel("SpitLizard", "ID.2002.6583", "DataPearl", "ID.-1.7907", preyIsItem: true, food: -1),
            Rel("PinkLizard", "ID.24.6784", "Spear", "ID.-19.7015", preyIsItem: true, food: -1),
        };

        var tree = DevourmentTree.Build(rows);

        DevourmentNode root = Assert.Single(tree);
        Assert.Equal(rows.Length, root.DescendantCount);
        Assert.Equal(rows.Length, CountNodes(tree) - 1);
    }

    [Fact]
    public void A_loop_is_broken_rather_than_followed_forever()
    {
        // Nothing should write this. If something does, the walk has to end.
        var tree = DevourmentTree.Build(new[]
        {
            Rel("A", "ID.1", "B", "ID.2"),
            Rel("B", "ID.2", "A", "ID.1"),
        });

        Assert.NotEmpty(tree);
        int nodes = CountNodes(tree);
        Assert.InRange(nodes, 2, 8);

        DevourmentNode? repeated = FindNode(tree, n => n.RepeatsAncestor);
        Assert.NotNull(repeated);
        Assert.Empty(repeated!.Contents);
    }

    [Fact]
    public void A_three_way_loop_still_terminates_and_keeps_every_row()
    {
        var tree = DevourmentTree.Build(new[]
        {
            Rel("A", "ID.1", "B", "ID.2"),
            Rel("B", "ID.2", "C", "ID.3"),
            Rel("C", "ID.3", "A", "ID.1"),
        });

        Assert.NotEmpty(tree);
        Assert.InRange(CountNodes(tree), 3, 12);
        Assert.Contains(AllNodes(tree), n => n.Type == "A");
        Assert.Contains(AllNodes(tree), n => n.Type == "B");
        Assert.Contains(AllNodes(tree), n => n.Type == "C");
    }

    [Fact]
    public void Rows_with_no_ids_stay_flat_instead_of_collapsing_together()
    {
        // This is what a manifest written before ids were recorded looks like.
        var tree = DevourmentTree.Build(new[]
        {
            new DevourmentRelationship("Slugcat", "PinkLizard", "Held", 6, false),
            new DevourmentRelationship("Slugcat", "DataPearl", "Held", -1, true),
            new DevourmentRelationship("SpitLizard", "PebblesPearl", "Held", -1, true),
        });

        Assert.Equal(3, tree.Count);
        Assert.All(tree, root => Assert.Single(root.Contents));
        Assert.Equal("", tree[0].EntityId);
    }

    [Fact]
    public void A_missing_prey_id_leaves_that_row_a_leaf_without_losing_it()
    {
        var tree = DevourmentTree.Build(new[]
        {
            new DevourmentRelationship("Slugcat", "GreenLizard", "Held", 16, false, "ID.-1.0", ""),
            Rel("Slugcat", "ID.-1.0", "Rock", "ID.-9.1", preyIsItem: true, food: -1),
        });

        DevourmentNode root = Assert.Single(tree);
        Assert.Equal(2, root.Contents.Count);
        Assert.All(root.Contents, c => Assert.Empty(c.Contents));
    }

    [Fact]
    public void Contents_keep_the_order_the_save_listed_them_in()
    {
        var tree = DevourmentTree.Build(new[]
        {
            Rel("Slugcat", "ID.-1.0", "First", "ID.a", preyIsItem: true, food: -1),
            Rel("Slugcat", "ID.-1.0", "Second", "ID.b", preyIsItem: true, food: -1),
            Rel("Slugcat", "ID.-1.0", "Third", "ID.c", preyIsItem: true, food: -1),
        });

        DevourmentNode root = Assert.Single(tree);
        Assert.Equal(new[] { "First", "Second", "Third" }, root.Contents.Select(c => c.Type).ToArray());
    }

    [Fact]
    public void Sav3_builds_the_chain_the_real_save_describes()
    {
        SlotMetadata slot = SaveMetadataExtractor.Extract(FixtureFiles.PathTo("sav3.bin"), 3);
        CampaignSummary campaign = Assert.Single(slot.Campaigns);

        var tree = DevourmentTree.Build(campaign.DevourmentStates);

        DevourmentNode root = Assert.Single(tree);
        Assert.Equal("Slugcat", root.Type);
        Assert.Equal(3, root.Contents.Count);
        Assert.All(root.Contents, c => Assert.Equal("GreenLizard", c.Type));

        // One of those green lizards is itself holding a pink lizard.
        DevourmentNode carrier = Assert.Single(root.Contents, c => c.HasContents);
        DevourmentNode inner = Assert.Single(carrier.Contents);
        Assert.Equal("PinkLizard", inner.Type);
        Assert.Equal("Healing", inner.Status);

        // and every stored relationship is represented
        Assert.Equal(campaign.DevourmentStates.Count, root.DescendantCount);
    }

    private static IEnumerable<DevourmentNode> AllNodes(IEnumerable<DevourmentNode> nodes)
    {
        foreach (DevourmentNode node in nodes)
        {
            yield return node;
            foreach (DevourmentNode child in AllNodes(node.Contents))
            {
                yield return child;
            }
        }
    }

    private static int CountNodes(IEnumerable<DevourmentNode> nodes) => AllNodes(nodes).Count();

    private static DevourmentNode? FindNode(IEnumerable<DevourmentNode> nodes, Func<DevourmentNode, bool> match) =>
        AllNodes(nodes).FirstOrDefault(match);
}
