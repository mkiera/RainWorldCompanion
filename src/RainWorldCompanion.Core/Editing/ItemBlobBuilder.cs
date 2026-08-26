// RainWorldCompanion.Core.System exists in this assembly, so a using written inside the namespace
// body would bind "System" to that namespace instead of the BCL root.
using System.Globalization;
using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Core.Editing;

/// <param name="EntityId">The id, in the form ID.spawner.number.</param>
/// <param name="Position">The whole world coordinate, as room, x, y and node joined by dots.</param>
public sealed record ItemBlob(
    string Type,
    string EntityId,
    int RippleLayer,
    string Position,
    IReadOnlyList<string> Tail);

/// <summary>
/// AbstractPhysicalObject.ToString writes
/// <c>{id}&lt;oB&gt;{rippleLayer}&lt;oA&gt;{type}&lt;oA&gt;{position}</c> and each subclass appends
/// its own fields after that, which is what <see cref="ObjectCatalog"/> holds. A creature writes its
/// room and node as two dotted parts, where an item writes the whole WorldCoordinate as four.
/// </summary>
public static class ItemBlobBuilder
{
    public const string PartSeparator = "<oA>";

    public const string IdSeparator = "<oB>";

    /// <summary>What the game writes for both coordinates of an object with no position of its own,
    /// which is what a thing in a stomach is.</summary>
    public const int Unplaced = -1;

    /// <summary>A type the catalog cannot build gets the base alone, which is what the game reader's
    /// last branch accepts. For a type that wanted more, the reader drops the object.</summary>
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

    /// <summary>A creature only records its room and node, so the two coordinates it does not carry
    /// are written as <see cref="Unplaced"/>.</summary>
    public static string BuildBeside(string type, string entityId, CreatureBlob predator)
        => Build(type, entityId, predator.Room, node: predator.Node);

    /// <summary>Null when the blob has fewer than the three parts the game's own reader requires.</summary>
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

    public static string ToBlob(ItemBlob blob)
    {
        string text = blob.EntityId + IdSeparator
            + blob.RippleLayer.ToString(CultureInfo.InvariantCulture) + PartSeparator
            + blob.Type + PartSeparator + blob.Position;

        return blob.Tail.Count == 0
            ? text
            : text + PartSeparator + string.Join(PartSeparator, blob.Tail);
    }

    /// <summary>Keeps the rest of the item's position.</summary>
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

    /// <summary>Everything before the last three dotted parts, because a room name can hold a dot.</summary>
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
