// RainWorldCompanion.Core.System exists in this assembly, so a using written inside the namespace
// body would bind "System" to that namespace instead of the BCL root.
using System.Globalization;
using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Core.Editing;

/// <summary>Written by name and read back with Enum.Parse, which is not inside a try: a name the mod
/// does not know throws while the save is loading.</summary>
public static class DevourmentStatus
{
    public const string Held = "Held";
    public const string Digesting = "Digesting";
    public const string EnergyTheft = "EnergyTheft";
    public const string Healing = "Healing";
    public const string Sedating = "Sedating";
    public const string Regurgitating = "Regurgitating";

    public static IReadOnlyList<string> All { get; } = new[]
    {
        Held, Digesting, EnergyTheft, Healing, Sedating, Regurgitating,
    };

    /// <summary>Whether the mod would read this name back without throwing.</summary>
    public static bool IsKnown(string? status)
        => status is not null && All.Contains(status, StringComparer.Ordinal);
}

/// <summary>
/// The mod writes <c>{pred}&lt;dvD&gt;{prey}&lt;dvD&gt;{status}&lt;dvD&gt;{food}</c> and reads it
/// back by splitting on the same separator and taking four parts. A field that does not split into
/// four is carried as the text it arrived as and written back untouched.
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

    public string Predator { get; internal set; }

    public string Prey { get; internal set; }

    /// <summary>One of <see cref="DevourmentStatus"/>, though anything can be stored.</summary>
    public string Status { get; internal set; }

    /// <summary>How much food is left in it, kept as text so a value that is not a number survives.</summary>
    public string Food { get; internal set; }

    public string? PredatorId => DevourmentReader.CreatureIdOf(Predator);

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
/// A creature the player has swallowed and tamed is stored three times over: as a predator and as
/// prey inside DEVOURMENTSTATE, and again in FRIENDS. The copies have to agree, so every mutator
/// here works from an entity id and writes each copy of it. Nothing rebuilds a field it did not
/// change.
/// </summary>
public sealed class DevourmentEditState
{
    /// <summary>The record carries one of these per swallowed thing.</summary>
    public const string EntryField = "DEVOURMENTSTATE";

    public const string FriendsField = "FRIENDS";

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

    /// <summary>In the order the record stores them.</summary>
    public IReadOnlyList<DevourmentEntry> Entries => _entries;

    public IReadOnlyList<string> Friends => _friends;

    public IReadOnlyList<string> TamedIds => _friends
        .Select(blob => DevourmentReader.CreatureIdOf(blob) ?? "")
        .Where(id => id.Length > 0)
        .ToArray();

    /// <summary>True when this campaign has no id counter, so an id handed out here could be handed
    /// out again by the game.</summary>
    public bool IdCounterWasMissing => _allocator.CounterWasMissing;

    public bool IsDirty => _entriesChanged || _friendsChanged || _allocator.Issued > 0;

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

    /// <summary>Stored order is the order the mod reads, so this is what dragging a row comes to.</summary>
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

    /// <summary>What it was stays tamed if it was tamed: being in a stomach and being a friend are
    /// two different facts, held in two different fields.</summary>
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

    /// <summary>As text, so a value the mod would choke on still goes in.</summary>
    public void SetFood(int index, string food)
    {
        if (!InRange(index) || !_entries[index].IsWellFormed || _entries[index].Food == food)
        {
            return;
        }

        _entries[index].Food = food;
        _entriesChanged = true;
    }

    /// <summary>The nesting a save describes is not stored anywhere: it is implied by the same entity
    /// id being prey in one field and predator in another, so rewriting one field's predator moves
    /// the whole subtree.</summary>
    /// <returns>False when the move would make a loop, or the entry is not one to move.</returns>
    public bool SetPredator(int index, string predatorBlob)
    {
        if (!InRange(index) || !_entries[index].IsWellFormed)
        {
            return false;
        }

        DevourmentEntry entry = _entries[index];
        string? newPredatorId = DevourmentReader.CreatureIdOf(predatorBlob);

        if (newPredatorId is null || string.Equals(entry.PredatorId, newPredatorId, StringComparison.Ordinal))
        {
            return false;
        }

        if (entry.PreyId is { Length: > 0 } preyId && WouldLoop(preyId, newPredatorId))
        {
            return false;
        }

        entry.Predator = predatorBlob;

        // The mod puts a swallowed thing in the room its predator is in, so it follows the move. An
        // item keeps the rest of its coordinate, which a creature does not carry at all.
        if (CreatureBlobBuilder.Parse(predatorBlob) is { } predator)
        {
            entry.Prey = entry.PreyIsItem
                ? ItemBlobBuilder.WithRoom(entry.Prey, predator.Room)
                : CreatureBlobBuilder.WithRoom(entry.Prey, predator.Room, predator.Node);
        }

        _entriesChanged = true;
        return true;
    }

