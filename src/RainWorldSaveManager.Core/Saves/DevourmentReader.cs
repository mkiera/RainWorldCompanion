// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Globalization;
using RainWorldSaveManager.Core.Saves.Models;

namespace RainWorldSaveManager.Core.Saves;

/// <summary>
/// Reads the DEVOURMENTSTATE fields the Devourment mod writes into a SAVE STATE record.
///
/// One field holds one relationship, split on &lt;dvD&gt; into predator, prey, status and food
/// value. Predator and prey are the game's own serialized forms, so their type names are read
/// off the front of those blobs rather than parsed in full: everything after the type is
/// position, id and social data this app has no use for.
/// </summary>
public static class DevourmentReader
{
    /// <summary>Separates the four parts of one relationship.</summary>
    public const string PartSeparator = "<dvD>";

    /// <summary>Separates the parts of a serialized creature. The type comes first.</summary>
    public const string CreatureSeparator = "<cA>";

    /// <summary>Separates the parts of a serialized item. The type is at index 1.</summary>
    public const string ItemSeparator = "<oA>";

    /// <summary>A prey blob starting with this is an item, not a creature.</summary>
    public const string ItemPrefix = "ID.";

    /// <summary>Separates a creature's id from the rest of its id block.</summary>
    public const string CreatureIdSeparator = "<cB>";

    /// <summary>Separates an item's id from the rest of its blob.</summary>
    public const string ItemIdSeparator = "<oB>";

    /// <summary>Index of the type name inside a serialized item.</summary>
    private const int ItemTypeIndex = 1;

    /// <summary>Index of the id inside a serialized creature, after splitting on &lt;cA&gt;.</summary>
    private const int CreatureIdIndex = 1;

    /// <summary>
    /// Turns one DEVOURMENTSTATE value into a relationship. Returns false for anything that does
    /// not split into exactly four parts or that carries no predator, prey or status text, so a
    /// field written by a newer mod version costs that one row and nothing else.
    /// </summary>
    public static bool TryRead(string? value, out DevourmentRelationship? relationship)
    {
        relationship = null;

        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        string[] parts = value.Split(PartSeparator, StringSplitOptions.None);
        if (parts.Length != 4)
        {
            return false;
        }

        string predator = CreatureTypeOf(parts[0]) ?? "";
        if (predator.Length == 0)
        {
            return false;
        }

        string preyRaw = parts[1];
        bool preyIsItem = preyRaw.StartsWith(ItemPrefix, StringComparison.Ordinal);
        string prey = (preyIsItem ? ItemTypeOf(preyRaw) : CreatureTypeOf(preyRaw)) ?? "";
        if (prey.Length == 0)
        {
            return false;
        }

        string status = parts[2].Trim();
        if (status.Length == 0)
        {
            return false;
        }

        int? foodValue = null;
        if (int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedFood))
        {
            foodValue = parsedFood;
        }

        relationship = new DevourmentRelationship(
            predator,
            prey,
            status,
            foodValue,
            preyIsItem,
            CreatureIdOf(parts[0]) ?? "",
            (preyIsItem ? ItemIdOf(preyRaw) : CreatureIdOf(preyRaw)) ?? "");
        return true;
    }

    /// <summary>
    /// The entity id of a serialized creature: the element at index 1 after splitting on
    /// &lt;cA&gt;, up to the following &lt;cB&gt;. For "Slugcat&lt;cA&gt;ID.-1.0&lt;cB&gt;0..."
    /// that is "ID.-1.0". Null when the blob has no id there.
    /// </summary>
    public static string? CreatureIdOf(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        string[] parts = value.Split(CreatureSeparator, StringSplitOptions.None);
        if (parts.Length <= CreatureIdIndex)
        {
            return null;
        }

        string id = parts[CreatureIdIndex];
        int end = id.IndexOf(CreatureIdSeparator, StringComparison.Ordinal);
        if (end >= 0)
        {
            id = id.Substring(0, end);
        }

        id = id.Trim();
        return id.Length == 0 ? null : id;
    }

    /// <summary>
    /// The entity id of a serialized item: everything before the first &lt;oB&gt;, or before the
    /// first &lt;oA&gt; when there is no &lt;oB&gt;. For "ID.-2588.11856&lt;oB&gt;0&lt;oA&gt;Rock..."
    /// that is "ID.-2588.11856". Null when the blob has no id there.
    /// </summary>
    public static string? ItemIdOf(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        int end = value.IndexOf(ItemIdSeparator, StringComparison.Ordinal);
        if (end < 0)
        {
            end = value.IndexOf(ItemSeparator, StringComparison.Ordinal);
        }

        string id = (end < 0 ? value : value.Substring(0, end)).Trim();
        return id.Length == 0 ? null : id;
    }

    /// <summary>
    /// The type name of a serialized creature: everything before the first &lt;cA&gt;. Null when
    /// there is no name there.
    /// </summary>
    public static string? CreatureTypeOf(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        int end = value.IndexOf(CreatureSeparator, StringComparison.Ordinal);
        string type = end < 0 ? value.Trim() : value.Substring(0, end).Trim();
        return type.Length == 0 ? null : type;
    }

    /// <summary>
    /// The type name of a serialized item: the element at index 1 after splitting on &lt;oA&gt;.
    /// Used for Devourment prey and for SWALLOWEDITEMS, which store the same shape.
    /// Null when the blob has no such element.
    /// </summary>
    public static string? ItemTypeOf(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        string[] parts = value.Split(ItemSeparator, StringSplitOptions.None);
        if (parts.Length <= ItemTypeIndex)
        {
            return null;
        }

        string type = parts[ItemTypeIndex].Trim();
        return type.Length == 0 ? null : type;
    }
}
