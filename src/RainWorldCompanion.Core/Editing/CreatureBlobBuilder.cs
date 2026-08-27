// RainWorldCompanion.Core.System exists in this assembly, so a using written inside the namespace
// body would bind "System" to that namespace instead of the BCL root.
using System.Globalization;

namespace RainWorldCompanion.Core.Editing;

/// <param name="Type">The creature name, such as PinkLizard.</param>
/// <param name="EntityId">The id, in the form ID.spawner.number.</param>
/// <param name="RippleLayer">Written after the id, behind a &lt;cB&gt;. Zero in every save without Watcher.</param>
public sealed record CreatureBlob(
    string Type,
    string EntityId,
    int RippleLayer,
    string Room,
    int Node,
    string State);

/// <summary>
/// SaveState.AbstractCreatureToStringStoryWorld writes
/// <c>{type}&lt;cA&gt;{id}&lt;cB&gt;{rippleLayer}&lt;cA&gt;{room}.{node}&lt;cA&gt;{state}</c>, and
/// AbstractCreatureFromString reads it back by splitting on &lt;cA&gt; and taking those four in
/// order. The state is a run of <c>Tag&lt;cC&gt;value&lt;cB&gt;</c> blocks, empty for a creature
/// alive, undamaged and remembering nobody, which the game reads back without complaint.
/// </summary>
public static class CreatureBlobBuilder
{
    public const string PartSeparator = "<cA>";

    /// <summary>Separates the id from the ripple layer, and one state block from the next.</summary>
    public const string BlockSeparator = "<cB>";

    /// <summary>Separates a state block's tag from its value.</summary>
    public const string TagSeparator = "<cC>";

    /// <summary>Separates the parts of one social relationship.</summary>
    public const string RelationPartSeparator = "<rA>";

    /// <summary>Separates a relationship value's letter from its number.</summary>
    public const string RelationValueSeparator = "<rB>";

    /// <summary>Separates one relationship from the next.</summary>
    public const string RelationSeparator = "<smA>";

    public const string SocialTag = "Social";

    public const string MeatLeftTag = "MeatLeft";

    /// <summary>The state block, written bare, that marks a creature as dead.</summary>
    public const string DeadTag = "Dead";

    /// <summary>The player's id in a story campaign, which is what a creature's feelings towards the
    /// player are stored against.</summary>
    public const string PlayerEntityId = "ID.-1.0";

    /// <summary>The spawner the game uses for an id it issues itself: RainWorldGame.GetNewID builds
    /// <c>new EntityID(-1, ++nextIssuedId)</c>.</summary>
    public const int IssuedSpawner = -1;

    public static string EntityId(int spawner, int number) => string.Format(
        CultureInfo.InvariantCulture,
        "ID.{0}.{1}",
        spawner,
        number);