    /// <summary>A loop closes when the prey already holds the predator at any depth.</summary>
    public bool WouldLoop(string preyId, string predatorId)
    {
        if (string.Equals(preyId, predatorId, StringComparison.Ordinal))
        {
            return true;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? walk = predatorId;

        while (walk is not null && seen.Add(walk))
        {
            if (string.Equals(walk, preyId, StringComparison.Ordinal))
            {
                return true;
            }

            walk = HolderOf(walk);
        }

        return false;
    }

    /// <summary>The id of whatever is holding this one, or null when nothing is.</summary>
    public string? HolderOf(string entityId) => _entries
        .FirstOrDefault(entry => string.Equals(entry.PreyId, entityId, StringComparison.Ordinal))
        ?.PredatorId;

    public int SiblingIndexOf(int index)
    {
        if (!InRange(index))
        {
            return -1;
        }

        string predator = _entries[index].PredatorId ?? "";
        int seen = 0;

        for (int i = 0; i < index; i++)
        {
            if (string.Equals(_entries[i].PredatorId ?? "", predator, StringComparison.Ordinal))
            {
                seen++;
            }
        }

        return seen;
    }

    /// <summary>Positions in the entry list, in stored order.</summary>
    public IReadOnlyList<int> SiblingsOf(string predatorId)
    {
        var siblings = new List<int>();

        for (int i = 0; i < _entries.Count; i++)
        {
            if (string.Equals(_entries[i].PredatorId ?? "", predatorId, StringComparison.Ordinal))
            {
                siblings.Add(i);
            }
        }

        return siblings;
    }

    /// <summary>The new creature gets an id nothing else in the campaign holds.</summary>
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

    /// <summary>The food value is written as -1, which is what the game stores for something that is
    /// not food: an item read back as a number of meals would be a meal.</summary>
    /// <returns>The id the new item was given.</returns>
    public string AddItem(
        string type,
        string predatorBlob,
        string status = DevourmentStatus.Held,
        string food = "-1")
    {
        string id = _allocator.Allocate();
        CreatureBlob? predator = CreatureBlobBuilder.Parse(predatorBlob);

        string prey = predator is null
            ? ItemBlobBuilder.Build(type, id, "")
            : ItemBlobBuilder.BuildBeside(type, id, predator);

        _entries.Add(DevourmentEntry.Create(predatorBlob, prey, status, food));
        _entriesChanged = true;

        return id;
    }

    /// <summary>Setting the liking to zero takes the whole relationship out, along with what the
    /// creature knew of the player, because that is what the game's own writer does with it.</summary>
    public void SetFeelingTowardPlayer(string entityId, float? like, float? know)
        => SetFeeling(entityId, CreatureBlobBuilder.PlayerEntityId, like, null, know);

    /// <summary>Writes every copy of the creature the record holds.</summary>
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

    /// <summary>A creature the record does not hold anywhere cannot be tamed, because the list stores
    /// whole creatures rather than names and there is nothing to copy.</summary>
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

    /// <summary>Touches only the fields that changed. The entries are removed and written again as a
    /// block at the end, which is where the mod appends them.</summary>
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

    /// <summary>As a predator, as prey, and in the tamed list.</summary>
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

    /// <summary>Null when the entry does not say a number.</summary>
    public static int? FoodOf(DevourmentEntry entry)
        => int.TryParse(entry.Food, NumberStyles.Any, CultureInfo.InvariantCulture, out int food)
            ? food
            : null;
}
