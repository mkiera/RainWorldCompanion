// Usings sit above the namespace declaration on purpose. RainWorldCompanion.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Globalization;
using RainWorldCompanion.Core.Saves.Models;

namespace RainWorldCompanion.Core.Saves;

/// <summary>
/// Reads the detail out of the game's own serialized creature and object strings.
///
/// Field positions here were taken from the game assembly rather than guessed: AbstractCreature
/// and the AbstractPhysicalObject subclasses each build their string with a literal format, so
/// every index below is pinned to a named field in that code.
/// </summary>
public static class EntityBlobReader
{
    private const string CreatureSep = "<cA>";
    private const string CreatureSub = "<cB>";
    private const string StateSep = "<cC>";
    private const string ItemSep = "<oA>";
    private const string ItemIdSep = "<oB>";
    private const string RelSep = "<rA>";
    private const string RelValueSep = "<rB>";
    private const string SocialSep = "<smA>";

    /// <summary>Marks the social memory block inside a creature blob.</summary>
    private const string SocialTag = "Social";

    /// <summary>Marks the meat remaining on a partly eaten creature.</summary>
    private const string MeatTag = "MeatLeft";

    /// <summary>
    /// From DataPearl.AbstractDataPearl.BaseToString: id, type, position, originRoom,
    /// placedObjectIndex, dataPearlType. The first two of those are not object separators, so the
    /// pearl type lands at index 5 of the &lt;oA&gt; split.
    /// </summary>
    private const int PearlTypeIndex = 5;

    /// <summary>PebblesPearl appends colour then number after the DataPearl part.</summary>
    private const int PebblesNumberIndex = 7;

    /// <summary>
    /// From AbstractSpear.ToString: after id, type and position come stuckInWallCycles, explosive,
    /// hue, electric, electricCharge, needle, poison, poisonHue.
    /// </summary>
    private const int SpearFirstIndex = 3;

    /// <summary>Reads a creature blob. Never throws.</summary>
    public static DevourmentEntity ReadCreature(string? blob, string entityId, string type)
    {
        var entity = new DevourmentEntity { EntityId = entityId, Type = type, IsItem = false };
        if (string.IsNullOrEmpty(blob))
        {
            return entity;
        }

        try
        {
            return new DevourmentEntity
            {
                EntityId = entityId,
                Type = type,
                IsItem = false,
                Social = ReadSocial(blob),
                MeatLeft = ReadTaggedInt(blob, MeatTag),
            };
        }
        catch (ArgumentException)
        {
            return entity;
        }
    }

