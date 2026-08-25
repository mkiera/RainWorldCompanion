// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Globalization;
using RainWorldSaveManager.Core.Saves;

namespace RainWorldSaveManager.Core.Editing;

/// <summary>
/// The states the Devourment mod puts a swallowed thing in.
///
/// Written by name and read back with Enum.Parse, which is not inside a try: a name the mod does
/// not know throws while the save is loading. That makes an unrecognised status worth saying
/// something about, which is why the list is here rather than left to free text alone.
/// </summary>
public static class DevourmentStatus
{
    public const string Held = "Held";
    public const string Digesting = "Digesting";
    public const string EnergyTheft = "EnergyTheft";
    public const string Healing = "Healing";
    public const string Sedating = "Sedating";
    public const string Regurgitating = "Regurgitating";

    /// <summary>Every status the mod defines, in the order it declares them.</summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        Held, Digesting, EnergyTheft, Healing, Sedating, Regurgitating,
    };

    /// <summary>Whether the mod would read this name back without throwing.</summary>
    public static bool IsKnown(string? status)
        => status is not null && All.Contains(status, StringComparer.Ordinal);
}

/// <summary>
/// One swallowed thing, as a DEVOURMENTSTATE field stores it.
///
/// The mod writes <c>{pred}&lt;dvD&gt;{prey}&lt;dvD&gt;{status}&lt;dvD&gt;{food}</c> and reads it
/// back by splitting on the same separator and taking four parts. A field that does not split into
/// four is one a newer version of the mod wrote, so it is carried as the text it arrived as and
/// written back untouched rather than guessed at.
/// </summary>
public sealed class DevourmentEntry
{
    private const string PartSeparator = DevourmentReader.PartSeparator;

    private DevourmentEntry(string raw, string predator, string prey, string status, string food, bool wellFormed)
    {
        Raw = raw;
        Predator = predator;
        Prey = prey;
        Status = status;
        Food = food;
        IsWellFormed = wellFormed;
    }

    /// <summary>The field value as it was read, which is what a malformed entry is written back as.</summary>
    public string Raw { get; }

    /// <summary>False when the field did not split into the four parts the mod writes.</summary>
    public bool IsWellFormed { get; }

    /// <summary>The serialized creature doing the swallowing.</summary>
    public string Predator { get; internal set; }

    /// <summary>The serialized creature or item that was swallowed.</summary>
    public string Prey { get; internal set; }

    /// <summary>One of <see cref="DevourmentStatus"/>, though anything can be stored.</summary>
    public string Status { get; internal set; }

    /// <summary>How much food is left in it, kept as text so a value that is not a number survives.</summary>
    public string Food { get; internal set; }

    public string? PredatorId => DevourmentReader.CreatureIdOf(Predator);

    /// <summary>True when the swallowed thing is an item rather than a creature.</summary>
    public bool PreyIsItem => Prey.StartsWith(DevourmentReader.ItemPrefix, StringComparison.Ordinal);

    public string? PreyId => PreyIsItem
        ? DevourmentReader.ItemIdOf(Prey)
        : DevourmentReader.CreatureIdOf(Prey);

    public string PreyType => (PreyIsItem
        ? DevourmentReader.ItemTypeOf(Prey)
        : DevourmentReader.CreatureTypeOf(Prey)) ?? "";

    public string PredatorType => DevourmentReader.CreatureTypeOf(Predator) ?? "";

    internal static DevourmentEntry Parse(string value)
    {
        string[] parts = value.Split(PartSeparator, StringSplitOptions.None);

        return parts.Length == 4
            ? new DevourmentEntry(value, parts[0], parts[1], parts[2], parts[3], wellFormed: true)
            : new DevourmentEntry(value, "", "", "", "", wellFormed: false);
    }

    internal static DevourmentEntry Create(string predator, string prey, string status, string food)
        => new(
            string.Join(PartSeparator, predator, prey, status, food),
            predator,
            prey,
            status,
            food,
            wellFormed: true);

