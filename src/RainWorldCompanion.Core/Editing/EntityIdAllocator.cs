// Usings sit above the namespace declaration on purpose. RainWorldCompanion.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Globalization;
using System.Text.RegularExpressions;

namespace RainWorldCompanion.Core.Editing;

/// <summary>
/// Hands out entity ids for creatures and items this app adds to a campaign.
///
/// The game keeps one counter per campaign, written as NEXTID. RainWorldGame.GetNewID raises it
/// and builds <c>new EntityID(-1, nextIssuedId)</c>, so an id from here is shaped like an id the
/// game would have issued next.
///
/// Uniqueness matters more than it looks. EntityID.Equals compares only the number half, ignoring
/// the spawner, so two entities sharing a number are one entity as far as the game is concerned.
/// The counter alone is not enough to guarantee that: a save touched by another tool can carry a
/// number above it. So the record is read as well, and allocation starts above whichever is higher.
/// </summary>
public sealed class EntityIdAllocator
{
    /// <summary>The field holding the campaign's counter.</summary>
    public const string NextIdField = "NEXTID";

    /// <summary>
    /// Matches an id anywhere in a record, whichever field holds it. FRIENDS, DEVOURMENTSTATE,
    /// OBJECTS and the rest all store ids in this one shape, so one sweep of the record covers
    /// every field rather than a list of the fields this app happens to know about.
    /// </summary>
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

    /// <summary>The largest id number found anywhere in the record.</summary>
    public int HighestSeen { get; }

    /// <summary>
    /// True when the campaign has no NEXTID to work from.
    ///
    /// Ids are still handed out, counting up from the highest one the record already holds, which
    /// is unique within this save. What is lost is agreement with the game: a campaign with no
    /// counter gets a random one when it next loads, and that could land on a number handed out
    /// here. Worth saying so; not worth refusing over.
    /// </summary>
    public bool CounterWasMissing => StoredNextId is null;

    /// <summary>What NEXTID should be set to, so the game carries on where this left off.</summary>
    public int NextIdToWrite => _next;

    /// <summary>How many ids have been handed out.</summary>
    public int Issued { get; private set; }

    /// <summary>Reads the counter and sweeps the record for ids already in use.</summary>
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

    /// <summary>
    /// The next id, in the form the game writes. Raising the counter before using it is what
    /// GetNewID does, so the number handed out is one past what was there.
    /// </summary>
    public string Allocate()
    {
        _next++;
        Issued++;

        return CreatureBlobBuilder.EntityId(CreatureBlobBuilder.IssuedSpawner, _next);
    }

    /// <summary>Writes the counter back into a record body, adding NEXTID when it was missing.</summary>
    public string WriteCounter(string recordBody) => DelimitedFields.Record.SetValue(
        recordBody,
        NextIdField,
        _next.ToString(CultureInfo.InvariantCulture));
}
