// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Globalization;
using RainWorldSaveManager.Core.Saves;

namespace RainWorldSaveManager.Core.Editing;

/// <summary>The pieces of a serialized item, kept apart so one of them can be replaced.</summary>
/// <param name="Type">The item name, such as Rock.</param>
/// <param name="EntityId">The id, in the form ID.spawner.number.</param>
/// <param name="RippleLayer">Written after the id, behind a &lt;oB&gt;.</param>
/// <param name="Position">The whole world coordinate, as room, x, y and node joined by dots.</param>
/// <param name="Tail">Everything the item's own class wrote after that, which may be empty.</param>
public sealed record ItemBlob(
    string Type,
    string EntityId,
    int RippleLayer,
    string Position,
    IReadOnlyList<string> Tail);

/// <summary>
/// Builds and takes apart the strings the game writes for one abstract object.
///
/// AbstractPhysicalObject.ToString writes
/// <c>{id}&lt;oB&gt;{rippleLayer}&lt;oA&gt;{type}&lt;oA&gt;{position}</c>, and each subclass appends
/// its own fields after that with the same separator. So the shape is one base and many tails, and
/// the tails are what <see cref="ObjectCatalog"/> holds.
///
/// The difference from a creature is worth keeping in mind: a creature writes its room and node as
/// two parts, while an item writes the whole WorldCoordinate as four, room, x, y and node. Both are
/// separated by dots, so the two look alike and are not.
///
/// A save from a real game agrees: a rock is
/// <c>ID.-1759.8243&lt;oB&gt;0&lt;oA&gt;Rock&lt;oA&gt;SL_S06.23.18.0</c> and nothing else, while a
/// pearl carried around is <c>...&lt;oA&gt;-1&lt;oA&gt;-1&lt;oA&gt;PebblesPearl&lt;oA&gt;0&lt;oA&gt;1</c>.
/// </summary>
public static class ItemBlobBuilder
{
    /// <summary>Separates the fields of a serialized item.</summary>
    public const string PartSeparator = "<oA>";

    /// <summary>Separates the id from the ripple layer.</summary>
    public const string IdSeparator = "<oB>";

    /// <summary>
    /// Coordinates for something nobody placed in a level, which is what a thing in a stomach is.
    /// The game writes -1 for both when an object has no position of its own.
    /// </summary>
    public const int Unplaced = -1;

    /// <summary>
    /// Builds an item the game will read back as one it wrote.
    ///
    /// The tail comes from the catalog, which took it from the game's own reader. A type the
    /// catalog cannot build gets the base alone, which is what the reader's last branch accepts;
    /// for a type that wanted more, the reader throws inside its own try and the object is dropped,
    /// so the caller is the one that has to say so.
    /// </summary>
    public static string Build(
        string type,
        string entityId,
        string room,
        int x = Unplaced,
        int y = Unplaced,
        int node = 0,
        int rippleLayer = 0,
        IReadOnlyList<string>? tail = null)
    {
        string position = string.Format(
            CultureInfo.InvariantCulture,
            "{0}.{1}.{2}.{3}",
            room,
            x,
            y,
            node);

        string blob = string.Format(
            CultureInfo.InvariantCulture,
            "{0}{4}{1}{5}{2}{5}{3}",
            entityId,
            rippleLayer,
            type,
            position,
            IdSeparator,
            PartSeparator);

        IReadOnlyList<string> fields = tail ?? ObjectCatalog.ForName(type).Tail ?? Array.Empty<string>();

        return fields.Count == 0
            ? blob
            : blob + PartSeparator + string.Join(PartSeparator, fields);
    }

    /// <summary>
    /// Builds an item in the same place as a creature, which is where a swallowed one belongs.
    /// A creature only records its room and node, so the two coordinates it does not carry are
    /// written as not placed.
    /// </summary>
    public static string BuildBeside(string type, string entityId, CreatureBlob predator)
        => Build(type, entityId, predator.Room, node: predator.Node);

    /// <summary>
    /// Takes an item blob apart. Null when it has fewer than the three parts the game's reader
    /// requires, because a blob that short is one this app should leave exactly as it found it.
    /// </summary>
    public static ItemBlob? Parse(string? blob)
    {
        if (string.IsNullOrEmpty(blob))
        {
            return null;
        }

        string[] parts = blob.Split(PartSeparator, StringSplitOptions.None);

        if (parts.Length < 3)
        {
            return null;
        }

        string id = parts[0];
        int rippleLayer = 0;
        int separator = id.IndexOf(IdSeparator, StringComparison.Ordinal);

        if (separator >= 0)
        {
            rippleLayer = ParseInt(id.Substring(separator + IdSeparator.Length));
            id = id.Substring(0, separator);
        }

        return new ItemBlob(parts[1], id, rippleLayer, parts[2], parts.Skip(3).ToArray());
    }

    /// <summary>Writes a blob back out from its pieces.</summary>
    public static string ToBlob(ItemBlob blob)
    {
        string text = blob.EntityId + IdSeparator
            + blob.RippleLayer.ToString(CultureInfo.InvariantCulture) + PartSeparator
            + blob.Type + PartSeparator + blob.Position;

        return blob.Tail.Count == 0
            ? text
            : text + PartSeparator + string.Join(PartSeparator, blob.Tail);
    }

    /// <summary>
    /// Moves an item to another room, keeping the rest of its position. A swallowed item follows
    /// whatever swallowed it, the same as a swallowed creature does.
    /// </summary>
    public static string WithRoom(string blob, string room)
    {
        if (Parse(blob) is not { } parsed)
        {
            return blob;
        }

        string[] coordinates = parsed.Position.Split('.');

        if (coordinates.Length < 2)
        {
            return ToBlob(parsed with { Position = room });
        }

        // The room name is everything before the last three, because a name can hold a dot.
        string rest = string.Join(".", coordinates.Skip(coordinates.Length - 3));

        return ToBlob(parsed with { Position = room + "." + rest });
    }

    /// <summary>The room half of an item's position, which is everything before the last three parts.</summary>
    public static string RoomOf(string? blob)
    {
        if (Parse(blob) is not { } parsed)
        {
            return "";
        }

        string[] coordinates = parsed.Position.Split('.');

        return coordinates.Length < 4
            ? parsed.Position
            : string.Join(".", coordinates.Take(coordinates.Length - 3));
    }

    private static int ParseInt(string text)
        => int.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out int value) ? value : 0;
}
