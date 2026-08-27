// RainWorldCompanion.Core.System exists in this assembly, so a using written inside the namespace
// body would bind "System" to that namespace instead of the BCL root.
using System.Globalization;
using System.Text.RegularExpressions;

namespace RainWorldCompanion.Core.Editing;

/// <summary>
/// Hands out entity ids for creatures and items this app adds to a campaign. EntityID.Equals compares
/// only the number half, ignoring the spawner, so two entities sharing a number are one entity to the
/// game. The campaign's NEXTID counter alone does not guarantee that, because a save touched by
/// another tool can carry a number above it, so allocation starts above whichever is higher.
/// </summary>
public sealed class EntityIdAllocator
{
    public const string NextIdField = "NEXTID";

    /// <summary>Every field stores ids in this one shape, so one sweep of the record covers them all
    /// rather than a list of the fields this app happens to know about.</summary>
    private static readonly Regex IdPattern = new(
        @"ID\.-?\d+\.(-?\d+)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private int _next;

    private EntityIdAllocator(int next, int? storedNextId, int highestSeen)
    {
        _next = next;
        StoredNextId = storedNextId;
        HighestSeen = highestSeen;
    }

    /// <summary>What NEXTID held, or null when the campaign carries no counter.</summary>
    public int? StoredNextId { get; }

    public int HighestSeen { get; }

    /// <summary>Ids are still handed out, but a campaign with no counter gets a random one when it
    /// next loads, which could land on a number handed out here.</summary>
    public bool CounterWasMissing => StoredNextId is null;

    public int NextIdToWrite => _next;

    public int Issued { get; private set; }

    public static EntityIdAllocator ForRecord(string? recordBody)
    {
        string body = recordBody ?? "";
        int? stored = null;

        if (DelimitedFields.Record.GetValue(body, NextIdField) is { } text
            && int.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out int parsed))
        {
            stored = parsed;
        }

        int highest = 0;

        foreach (Match match in IdPattern.Matches(body))
        {
            if (int.TryParse(
                    match.Groups[1].Value,
                    NumberStyles.Any,
                    CultureInfo.InvariantCulture,
                    out int number)
                && number > highest)
            {
                highest = number;
            }
        }

        return new EntityIdAllocator(Math.Max(stored ?? 0, highest), stored, highest);
    }

    /// <summary>Raises the counter before using it, as GetNewID does.</summary>
    public string Allocate()
    {
        _next++;
        Issued++;

        return CreatureBlobBuilder.EntityId(CreatureBlobBuilder.IssuedSpawner, _next);
    }

    /// <summary>Adds NEXTID when it was missing.</summary>
    public string WriteCounter(string recordBody) => DelimitedFields.Record.SetValue(
        recordBody,
        NextIdField,
        _next.ToString(CultureInfo.InvariantCulture));
}