    /// <summary>The field value to write, which for an entry this app could not read is the original.</summary>
    public string ToFieldValue() => IsWellFormed
        ? string.Join(PartSeparator, Predator, Prey, Status, Food)
        : Raw;
}

/// <summary>
/// The Devourment state of one campaign, open for editing.
///
/// Three fields of the record carry it and they have to agree. DEVOURMENTSTATE holds one field per
/// swallowed thing, each with a full copy of both the predator and the prey. FRIENDS holds another
/// full copy of every tamed creature. So a creature the player has swallowed and tamed exists three
/// times over, and changing what it thinks of the player in one copy and not the others leaves the
/// save saying two different things about one creature. Every mutator here works from an entity id
/// and writes each copy of it.
///
/// Nothing rebuilds a field it did not change. A state read and applied without an edit gives the
/// record back exactly as it arrived.
/// </summary>
public sealed class DevourmentEditState
{
    /// <summary>The field holding one swallowed thing. The record carries one per entry.</summary>
    public const string EntryField = "DEVOURMENTSTATE";

    /// <summary>The field listing tamed creatures, as whole creature blobs.</summary>
    public const string FriendsField = "FRIENDS";

    /// <summary>Separates one tamed creature from the next.</summary>
    public const string FriendSeparator = "<svC>";

    private static readonly DelimitedFields Fields = DelimitedFields.Record;

    private readonly List<DevourmentEntry> _entries;
    private readonly List<string> _friends;
    private readonly EntityIdAllocator _allocator;

    private bool _entriesChanged;
    private bool _friendsChanged;

    private DevourmentEditState(
        List<DevourmentEntry> entries,
        List<string> friends,
        EntityIdAllocator allocator)
    {
        _entries = entries;
        _friends = friends;
        _allocator = allocator;
    }

    /// <summary>The swallowed things, in the order the record stores them.</summary>
    public IReadOnlyList<DevourmentEntry> Entries => _entries;

    /// <summary>The tamed creatures, as the whole blobs FRIENDS stores.</summary>
    public IReadOnlyList<string> Friends => _friends;

    /// <summary>The ids of the tamed creatures.</summary>
    public IReadOnlyList<string> TamedIds => _friends
        .Select(blob => DevourmentReader.CreatureIdOf(blob) ?? "")
        .Where(id => id.Length > 0)
        .ToArray();

    /// <summary>
    /// True when this campaign has no id counter, so an id handed out here could be handed out
    /// again by the game. Worth saying; not worth refusing over.
    /// </summary>
    public bool IdCounterWasMissing => _allocator.CounterWasMissing;

    public bool IsDirty => _entriesChanged || _friendsChanged || _allocator.Issued > 0;

    /// <summary>Reads the Devourment state out of one campaign record body.</summary>
    public static DevourmentEditState Read(string? recordBody)
    {
        string body = recordBody ?? "";

        var entries = new List<DevourmentEntry>();
        int occurrence = 0;

        while (Fields.GetValue(body, EntryField, occurrence) is { } value)
        {
            entries.Add(DevourmentEntry.Parse(value));
            occurrence++;
        }

        var friends = (Fields.GetValue(body, FriendsField) ?? "")
            .Split(FriendSeparator, StringSplitOptions.None)
            .Where(blob => blob.Length > 0)
            .ToList();

        return new DevourmentEditState(entries, friends, EntityIdAllocator.ForRecord(body));
    }

    // ---- the list ----

    /// <summary>
    /// Moves one entry to another place in the list. Stored order is the order, so this is what
    /// dragging a row up or down comes to.
    /// </summary>
    public void Move(int from, int to)
    {
        if (from == to || !InRange(from) || !InRange(to))
        {
            return;
        }

        DevourmentEntry moved = _entries[from];
        _entries.RemoveAt(from);
        _entries.Insert(to, moved);
        _entriesChanged = true;
    }

