// RainWorldCompanion.Core.System exists in this assembly, so a using written inside the namespace
// body would bind "System" to that namespace instead of the BCL root.
using System.Globalization;
using RainWorldCompanion.Core.Saves.Models;

namespace RainWorldCompanion.Core.Saves;

/// <summary>
/// Turns the flat list of DEVOURMENTSTATE relationships into the stomach chains they describe. The
/// mod writes one row per predator and prey pair, so a creature swallowed while itself carrying
/// something appears twice under the same entity id, and following those ids rebuilds the nesting.
/// </summary>
public static class DevourmentTree
{
    /// <summary>A row with no usable predator id gets a key of its own from this prefix and its
    /// position. The leading space keeps it from colliding with a real id.</summary>
    private const string UnlinkedKeyPrefix = " unlinked:";

    /// <summary>Every relationship reaches the result exactly once, including ones the ids cannot
    /// place, so nothing is dropped for being malformed.</summary>
    /// <param name="friendIds">Optional. Without it nothing is marked as tamed, which is what a
    /// manifest written before FRIENDS was recorded gives.</param>
    public static IReadOnlyList<DevourmentNode> Build(
        IReadOnlyList<DevourmentRelationship>? relationships,
        IReadOnlyCollection<string>? friendIds = null)
    {
        if (relationships is null || relationships.Count == 0)
        {
            return Array.Empty<DevourmentNode>();
        }

        var friends = friendIds is null || friendIds.Count == 0
            ? null
            : new HashSet<string>(friendIds, StringComparer.Ordinal);

        var byPredator = new Dictionary<string, List<DevourmentRelationship>>(StringComparer.Ordinal);
        var predatorOrder = new List<string>();
        var preyIds = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < relationships.Count; i++)
        {
            DevourmentRelationship row = relationships[i];

            string predatorKey = row.PredatorId.Length > 0
                ? row.PredatorId
                : UnlinkedKeyPrefix + i.ToString(CultureInfo.InvariantCulture);

            if (!byPredator.TryGetValue(predatorKey, out List<DevourmentRelationship>? carried))
            {
                carried = new List<DevourmentRelationship>();
                byPredator.Add(predatorKey, carried);
                predatorOrder.Add(predatorKey);
            }

            carried.Add(row);

            if (row.PreyId.Length > 0)
            {
                preyIds.Add(row.PreyId);
            }
        }

        var roots = new List<DevourmentNode>();
        var placed = new HashSet<string>(StringComparer.Ordinal);

        // A root is a predator that nothing else is holding.
        foreach (string predatorKey in predatorOrder)
        {
            if (!preyIds.Contains(predatorKey) && placed.Add(predatorKey))
            {
                roots.Add(BuildRoot(predatorKey, byPredator, placed, friends));
            }
        }

        // Anything still unplaced was only reachable through a loop, so it has no root to hang from.
        foreach (string predatorKey in predatorOrder)
        {
            if (placed.Add(predatorKey))
            {
                roots.Add(BuildRoot(predatorKey, byPredator, placed, friends));
            }
        }

        return roots;
    }

    private static DevourmentNode BuildRoot(
        string predatorKey,
        Dictionary<string, List<DevourmentRelationship>> byPredator,
        HashSet<string> placed,
        HashSet<string>? friends)
    {
        List<DevourmentRelationship> carried = byPredator[predatorKey];
        var ancestors = new HashSet<string>(StringComparer.Ordinal) { predatorKey };
        string rootId = carried[0].PredatorId;

        return new DevourmentNode(
            rootId,
            carried[0].PredatorType,
            IsItem: false,
            Status: null,
            FoodValue: null,
            BuildContents(carried, byPredator, placed, ancestors, friends),
            RepeatsAncestor: false,
            carried[0].PredatorDetail,
            IsFriend(friends, rootId));
    }

    private static bool IsFriend(HashSet<string>? friends, string entityId) =>
        friends is not null && entityId.Length > 0 && friends.Contains(entityId);

    private static IReadOnlyList<DevourmentNode> BuildContents(
        List<DevourmentRelationship> carried,
        Dictionary<string, List<DevourmentRelationship>> byPredator,
        HashSet<string> placed,
        HashSet<string> ancestors,
        HashSet<string>? friends)
    {
        var contents = new List<DevourmentNode>(carried.Count);

        foreach (DevourmentRelationship row in carried)
        {
            string preyKey = row.PreyId;
            bool repeatsAncestor = preyKey.Length > 0 && ancestors.Contains(preyKey);
            IReadOnlyList<DevourmentNode> inner = Array.Empty<DevourmentNode>();

            if (!repeatsAncestor
                && preyKey.Length > 0
                && byPredator.TryGetValue(preyKey, out List<DevourmentRelationship>? nested)
                && placed.Add(preyKey))
            {
                ancestors.Add(preyKey);
                inner = BuildContents(nested, byPredator, placed, ancestors, friends);
                ancestors.Remove(preyKey);
            }

            contents.Add(new DevourmentNode(
                preyKey,
                row.PreyType,
                row.PreyIsItem,
                row.Status,
                row.FoodValue,
                inner,
                repeatsAncestor,
                row.PreyDetail,
                IsFriend(friends, preyKey)));
        }

        return contents;
    }
}