    /// <summary>The half of an id that has to be unique: EntityID.Equals compares only the number,
    /// so two creatures sharing one are the same creature to the game.</summary>
    public static int? NumberOf(string? entityId)
    {
        if (string.IsNullOrEmpty(entityId))
        {
            return null;
        }

        string[] parts = entityId.Split('.');

        return parts.Length >= 3
            && int.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out int number)
                ? number
                : null;
    }

    /// <param name="state">Empty means alive, undamaged and remembering nobody.</param>
    public static string Build(
        string type,
        string entityId,
        string room,
        int node = 0,
        int rippleLayer = 0,
        string state = "")
        => string.Format(
            CultureInfo.InvariantCulture,
            "{0}{5}{1}{6}{2}{5}{3}.{4}{5}{7}",
            type,
            entityId,
            rippleLayer,
            room,
            node,
            PartSeparator,
            BlockSeparator,
            state);

    /// <summary>Null when the blob does not have the four parts the game's own reader requires.</summary>
    public static CreatureBlob? Parse(string? blob)
    {
        if (string.IsNullOrEmpty(blob))
        {
            return null;
        }

        string[] parts = blob.Split(PartSeparator, StringSplitOptions.None);

        if (parts.Length < 4)
        {
            return null;
        }

        string id = parts[1];
        int rippleLayer = 0;
        int separator = id.IndexOf(BlockSeparator, StringComparison.Ordinal);

        if (separator >= 0)
        {
            rippleLayer = ParseInt(id.Substring(separator + BlockSeparator.Length));
            id = id.Substring(0, separator);
        }

        string room = parts[2];
        int node = 0;
        int dot = room.LastIndexOf('.');

        if (dot >= 0)
        {
            node = ParseInt(room.Substring(dot + 1));
            room = room.Substring(0, dot);
        }

        // A state value can itself hold <cA>, so the parts from the fourth on are joined back rather
        // than only the fourth one taken.
        string state = string.Join(PartSeparator, parts.Skip(3));

        return new CreatureBlob(parts[0], id, rippleLayer, room, node, state);
    }

    public static string ToBlob(CreatureBlob blob)
        => Build(blob.Type, blob.EntityId, blob.Room, blob.Node, blob.RippleLayer, blob.State);

    public static string WithState(string blob, string state)
        => Parse(blob) is { } parsed ? ToBlob(parsed with { State = state }) : blob;

    public static string WithRoom(string blob, string room, int node = 0)
        => Parse(blob) is { } parsed ? ToBlob(parsed with { Room = room, Node = node }) : blob;

    public static string WithEntityId(string blob, string entityId)
        => Parse(blob) is { } parsed ? ToBlob(parsed with { EntityId = entityId }) : blob;

    /// <summary>Null when the state does not carry the block, and also when it carries it written
    /// bare, such as Dead, which has no value.</summary>
    public static string? GetStateBlock(string? state, string tag)
    {
        foreach (string block in Blocks(state))
        {
            int separator = block.IndexOf(TagSeparator, StringComparison.Ordinal);

            if (separator < 0)
            {
                continue;
            }

            if (string.Equals(block.Substring(0, separator), tag, StringComparison.Ordinal))
            {
                return block.Substring(separator + TagSeparator.Length);
            }
        }

        return null;
    }

    /// <summary>True whether or not the block has a value.</summary>
    public static bool HasStateBlock(string? state, string tag)
    {
        foreach (string block in Blocks(state))
        {
            int separator = block.IndexOf(TagSeparator, StringComparison.Ordinal);
            string name = separator < 0 ? block : block.Substring(0, separator);

            if (string.Equals(name, tag, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A null value removes the block. Every block the game writes ends with a &lt;cB&gt;,
    /// the last one included, and keeping that trailing separator is what makes an edited creature
    /// read back the same way as one the game wrote.</summary>
    public static string SetStateBlock(string? state, string tag, string? value)
    {
        var kept = new List<string>();
        bool written = false;

        foreach (string block in Blocks(state))
        {
            int separator = block.IndexOf(TagSeparator, StringComparison.Ordinal);
            string name = separator < 0 ? block : block.Substring(0, separator);

            if (!string.Equals(name, tag, StringComparison.Ordinal))
            {
                kept.Add(block);
                continue;
            }

            written = true;

            if (value is not null)
            {
                kept.Add(tag + TagSeparator + value);
            }
        }

        if (!written && value is not null)
        {
            kept.Add(tag + TagSeparator + value);
        }

        return kept.Count == 0 ? "" : string.Concat(kept.Select(block => block + BlockSeparator));
    }

    /// <summary>The trailing separator the game writes leaves an empty piece at the end, which is
    /// dropped rather than carried as a block of its own.</summary>
    private static IEnumerable<string> Blocks(string? state)
    {
        if (string.IsNullOrEmpty(state))
        {
            yield break;
        }

        foreach (string block in state.Split(BlockSeparator, StringSplitOptions.None))
        {
            if (block.Length > 0)
            {
                yield return block;
            }
        }
    }

    /// <summary>What a creature feels about one other. A null number was not written, which the game
    /// treats as zero.</summary>
    public sealed record Relation(string SubjectId, float? Like, float? Fear, float? Know);

    /// <summary>In stored order.</summary>
    public static IReadOnlyList<Relation> ReadRelations(string? state)
    {
        string? social = GetStateBlock(state, SocialTag);

        if (string.IsNullOrEmpty(social))
        {
            return Array.Empty<Relation>();
        }

        var relations = new List<Relation>();

        foreach (string chunk in social.Split(RelationSeparator, StringSplitOptions.None))
        {
            string[] pieces = chunk.Split(RelationPartSeparator, StringSplitOptions.None);

            if (pieces.Length < 2 || pieces[0] != "REL")
            {
                continue;
            }

            float? like = null;
            float? fear = null;
            float? know = null;

            for (int i = 2; i < pieces.Length; i++)
            {
                string[] pair = pieces[i].Split(RelationValueSeparator, 2, StringSplitOptions.None);

                if (pair.Length < 2
                    || !float.TryParse(pair[1], NumberStyles.Any, CultureInfo.InvariantCulture, out float value))
                {
                    continue;
                }

                switch (pair[0])
                {
                    case "L": like = value; break;
                    case "F": fear = value; break;
                    case "K": know = value; break;
                }
            }

            relations.Add(new Relation(pieces[1].Trim(), like, fear, know));
        }

        return relations;
    }

    /// <summary>SocialMemory.Relationship.ToString writes nothing at all when like and fear are both
    /// zero, so setting a creature's liking to zero takes the whole entry out and what it knows of
    /// the subject goes with it. The game's rule, kept rather than worked around.</summary>
    public static string SetRelation(string? state, string subjectId, float? like, float? fear, float? know)
    {
        var relations = ReadRelations(state).ToList();
        int at = relations.FindIndex(relation =>
            string.Equals(relation.SubjectId, subjectId, StringComparison.Ordinal));

        var updated = new Relation(subjectId, like, fear, know);

        if (at >= 0)
        {
            relations[at] = updated;
        }
        else
        {
            relations.Add(updated);
        }

        return SetRelations(state, relations);
    }

    public static string SetRelations(string? state, IReadOnlyList<Relation> relations)
    {
        var written = relations
            .Select(Write)
            .Where(text => text.Length > 0)
            .ToList();

        return SetStateBlock(state, SocialTag, written.Count == 0 ? null : string.Join(RelationSeparator, written));
    }

    private static string Write(Relation relation)
    {
        // The game's own condition for writing a relationship at all.
        if (Zero(relation.Like) && Zero(relation.Fear))
        {
            return "";
        }

        string text = "REL" + RelationPartSeparator + relation.SubjectId;

        text += Part("L", relation.Like);
        text += Part("F", relation.Fear);
        text += Part("K", relation.Know);

        return text;

        static string Part(string letter, float? value) => Zero(value)
            ? ""
            : RelationPartSeparator + letter + RelationValueSeparator
                + value!.Value.ToString(CultureInfo.InvariantCulture);

        static bool Zero(float? value) => value is null || value.Value == 0f;
    }

    private static int ParseInt(string text)
        => int.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out int value) ? value : 0;
}