    /// <summary>
    /// Takes one swallowed thing out. What it was stays tamed if it was tamed: being in a stomach
    /// and being a friend are two different facts, held in two different fields.
    /// </summary>
    public void RemoveAt(int index)
    {
        if (!InRange(index))
        {
            return;
        }

        _entries.RemoveAt(index);
        _entriesChanged = true;
    }

    public void SetStatus(int index, string status)
    {
        if (!InRange(index) || !_entries[index].IsWellFormed || _entries[index].Status == status)
        {
            return;
        }

        _entries[index].Status = status;
        _entriesChanged = true;
    }

    /// <summary>Sets how much food is left, as text, so a value the mod would choke on still goes in.</summary>
    public void SetFood(int index, string food)
    {
        if (!InRange(index) || !_entries[index].IsWellFormed || _entries[index].Food == food)
        {
            return;
        }

        _entries[index].Food = food;
        _entriesChanged = true;
    }

    /// <summary>
    /// Puts a creature in a predator's stomach, giving it an id nothing else in the campaign holds.
    /// It is placed in the same room as the predator, which is where the mod puts one.
    /// </summary>
    /// <returns>The id the new creature was given.</returns>
    public string AddCreature(
        string type,
        string predatorBlob,
        string status = DevourmentStatus.Held,
        string food = "0")
    {
        string id = _allocator.Allocate();
        CreatureBlob? predator = CreatureBlobBuilder.Parse(predatorBlob);

        string prey = CreatureBlobBuilder.Build(
            type,
            id,
            predator?.Room ?? "",
            predator?.Node ?? 0);

        _entries.Add(DevourmentEntry.Create(predatorBlob, prey, status, food));
        _entriesChanged = true;

        return id;
    }

    // ---- one creature, wherever the record keeps it ----

    /// <summary>
    /// Sets what a creature feels about the player, in every copy of that creature the record holds.
    ///
    /// A creature swallowed and tamed is stored three times over, and the copies have to agree.
    /// Setting the liking to zero takes the whole relationship out, along with what the creature
    /// knew of the player, because that is what the game's own writer does with it.
    /// </summary>
    public void SetFeelingTowardPlayer(string entityId, float? like, float? know)
        => SetFeeling(entityId, CreatureBlobBuilder.PlayerEntityId, like, null, know);

    /// <summary>Sets what one creature feels about another, in every copy of it.</summary>
    public void SetFeeling(string entityId, string subjectId, float? like, float? fear, float? know)
        => UpdateEveryCopy(entityId, blob =>
        {
            CreatureBlob? parsed = CreatureBlobBuilder.Parse(blob);

            return parsed is null
                ? blob
                : CreatureBlobBuilder.ToBlob(parsed with
                {
                    State = CreatureBlobBuilder.SetRelation(parsed.State, subjectId, like, fear, know),
                });
        });

    /// <summary>What a creature feels about the player, read off whichever copy the record holds.</summary>
    public CreatureBlobBuilder.Relation? FeelingTowardPlayer(string entityId)
        => EveryCopy(entityId)
            .Select(blob => CreatureBlobBuilder.ReadRelations(CreatureBlobBuilder.Parse(blob)?.State)
                .FirstOrDefault(relation =>
                    string.Equals(
                        relation.SubjectId,
                        CreatureBlobBuilder.PlayerEntityId,
                        StringComparison.Ordinal)))
            .FirstOrDefault(relation => relation is not null);

    public bool IsTamed(string entityId) => _friends.Any(blob =>
        string.Equals(DevourmentReader.CreatureIdOf(blob), entityId, StringComparison.Ordinal));

