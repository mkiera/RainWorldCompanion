// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Globalization;

namespace RainWorldSaveManager.Core.Editing;

/// <summary>The pieces of a serialized creature, kept apart so one of them can be replaced.</summary>
/// <param name="Type">The creature name, such as PinkLizard.</param>
/// <param name="EntityId">The id, in the form ID.spawner.number.</param>
/// <param name="RippleLayer">Written after the id, behind a &lt;cB&gt;. Zero in every save without Watcher.</param>
/// <param name="Room">The room name the creature is in.</param>
/// <param name="Node">Which node of that room, which the game writes after a dot.</param>
/// <param name="State">Everything the creature's own state class wrote, which may be empty.</param>
public sealed record CreatureBlob(
    string Type,
    string EntityId,
    int RippleLayer,
    string Room,
    int Node,
    string State);

/// <summary>
/// Builds and takes apart the strings the game writes for one abstract creature.
///
/// Every position here is pinned to the game's own code rather than inferred from a save.
/// SaveState.AbstractCreatureToStringStoryWorld writes
/// <c>{type}&lt;cA&gt;{id}&lt;cB&gt;{rippleLayer}&lt;cA&gt;{room}.{node}&lt;cA&gt;{state}</c>, and
/// SaveState.AbstractCreatureFromString reads it back by splitting on &lt;cA&gt; and taking those
/// four in order. EntityID.ToString writes <c>ID.{spawner}.{number}</c>.
///
/// The state is whatever CreatureState.ToString produced, which is a run of
/// <c>Tag&lt;cC&gt;value&lt;cB&gt;</c> blocks. A creature that is alive, undamaged and remembers
/// nobody writes an empty state, and the game reads an empty one back without complaint: every
/// branch of CreatureState.LoadFromString that touches a value is behind a length check. A save
/// from a real game confirms it, storing the player as
/// <c>Slugcat&lt;cA&gt;ID.-1.0&lt;cB&gt;0&lt;cA&gt;SL_S06.0&lt;cA&gt;</c> with nothing after the
/// last separator.
/// </summary>
public static class CreatureBlobBuilder
{
    /// <summary>Separates the parts of a serialized creature.</summary>
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

    /// <summary>The state block holding a creature's social memory.</summary>
    public const string SocialTag = "Social";

    /// <summary>The state block holding how much of a creature is left to eat.</summary>
    public const string MeatLeftTag = "MeatLeft";

    /// <summary>The state block, written bare, that marks a creature as dead.</summary>
    public const string DeadTag = "Dead";

    /// <summary>
    /// The player's id in a story campaign, which is what a creature's feelings towards the player
    /// are stored against. Confirmed in a real save: every swallowed creature that has met the
    /// player carries a relationship addressed to this id.
    /// </summary>
    public const string PlayerEntityId = "ID.-1.0";

    /// <summary>
    /// The spawner the game uses for an id it issues itself. RainWorldGame.GetNewID builds
    /// <c>new EntityID(-1, ++nextIssuedId)</c>, so an id this app hands out looks like one the game
    /// handed out.
    /// </summary>
    public const int IssuedSpawner = -1;

    /// <summary>Builds the id text for a spawner and a number.</summary>
    public static string EntityId(int spawner, int number) => string.Format(
        CultureInfo.InvariantCulture,
        "ID.{0}.{1}",
        spawner,
        number);

    /// <summary>
    /// The number half of an id, which is the half that has to be unique: EntityID.Equals compares
    /// only the number, so two creatures sharing one are the same creature to the game.
    /// </summary>
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

    /// <summary>Builds a creature the game will read back as one it wrote.</summary>
    /// <param name="state">
    /// The creature's state blocks. Empty means alive, undamaged and remembering nobody, which is
    /// what the game itself writes for a creature in that condition.
    /// </param>
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

    /// <summary>
    /// Takes a creature blob apart. Null when it does not have the four parts the game's reader
    /// requires, because a blob that short is one this app should leave exactly as it found it.
    /// </summary>
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

        // Everything from the fourth part on is state. A state value can itself hold <cA>, so the
        // parts are joined back rather than only the fourth one taken.
        string state = string.Join(PartSeparator, parts.Skip(3));

        return new CreatureBlob(parts[0], id, rippleLayer, room, node, state);
    }

    /// <summary>Writes a blob back out from its pieces.</summary>
    public static string ToBlob(CreatureBlob blob)
        => Build(blob.Type, blob.EntityId, blob.Room, blob.Node, blob.RippleLayer, blob.State);

    /// <summary>Replaces a creature's state, leaving its type, id and room where they were.</summary>
    public static string WithState(string blob, string state)
        => Parse(blob) is { } parsed ? ToBlob(parsed with { State = state }) : blob;

    /// <summary>Moves a creature to another room, which is how a swallowed creature follows its predator.</summary>
    public static string WithRoom(string blob, string room, int node = 0)
        => Parse(blob) is { } parsed ? ToBlob(parsed with { Room = room, Node = node }) : blob;

    /// <summary>Gives a creature a different id, in both halves of the id text.</summary>
    public static string WithEntityId(string blob, string entityId)
        => Parse(blob) is { } parsed ? ToBlob(parsed with { EntityId = entityId }) : blob;

    // ---- state blocks ----

    /// <summary>
    /// The value of one state block, or null when the state does not carry it. A block written
    /// bare, such as Dead, has no value and answers null as well.
    /// </summary>
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

    /// <summary>Whether the state carries a block at all, whether or not it has a value.</summary>
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

    /// <summary>
    /// Writes one state block, replacing it where it is or adding it at the end. A null value
    /// removes it.
    ///
    /// Every block the game writes ends with a &lt;cB&gt;, including the last one, which is what a
    /// real save shows: a swallowed creature's social memory runs up to
    /// <c>...&lt;cB&gt;&lt;dvD&gt;Held</c>. Keeping that trailing separator is what makes an edited
    /// creature read back the same way as one the game wrote.
    /// </summary>
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

    /// <summary>
    /// The blocks of a state string. The trailing separator the game writes leaves an empty piece
    /// at the end, which is dropped rather than carried as a block of its own.
    /// </summary>
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

    // ---- social memory ----

    /// <summary>
    /// What a creature feels about one other. Null means the number was not written, which the game
    /// treats as zero.
    /// </summary>
    public sealed record Relation(string SubjectId, float? Like, float? Fear, float? Know);

    /// <summary>Reads the relationships out of a state's Social block, in stored order.</summary>
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

    /// <summary>
    /// Writes one relationship into a state, replacing what was there for that subject.
    ///
    /// A relationship the game would not write is not written here either. SocialMemory.Relationship
    /// .ToString returns nothing at all when like and fear are both zero, so setting a creature's
    /// liking to zero takes the whole entry out, and what it knows of the subject goes with it. That
    /// is the game's rule, kept rather than worked around, so a save this app writes matches one the
    /// game would.
    /// </summary>
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

    /// <summary>Replaces every relationship in a state at once.</summary>
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