    /// <summary>Reads an item blob. Never throws.</summary>
    public static DevourmentEntity ReadItem(string? blob, string entityId, string type)
    {
        var entity = new DevourmentEntity { EntityId = entityId, Type = type, IsItem = true };
        if (string.IsNullOrEmpty(blob))
        {
            return entity;
        }

        try
        {
            string[] parts = blob.Split(ItemSep, StringSplitOptions.None);

            string? pearlType = null;
            int? pebblesNumber = null;
            if (IsPearl(type) && parts.Length > PearlTypeIndex)
            {
                string candidate = parts[PearlTypeIndex].Trim();
                pearlType = candidate.Length == 0 ? null : candidate;

                if (parts.Length > PebblesNumberIndex
                    && int.TryParse(parts[PebblesNumberIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
                {
                    pebblesNumber = number;
                }
            }

            return new DevourmentEntity
            {
                EntityId = entityId,
                Type = type,
                IsItem = true,
                PearlType = pearlType,
                PebblesPearlNumber = pebblesNumber,
                Spear = ReadSpear(type, parts),
            };
        }
        catch (ArgumentException)
        {
            return entity;
        }
    }

    private static bool IsPearl(string type) =>
        type.Equals("DataPearl", StringComparison.OrdinalIgnoreCase)
        || type.Equals("PebblesPearl", StringComparison.OrdinalIgnoreCase);

    private static SpearState? ReadSpear(string type, string[] parts)
    {
        if (!type.Equals("Spear", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // A spear written by an older version can stop short of the later fields, so each one is
        // read only if it is there.
        int stuck = IntAt(parts, SpearFirstIndex);
        bool explosive = FlagAt(parts, SpearFirstIndex + 1);
        bool electric = FlagAt(parts, SpearFirstIndex + 3);
        int charge = IntAt(parts, SpearFirstIndex + 4);
        bool needle = FlagAt(parts, SpearFirstIndex + 5);
        float poison = FloatAt(parts, SpearFirstIndex + 6);

        return new SpearState(stuck, explosive, electric, charge, needle, poison);
    }

    private static int IntAt(string[] parts, int index) =>
        index < parts.Length
        && int.TryParse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : 0;

    private static float FloatAt(string[] parts, int index) =>
        index < parts.Length
        && float.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            ? value
            : 0f;

    private static bool FlagAt(string[] parts, int index) => IntAt(parts, index) == 1;

    /// <summary>
    /// Pulls the value out of a "Tag&lt;cC&gt;value&lt;cB&gt;" block inside a creature blob.
    /// </summary>
    private static int? ReadTaggedInt(string blob, string tag)
    {
        string opener = tag + StateSep;
        int start = blob.IndexOf(opener, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += opener.Length;
        int end = blob.IndexOf(CreatureSub, start, StringComparison.Ordinal);
        string text = end < 0 ? blob.Substring(start) : blob.Substring(start, end - start);

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : null;
    }

    /// <summary>
    /// Reads the social memory block. Each relationship is REL, the subject id, then any of L, F
    /// and K that were not zero, and several relationships are separated by &lt;smA&gt;.
    /// </summary>
    public static IReadOnlyList<SocialRelationship> ReadSocial(string? blob)
    {
        if (string.IsNullOrEmpty(blob))
        {
            return Array.Empty<SocialRelationship>();
        }

        string opener = SocialTag + StateSep;
        int start = blob.IndexOf(opener, StringComparison.Ordinal);
        if (start < 0)
        {
            return Array.Empty<SocialRelationship>();
        }

        start += opener.Length;
        int end = blob.IndexOf(CreatureSub, start, StringComparison.Ordinal);
        string body = end < 0 ? blob.Substring(start) : blob.Substring(start, end - start);

        var results = new List<SocialRelationship>();
        foreach (string chunk in body.Split(SocialSep, StringSplitOptions.None))
        {
            if (chunk.Length == 0)
            {
                continue;
            }

            string subject = "";
            float? like = null;
            float? fear = null;
            float? know = null;

            foreach (string piece in chunk.Split(RelSep, StringSplitOptions.None))
            {
                if (piece.Length == 0 || piece == "REL")
                {
                    continue;
                }

                string[] pair = piece.Split(RelValueSep, 2, StringSplitOptions.None);
                if (pair.Length < 2)
                {
                    if (subject.Length == 0)
                    {
                        subject = piece.Trim();
                    }

                    continue;
                }

                if (!float.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
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

            if (subject.Length > 0)
            {
                results.Add(new SocialRelationship(subject, like, fear, know));
            }
        }

        return results;
    }

    /// <summary>
    /// Entity ids out of a FRIENDS field, which lists tamed creatures as whole creature blobs
    /// separated by &lt;svC&gt;.
    /// </summary>
    public static IReadOnlyList<string> ReadFriendIds(string? friendsField)
    {
        if (string.IsNullOrEmpty(friendsField))
        {
            return Array.Empty<string>();
        }

        var ids = new List<string>();
        foreach (string blob in friendsField.Split("<svC>", StringSplitOptions.None))
        {
            if (blob.Length == 0)
            {
                continue;
            }

            string? id = DevourmentReader.CreatureIdOf(blob);
            if (!string.IsNullOrEmpty(id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }
}