    /// <summary>
    /// Adds a creature to the tamed list or takes it off it.
    ///
    /// Taming copies the creature as the stomach holds it, so the two copies start out agreeing.
    /// A creature the record does not hold anywhere cannot be tamed, because there is nothing to
    /// copy: the list stores whole creatures, not names.
    /// </summary>
    public void SetTamed(string entityId, bool tamed)
    {
        if (IsTamed(entityId) == tamed)
        {
            return;
        }

        if (!tamed)
        {
            _friends.RemoveAll(blob =>
                string.Equals(DevourmentReader.CreatureIdOf(blob), entityId, StringComparison.Ordinal));
            _friendsChanged = true;
            return;
        }

        string? copy = EveryCopy(entityId).FirstOrDefault();

        if (copy is null)
        {
            return;
        }

        _friends.Add(copy);
        _friendsChanged = true;
    }

    // ---- writing it back ----

    /// <summary>
    /// Puts the edits into a record body, touching only the fields that changed.
    ///
    /// The entries are removed and written again as a block, which is where the mod puts them: it
    /// appends them after everything the game itself wrote. Nothing else in the record moves.
    /// </summary>
    public string Apply(string recordBody)
    {
        string body = recordBody;

        if (_entriesChanged)
        {
            while (Fields.Has(body, EntryField))
            {
                body = Fields.Remove(body, EntryField);
            }

            foreach (DevourmentEntry entry in _entries)
            {
                body = Fields.Append(body, Fields.Field(EntryField, entry.ToFieldValue()));
            }
        }

        if (_friendsChanged)
        {
            body = _friends.Count == 0
                ? Fields.Remove(body, FriendsField)
                : Fields.SetValue(
                    body,
                    FriendsField,

                    // Every entry carries a trailing separator, including the last, which is what
                    // the game writes and what its reader skips over.
                    string.Concat(_friends.Select(blob => blob + FriendSeparator)));
        }

        if (_allocator.Issued > 0)
        {
            body = _allocator.WriteCounter(body);
        }

        return body;
    }

    // ---- finding every copy of one creature ----

    /// <summary>
    /// Every place the record holds this creature: as a predator, as prey, and in the tamed list.
    /// </summary>
    private IEnumerable<string> EveryCopy(string entityId)
    {
        foreach (DevourmentEntry entry in _entries)
        {
            if (Matches(entry.Predator, entityId))
            {
                yield return entry.Predator;
            }

            if (!entry.PreyIsItem && Matches(entry.Prey, entityId))
            {
                yield return entry.Prey;
            }
        }

        foreach (string friend in _friends)
        {
            if (Matches(friend, entityId))
            {
                yield return friend;
            }
        }
    }

    private void UpdateEveryCopy(string entityId, Func<string, string> update)
    {
        foreach (DevourmentEntry entry in _entries)
        {
            if (Matches(entry.Predator, entityId))
            {
                string updated = update(entry.Predator);

                if (!string.Equals(updated, entry.Predator, StringComparison.Ordinal))
                {
                    entry.Predator = updated;
                    _entriesChanged = true;
                }
            }

            if (!entry.PreyIsItem && Matches(entry.Prey, entityId))
            {
                string updated = update(entry.Prey);

                if (!string.Equals(updated, entry.Prey, StringComparison.Ordinal))
                {
                    entry.Prey = updated;
                    _entriesChanged = true;
                }
            }
        }

        for (int i = 0; i < _friends.Count; i++)
        {
            if (!Matches(_friends[i], entityId))
            {
                continue;
            }

            string updated = update(_friends[i]);

            if (!string.Equals(updated, _friends[i], StringComparison.Ordinal))
            {
                _friends[i] = updated;
                _friendsChanged = true;
            }
        }
    }

    private static bool Matches(string blob, string entityId)
        => string.Equals(DevourmentReader.CreatureIdOf(blob), entityId, StringComparison.Ordinal);

    private bool InRange(int index) => index >= 0 && index < _entries.Count;

    /// <summary>How much food an entry says is left, or null when it does not say a number.</summary>
    public static int? FoodOf(DevourmentEntry entry)
        => int.TryParse(entry.Food, NumberStyles.Any, CultureInfo.InvariantCulture, out int food)
            ? food
            : null;
}
