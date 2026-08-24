// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.

namespace RainWorldSaveManager.Core.Saves.Models;

/// <summary>
/// One entity in a Devourment stomach chain, with whatever it is itself carrying.
///
/// A root node is something nothing else is holding, so it has no status and no food value of
/// its own. Every other node was the prey half of a relationship, and its status and food value
/// describe it as it sits inside its parent.
/// </summary>
/// <param name="EntityId">Entity id, for example "ID.-1.0". Empty when the save carried none.</param>
/// <param name="Type">Creature or item type, for example "Slugcat" or "DataPearl".</param>
/// <param name="IsItem">True when this is an item rather than a creature.</param>
/// <param name="Status">Belly status inside the parent, or null for a root.</param>
/// <param name="FoodValue">Food worth inside the parent, or null for a root.</param>
/// <param name="Contents">What this entity is carrying, in the order the save listed it.</param>
/// <param name="RepeatsAncestor">
/// True when this entity also appears further up its own chain. The save should never describe
/// that, so the node is left empty rather than followed, which is what stops a malformed save
/// from being walked forever.
/// </param>
public sealed record DevourmentNode(
    string EntityId,
    string Type,
    bool IsItem,
    string? Status,
    int? FoodValue,
    IReadOnlyList<DevourmentNode> Contents,
    bool RepeatsAncestor = false)
{
    public bool HasContents => Contents.Count > 0;

    /// <summary>Everything below this node, at any depth.</summary>
    public int DescendantCount
    {
        get
        {
            int total = 0;
            foreach (DevourmentNode child in Contents)
            {
                total += 1 + child.DescendantCount;
            }

            return total;
        }
    }
}
