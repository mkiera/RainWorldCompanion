// RainWorldCompanion.Core.System exists in this assembly, so a using written inside the namespace
// body would bind "System" to that namespace instead of the BCL root.

namespace RainWorldCompanion.Core.Saves.Models;

/// <param name="EntityId">Entity id, for example "ID.-1.0". Empty when the save carried none.</param>
/// <param name="Type">Creature or item type, for example "Slugcat" or "DataPearl".</param>
/// <param name="Status">Belly status inside the parent, or null for a root.</param>
/// <param name="FoodValue">Food worth inside the parent, or null for a root.</param>
/// <param name="Contents">What this entity is carrying, in the order the save listed it.</param>
/// <param name="RepeatsAncestor">True when this entity also appears further up its own chain, which
/// leaves the node empty rather than followed and stops a malformed save being walked forever.</param>
/// <param name="Detail">What the blob said about this entity, or null when it was not recorded.</param>
/// <param name="IsTamedFriend">True when this entity's id is in the campaign's FRIENDS list, which
/// is NOT the same thing as a high like value.</param>
public sealed record DevourmentNode(
    string EntityId,
    string Type,
    bool IsItem,
    string? Status,
    int? FoodValue,
    IReadOnlyList<DevourmentNode> Contents,
    bool RepeatsAncestor = false,
    DevourmentEntity? Detail = null,
    bool IsTamedFriend = false)
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
