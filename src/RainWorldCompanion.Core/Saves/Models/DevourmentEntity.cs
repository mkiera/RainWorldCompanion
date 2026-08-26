// RainWorldCompanion.Core.System exists in this assembly, so a using written inside the namespace
// body would bind "System" to that namespace instead of the BCL root.

namespace RainWorldCompanion.Core.Saves.Models;

/// <summary>The game writes an entry only when like or fear is not zero.</summary>
/// <param name="SubjectId">Entity the feeling is about. "ID.-1.0" is the player.</param>
/// <param name="Like">Stored as L. Negative means dislike. Absent when zero.</param>
/// <param name="Fear">Stored as F. Absent when zero.</param>
/// <param name="Know">Stored as K, how well it knows the subject. Absent when zero.</param>
public sealed record SocialRelationship(string SubjectId, float? Like, float? Fear, float? Know);

/// <summary>A plain spear stores zero for all of it.</summary>
public sealed record SpearState(
    int StuckInWallCycles,
    bool Explosive,
    bool Electric,
    int ElectricCharge,
    bool Needle,
    float Poison)
{
    public bool IsSpecial => Explosive || Electric || Needle || Poison > 0f;
}

/// <summary>A field the blob does not carry comes back null or empty, so a blob written by a newer
/// game or mod version yields less rather than failing.</summary>
public sealed class DevourmentEntity
{
    public string EntityId { get; init; } = "";

    public string Type { get; init; } = "";

    public bool IsItem { get; init; }

    /// <summary>Empty for an item, and for a creature that has never felt anything.</summary>
    public IReadOnlyList<SocialRelationship> Social { get; init; } = Array.Empty<SocialRelationship>();

    /// <summary>Meat points left on a partly eaten creature. The maximum varies by creature type,
    /// so this is only meaningful as "some has been taken".</summary>
    public int? MeatLeft { get; init; }

    /// <summary>The stored DataPearlType, for example SL_moon. Null for anything not a pearl.</summary>
    public string? PearlType { get; init; }

    /// <summary>The number on one of Five Pebbles' own pearls. Null for any other object.</summary>
    public int? PebblesPearlNumber { get; init; }

    /// <summary>Set only for a spear.</summary>
    public SpearState? Spear { get; init; }

    public SocialRelationship? TowardPlayer
    {
        get
        {
            foreach (SocialRelationship relationship in Social)
            {
                if (string.Equals(relationship.SubjectId, PlayerEntityId, StringComparison.Ordinal))
                {
                    return relationship;
                }
            }

            return null;
        }
    }

    /// <summary>The player's own entity id, which every campaign uses.</summary>
    public const string PlayerEntityId = "ID.-1.0";
}
