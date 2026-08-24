// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Globalization;
using RainWorldSaveManager.Core.Saves.Models;

namespace RainWorldSaveManager.Core.Saves;

/// <summary>
/// Turns the flat list of DEVOURMENTSTATE relationships into the stomach chains they describe.
///
/// The mod writes one row per predator and prey pair, so a creature swallowed while itself
/// carrying something appears twice: once as prey, once as predator, under the same entity id.
/// Following those ids rebuilds the nesting. In a real save that is how you see a lizard inside
/// the player that is itself holding a spear, or the player inside a lizard, neither of which
/// the flat list gives any way to tell apart.
/// </summary>
public static class DevourmentTree
{
    /// <summary>
    /// A row with no usable predator id cannot be linked to anything, so it is given a key of its
    /// own built from this prefix and its position. The leading space keeps it from colliding with
    /// a real id. Manifests written before ids were recorded are entirely this case, and they come
    /// out as the flat list they used to be.
    /// </summary>
    private const string UnlinkedKeyPrefix = " unlinked:";

    /// <summary>
    /// Builds the chains. Every relationship reaches the result exactly once, including ones the
    /// ids cannot place, so nothing is dropped for being malformed.
    /// </summary>
    public static IReadOnlyList<DevourmentNode> Build(
        IReadOnlyList<DevourmentRelationship>? relationships)
    {
        if (relationships is null || relationships.Count == 0)
        {
            return Array.Empty<DevourmentNode>();
        }

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
                roots.Add(BuildRoot(predatorKey, byPredator, placed));
            }
        }

        // Anything still unplaced was only reachable through a loop, so it has no root to hang
        // from. Promote it rather than letting a malformed save hide rows.
        foreach (string predatorKey in predatorOrder)
        {
            if (placed.Add(predatorKey))
            {
                roots.Add(BuildRoot(predatorKey, byPredator, placed));
            }
        }

        return roots;
    }

    private static DevourmentNode BuildRoot(
        string predatorKey,
        Dictionary<string, List<DevourmentRelationship>> byPredator,
        HashSet<string> placed)
    {
        List<DevourmentRelationship> carried = byPredator[predatorKey];
        var ancestors = new HashSet<string>(StringComparer.Ordinal) { predatorKey };

        return new DevourmentNode(
            carried[0].PredatorId,
            carried[0].PredatorType,
            IsItem: false,
            Status: null,
            FoodValue: null,
            BuildContents(carried, byPredator, placed, ancestors));
    }

    private static IReadOnlyList<DevourmentNode> BuildContents(
        List<DevourmentRelationship> carried,
        Dictionary<string, List<DevourmentRelationship>> byPredator,
        HashSet<string> placed,
        HashSet<string> ancestors)
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
                inner = BuildContents(nested, byPredator, placed, ancestors);
                ancestors.Remove(preyKey);
            }

            contents.Add(new DevourmentNode(
                preyKey,
                row.PreyType,
                row.PreyIsItem,
                row.Status,
                row.FoodValue,
                inner,
                repeatsAncestor));
        }

        return contents;
    }
}
