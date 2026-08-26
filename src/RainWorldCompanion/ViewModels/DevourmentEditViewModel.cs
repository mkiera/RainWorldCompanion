// Usings sit above the namespace: RainWorldCompanion.Core.System would otherwise shadow System.
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RainWorldCompanion.Core.Editing;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.Core.System;
using RainWorldCompanion.Views.Behaviors;

namespace RainWorldCompanion.ViewModels;

/// <param name="Blob">The serialized creature, which is what an entry stores.</param>
public sealed record PredatorChoice(string Blob, string EntityId, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <param name="Available">
/// False for a creature from an expansion that is not installed and that this campaign has never
/// carried. Nothing is stopped by it: the button still adds the creature.
/// </param>
public sealed record CreatureChoice(CreatureKind Kind, bool Available, string Detail)
{
    public string Name => Kind.Name;

    public string DisplayName => Kind.DisplayName;
}

/// <param name="Available">
/// False for an item from an expansion this install does not have, and for the two whose trailing
/// fields this app has not pinned. Neither is stopped.
/// </param>
public sealed record ObjectChoice(ObjectKind Kind, bool Available, string Detail)
{
    public string Name => Kind.Name;

    public string DisplayName => Kind.DisplayName;
}

/// <summary>
/// A save stores a flat list of predator and prey pairs, and the chains are implied by one entity
/// id being prey in one pair and predator in another. Moving something into something else is
/// therefore an edit to one field's predator, which is what a drop does.
/// </summary>
public sealed partial class DevourmentEditViewModel : ObservableObject, IReorderable
{
    private readonly SaveEditSession _session;
    private readonly CampaignRecordRef _campaign;
    private readonly string _denPos;
    private readonly Action _changed;
    private readonly ExpansionPresence _expansions;

    /// <summary>Only creatures put in since the editor opened, which is what advice is about.</summary>
    private readonly HashSet<string> _added = new(StringComparer.Ordinal);

    /// <summary>
    /// Read once when the editor opened, and not again. Reading it live would let adding one
    /// creature make the next one look fine, which is the question answering itself.
    /// </summary>
    private readonly HashSet<CreatureSource> _arrivedWith = new();

    public DevourmentEditViewModel(
        SaveEditSession session,
        CampaignRecordRef campaign,
        string denPos,
        Action changed,
        ExpansionPresence? expansions = null)
    {
        _session = session;
        _campaign = campaign;
        _denPos = denPos.Trim();
        _changed = changed;
        _expansions = expansions ?? ExpansionPresence.Unknown;

        State = DevourmentEditState.Read(session.GetRecordBody(campaign));

        foreach (DevourmentEntry entry in State.Entries)
        {
            Note(entry.PredatorType);

            if (!entry.PreyIsItem)
            {
                Note(entry.PreyType);
            }
        }

        Roots = new ObservableCollection<DevourmentEditNode>();
        Predators = new ObservableCollection<PredatorChoice>();
        Warnings = new ObservableCollection<string>();

        Rebuild();

        void Note(string type)
        {
            if (CreatureCatalog.IsKnown(type))
            {
                _arrivedWith.Add(CreatureCatalog.ForName(type).Source);
            }
        }
    }

    internal DevourmentEditState State { get; private set; }

    public ObservableCollection<DevourmentEditNode> Roots { get; }

    public ObservableCollection<PredatorChoice> Predators { get; }

    /// <summary>Advice, never a refusal.</summary>
    public ObservableCollection<string> Warnings { get; }

    public bool HasWarnings => Warnings.Count > 0;

    public bool HasRows => State.Entries.Count > 0;

    public IEnumerable<DevourmentEditNode> AllNodes => Roots.SelectMany(root => root.Flatten());

    public string CountText => State.Entries.Count switch
    {
        0 => "Nothing swallowed",
        1 => "1 thing swallowed",
        int count => count.ToString(CultureInfo.InvariantCulture) + " things swallowed",
    };

    [ObservableProperty]
    private string newCreatureSearch = "";

    [ObservableProperty]
    private PredatorChoice? newCreaturePredator;

    /// <summary>Nothing is left out for being unavailable: it is offered with a mark and advice.</summary>
    public IReadOnlyList<CreatureChoice> CreatureMatches => CreatureCatalog
        .Search(NewCreatureSearch)
        .Select(Describe)
        .ToArray();

    public string CreatureMatchCountText => CreatureMatches.Count == CreatureCatalog.Known.Count
        ? CreatureCatalog.Known.Count.ToString(CultureInfo.InvariantCulture) + " creatures"
        : $"{CreatureMatches.Count} of {CreatureCatalog.Known.Count}";

    partial void OnNewCreatureSearchChanged(string value)
    {
        OnPropertyChanged(nameof(CreatureMatches));
        OnPropertyChanged(nameof(CreatureMatchCountText));
    }

    /// <summary>Available means installed, or the campaign already carries one from that source.</summary>
    private CreatureChoice Describe(CreatureKind kind)
    {
        if (kind.Source == CreatureSource.Vanilla)
        {
            return new CreatureChoice(kind, true, "In the base game.");
        }

        string expansion = kind.Source == CreatureSource.Watcher ? "The Watcher" : "Downpour";

        if (Installed(kind.Source))
        {
            return new CreatureChoice(kind, true, $"From {expansion}, which is installed.");
        }

        if (_arrivedWith.Contains(kind.Source))
        {
            return new CreatureChoice(
                kind,
                true,
                $"From {expansion}. This campaign was already carrying creatures from it.");
        }

        return new CreatureChoice(
            kind,
            false,
            _expansions.CheckedTheInstall
                ? $"From {expansion}, which is not installed. The game will not know this creature."
                : $"From {expansion}. Nothing says this save has it, and the game folder was not checked.");
    }

    private bool Installed(CreatureSource source) => _expansions.CheckedTheInstall && source switch
    {
        CreatureSource.Downpour => _expansions.Downpour,
        CreatureSource.Watcher => _expansions.Watcher,
        _ => true,
    };


    /// <summary>Whether the picker is offering items rather than creatures.</summary>
    [ObservableProperty]
    private bool addingAnItem;

    [RelayCommand]
    private void Add()
    {
        if (AddingAnItem)
        {
            AddItem(null);
        }
        else
        {
            AddCreature(null);
        }
    }

    /// <summary>
    /// Setting this off does nothing: a radio group clears the old button before it sets the new
    /// one, and acting on the clear would flip the choice on the way to itself.
    /// </summary>
    public bool AddingACreature
    {
        get => !AddingAnItem;
        set
        {
            if (value)
            {
                AddingAnItem = false;
            }
        }
    }

    partial void OnAddingAnItemChanged(bool value)
    {
        OnPropertyChanged(nameof(AddingACreature));
        OnPropertyChanged(nameof(CreatureMatches));
        OnPropertyChanged(nameof(ItemMatches));
        OnPropertyChanged(nameof(CreatureMatchCountText));
    }

    public IReadOnlyList<ObjectChoice> ItemMatches => ObjectCatalog
        .Search(NewCreatureSearch)
        .Select(DescribeItem)
        .ToArray();

    /// <summary>A type this app cannot build is written as the base alone, and the game drops it.</summary>
    [RelayCommand]
    private void AddItem(string? type)
    {
        string name = (type ?? NewCreatureSearch).Trim();
        PredatorChoice? predator = NewCreaturePredator ?? Predators.FirstOrDefault();

        if (name.Length == 0 || predator is null)
        {
            return;
        }

        string id = State.AddItem(name, predator.Blob);
        _added.Add(id);
        NewCreatureSearch = "";

        Apply(
            "devourment|added|" + id,
            $"put a {ObjectCatalog.ForName(name).DisplayName} inside {predator.DisplayName}");
    }

    private ObjectChoice DescribeItem(ObjectKind kind)
    {
        if (!kind.CanBuild)
        {
            return new ObjectChoice(
                kind,
                false,
                "This app does not know what the game reads after this one's position, so it would "
                + "be written short and the game would drop it.");
        }

        if (kind.Source == CreatureSource.Vanilla)
        {
            return new ObjectChoice(kind, true, "In the base game.");
        }

        string expansion = kind.Source == CreatureSource.Watcher ? "The Watcher" : "Downpour";

        if (Installed(kind.Source) || _arrivedWith.Contains(kind.Source))
        {
            return new ObjectChoice(kind, true, $"From {expansion}.");
        }

        return new ObjectChoice(
            kind,
            false,
            _expansions.CheckedTheInstall
                ? $"From {expansion}, which is not installed. The game will not know this item."
                : $"From {expansion}. Nothing says this save has it, and the game folder was not checked.");
    }

    [RelayCommand]
    private void AddCreature(string? type)
    {
        string name = (type ?? NewCreatureSearch).Trim();
        PredatorChoice? predator = NewCreaturePredator ?? Predators.FirstOrDefault();

        if (name.Length == 0 || predator is null)
        {
            return;
        }

        string id = State.AddCreature(name, predator.Blob);
        _added.Add(id);
        NewCreatureSearch = "";

        Apply(
            "devourment|added|" + id,
            $"put a {CreatureCatalog.ForName(name).DisplayName} inside {predator.DisplayName}");
    }

    /// <summary>Whatever it was holding moves up to whatever was holding it.</summary>
    [RelayCommand]
    internal void RemoveNode(DevourmentEditNode? node)
    {
        if (node is null || node.EntryIndex < 0)
        {
            return;
        }

        string? holder = State.Entries[node.EntryIndex].PredatorId;
        string predatorBlob = State.Entries[node.EntryIndex].Predator;

        foreach (DevourmentEditNode child in node.Children)
        {
            if (child.EntryIndex >= 0 && holder is not null)
            {
                State.SetPredator(child.EntryIndex, predatorBlob);
            }
        }

        State.RemoveAt(node.EntryIndex);

        Apply(
            "devourment|removed|" + node.EntityId,
            $"took the {node.DisplayName} out");
    }

    /// <summary>A drop puts the thing dragged inside the thing it was dropped on.</summary>
    public void MoveOnto(object moved, object target)
    {
        if (moved is DevourmentEditNode from && target is DevourmentEditNode to)
        {
            MoveInto(from, to);
        }
    }

    /// <summary>Puts one thing inside another, refusing a move that would close a loop.</summary>
    public void MoveInto(DevourmentEditNode moved, DevourmentEditNode target)
    {
        if (moved.EntryIndex < 0 || !target.CanHoldThings)
        {
            return;
        }

        if (!State.SetPredator(moved.EntryIndex, target.Blob))
        {
            // The only way this fails on a real gesture is a loop.
            NoteRefusedMove(moved, target);
            return;
        }

        _refusedMove = null;

        Apply(
            "devourment|moved|" + moved.EntityId,
            $"moved the {moved.DisplayName} inside {target.DisplayName}");
    }

    [RelayCommand]
    internal void MoveUp(DevourmentEditNode? node) => Shuffle(node, -1);

    [RelayCommand]
    internal void MoveDown(DevourmentEditNode? node) => Shuffle(node, 1);

    /// <summary>Changes order among the things sharing one stomach, not what holds what.</summary>
    private void Shuffle(DevourmentEditNode? node, int by)
    {
        if (node is null || node.EntryIndex < 0)
        {
            return;
        }

        string predator = State.Entries[node.EntryIndex].PredatorId ?? "";
        IReadOnlyList<int> siblings = State.SiblingsOf(predator);

        int at = -1;

        for (int i = 0; i < siblings.Count; i++)
        {
            if (siblings[i] == node.EntryIndex)
            {
                at = i;
                break;
            }
        }

        int to = at + by;

        if (at < 0 || to < 0 || to >= siblings.Count)
        {
            return;
        }

        State.Move(node.EntryIndex, siblings[to]);

        Apply("devourment|order", "changed the order of what is swallowed");
    }

    internal void OnlyThisRowIsEditing(DevourmentEditNode opened)
    {
        foreach (DevourmentEditNode node in AllNodes)
        {
            if (!ReferenceEquals(node, opened))
            {
                node.IsEditing = false;
            }
        }
    }

    internal void SetStatus(DevourmentEditNode node, string status)
    {
        if (node.EntryIndex < 0)
        {
            return;
        }

        State.SetStatus(node.EntryIndex, status);

        Apply(
            "devourment|status|" + node.EntityId,
            $"set the {node.DisplayName} to {status}",
            rebuild: false);
    }

    internal void SetFood(DevourmentEditNode node, string food)
    {
        if (node.EntryIndex < 0)
        {
            return;
        }

        State.SetFood(node.EntryIndex, food);

        Apply(
            "devourment|food|" + node.EntityId,
            $"set the {node.DisplayName} to {food} food",
            rebuild: false);
    }

    /// <summary>
    /// Setting the liking to zero, or clearing it, takes the whole relationship out and what the
    /// creature knew of the player with it, because that is what the game's own writer does.
    /// </summary>
    internal void SetFeeling(DevourmentEditNode node)
    {
        if (!node.IsCreature || node.EntityId.Length == 0)
        {
            return;
        }

        State.SetFeelingTowardPlayer(node.EntityId, node.LikesValue, node.KnowsValue);

        Apply(
            "devourment|feeling|" + node.EntityId,
            node.LikesValue is null or 0f
                ? $"made the {node.DisplayName} forget you"
                : $"set the {node.DisplayName} to like you {node.LikesValue.Value.ToString("0.##", CultureInfo.InvariantCulture)}",
            rebuild: false);
    }

    internal void SetTamed(DevourmentEditNode node, bool tamed)
    {
        if (!node.IsCreature || node.EntityId.Length == 0)
        {
            return;
        }

        State.SetTamed(node.EntityId, tamed);

        Apply(
            "devourment|tamed|" + node.EntityId,
            tamed ? $"tamed the {node.DisplayName}" : $"untamed the {node.DisplayName}",
            rebuild: false);
    }

    /// <summary>Safe to repeat: the entries are removed and written again from the state.</summary>
    private void Apply(string changeKey, string note, bool rebuild = true)
    {
        string body = State.Apply(_session.GetRecordBody(_campaign));

        _session.ReplaceRecordBody(_campaign, body, changeKey, note);

        // Read again, or a node's index and the field it addresses drift apart.
        State = DevourmentEditState.Read(_session.GetRecordBody(_campaign));

        if (rebuild)
        {
            Rebuild();
        }
        else
        {
            RefreshWarnings();
        }

        _changed();
    }

    /// <summary>Keeps which row is open, so an edit does not close the thing being edited.</summary>
    private void Rebuild()
    {
        string? wasEditing = AllNodes.FirstOrDefault(node => node.IsEditing)?.EntityId;

        Roots.Clear();

        var byPredator = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var order = new List<string>();
        var preyIds = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < State.Entries.Count; i++)
        {
            DevourmentEntry entry = State.Entries[i];

            // A field with no predator id gets a key of its own and comes out as a chain of one
            // rather than being dropped.
            string key = entry.PredatorId is { Length: > 0 } id
                ? id
                : " unlinked:" + i.ToString(CultureInfo.InvariantCulture);

            if (!byPredator.TryGetValue(key, out List<int>? carried))
            {
                carried = new List<int>();
                byPredator[key] = carried;
                order.Add(key);
            }

            carried.Add(i);

            if (entry.PreyId is { Length: > 0 } prey)
            {
                preyIds.Add(prey);
            }
        }

        var placed = new HashSet<string>(StringComparer.Ordinal);

        // A root is a predator nothing else is holding.
        foreach (string key in order)
        {
            if (!preyIds.Contains(key) && placed.Add(key))
            {
                Roots.Add(BuildRoot(key, byPredator, placed));
            }
        }

        // Anything left was only reachable through a loop, so it has no root to hang from.
        foreach (string key in order)
        {
            if (placed.Add(key))
            {
                Roots.Add(BuildRoot(key, byPredator, placed));
            }
        }

        if (wasEditing is not null)
        {
            DevourmentEditNode? again = AllNodes.FirstOrDefault(node => node.EntityId == wasEditing);

            if (again is not null)
            {
                again.IsEditing = true;
            }
        }

        RebuildPredators();
        RefreshWarnings();

        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(CountText));
    }

    private DevourmentEditNode BuildRoot(
        string key,
        Dictionary<string, List<int>> byPredator,
        HashSet<string> placed)
    {
        List<int> carried = byPredator[key];
        DevourmentEntry first = State.Entries[carried[0]];
        var ancestors = new HashSet<string>(StringComparer.Ordinal) { key };

        return new DevourmentEditNode(
            this,
            entryIndex: -1,
            first.Predator,
            first.PredatorId ?? "",
            first.PredatorType,
            isItem: false,
            isRoot: true,
            repeatsAncestor: false,
            depth: 0,
            BuildContents(carried, byPredator, placed, ancestors, depth: 1),
            entry: null);
    }

    private IReadOnlyList<DevourmentEditNode> BuildContents(
        List<int> carried,
        Dictionary<string, List<int>> byPredator,
        HashSet<string> placed,
        HashSet<string> ancestors,
        int depth)
    {
        var contents = new List<DevourmentEditNode>(carried.Count);

        foreach (int index in carried)
        {
            DevourmentEntry entry = State.Entries[index];
            string preyId = entry.PreyId ?? "";
            bool repeats = preyId.Length > 0 && ancestors.Contains(preyId);
            IReadOnlyList<DevourmentEditNode> inner = Array.Empty<DevourmentEditNode>();

            if (!repeats
                && preyId.Length > 0
                && byPredator.TryGetValue(preyId, out List<int>? nested)
                && placed.Add(preyId))
            {
                ancestors.Add(preyId);
                inner = BuildContents(nested, byPredator, placed, ancestors, depth + 1);
                ancestors.Remove(preyId);
            }

            contents.Add(new DevourmentEditNode(
                this,
                index,
                entry.Prey,
                preyId,
                entry.PreyType,
                entry.PreyIsItem,
                isRoot: false,
                repeats,
                depth,
                inner,
                entry));
        }

        return contents;
    }

    /// <summary>
    /// The player is offered even when the campaign has swallowed nothing, which is exactly when
    /// there is no entry to read a player blob out of, so one is built the way the game writes it.
    /// </summary>
    private void RebuildPredators()
    {
        var byId = new Dictionary<string, PredatorChoice>(StringComparer.Ordinal);

        Add(CreatureBlobBuilder.Build("Slugcat", CreatureBlobBuilder.PlayerEntityId, _denPos), "You");

        foreach (DevourmentEntry entry in State.Entries)
        {
            if (!entry.IsWellFormed)
            {
                continue;
            }

            Add(entry.Predator, null);

            if (!entry.PreyIsItem)
            {
                Add(entry.Prey, null);
            }
        }

        Predators.Clear();

        foreach (PredatorChoice choice in byId.Values)
        {
            Predators.Add(choice);
        }

        NewCreaturePredator = Predators.FirstOrDefault(p =>
            string.Equals(p.EntityId, CreatureBlobBuilder.PlayerEntityId, StringComparison.Ordinal))
            ?? Predators.FirstOrDefault();

        void Add(string blob, string? name)
        {
            string? id = DevourmentReader.CreatureIdOf(blob);

            if (id is null || byId.ContainsKey(id))
            {
                return;
            }

            string type = DevourmentReader.CreatureTypeOf(blob) ?? "";

            byId[id] = new PredatorChoice(
                blob,
                id,
                name ?? CreatureCatalog.ForName(type).DisplayName + " (" + id + ")");
        }
    }

    private string? _refusedMove;

    private void NoteRefusedMove(DevourmentEditNode moved, DevourmentEditNode target)
    {
        _refusedMove =
            $"The {target.DisplayName} is inside the {moved.DisplayName}, so putting one inside the other "
            + "would make a loop with no way out of it. Nothing was moved.";

        RefreshWarnings();
        _changed();
    }

    /// <summary>
    /// One line per expansion rather than one per creature. AbstractCreatureFromString logs
    /// "Unknown creature" and returns null for a name the game does not know, so the save still
    /// loads and only that one creature is lost.
    /// </summary>
    private void AddExpansionWarnings()
    {
        foreach (CreatureSource source in new[] { CreatureSource.Downpour, CreatureSource.Watcher })
        {
            if (Installed(source) || _arrivedWith.Contains(source))
            {
                continue;
            }

            var added = AllNodes
                .Where(node =>
                    node.IsCreature
                    && _added.Contains(node.EntityId)
                    && CreatureCatalog.IsKnown(node.RawType)
                    && CreatureCatalog.ForName(node.RawType).Source == source)
                .Select(node => node.DisplayName)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (added.Count == 0)
            {
                continue;
            }

            string expansion = source == CreatureSource.Watcher ? "The Watcher" : "Downpour";
            string names = string.Join(", ", added);

            Warnings.Add(_expansions.CheckedTheInstall
                ? $"{names} {(added.Count == 1 ? "comes" : "come")} from {expansion}, which is not installed. "
                    + "The game will not know the name, so it logs a warning and leaves that one out of the stomach."
                : $"{names} {(added.Count == 1 ? "comes" : "come")} from {expansion}, and nothing else in this "
                    + "campaign is from it. If the expansion is not on, the game leaves that one out of the stomach.");
        }
    }

    /// <summary>
    /// Two of the game's item types read their own fields at their own indices, which nothing here
    /// knows. Written with the base alone, the game's reader throws inside its own try and drops
    /// the object without a word.
    /// </summary>
    private void AddUnbuildableItemWarnings()
    {
        var dropped = AllNodes
            .Where(node =>
                node.IsItem
                && _added.Contains(node.EntityId)
                && ObjectCatalog.IsKnown(node.RawType)
                && !ObjectCatalog.ForName(node.RawType).CanBuild)
            .Select(node => node.DisplayName)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (dropped.Count == 0)
        {
            return;
        }

        Warnings.Add(
            $"{string.Join(", ", dropped)} {(dropped.Count == 1 ? "carries" : "carry")} fields this app does "
            + "not know how to write, so the game will fail to unpack "
            + (dropped.Count == 1 ? "it" : "them") + " and leave it out of the stomach. The rest of the save loads.");
    }

    private void RefreshWarnings()
    {
        Warnings.Clear();

        foreach (DevourmentEditNode node in AllNodes)
        {
            if (node.IsSwallowed && !DevourmentStatus.IsKnown(node.Status))
            {
                Warnings.Add(
                    $"{node.DisplayName} is set to \"{node.Status}\", which the mod does not know. It reads that "
                    + "back with Enum.Parse, so the save will not load until the name is one of the six.");
            }

            if (node.IsSwallowed && node.Food.Trim().Length > 0 && !int.TryParse(
                    node.Food.Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out _))
            {
                Warnings.Add($"{node.DisplayName} has food \"{node.Food}\", which the mod reads as a number.");
            }

            if (node.IsCreature && node.Likes.Trim().Length > 0 && node.LikesValue is null)
            {
                Warnings.Add($"{node.DisplayName} has a liking of \"{node.Likes}\", which is not a number, so it was left alone.");
            }

            if (node.IsCreature && node.LikesValue is 0f && node.KnowsValue is > 0f)
            {
                Warnings.Add(
                    $"{node.DisplayName} likes you zero, so the game writes no relationship at all and what it "
                    + "knows of you goes with it.");
            }

            if (!node.KnownToTheGame && node.IsWellFormed)
            {
                Warnings.Add($"{node.DisplayName} is not a creature this app knows of. If it came from a mod this is fine.");
            }

            if (node.RepeatsAncestor)
            {
                Warnings.Add(
                    $"{node.DisplayName} is already further up its own chain, so this save describes something "
                    + "holding itself. The chain is not followed round the loop.");
            }
        }

        AddExpansionWarnings();
        AddUnbuildableItemWarnings();

        if (_refusedMove is not null)
        {
            Warnings.Add(_refusedMove);
        }

        if (State.IdCounterWasMissing && State.Entries.Count > 0)
        {
            Warnings.Add(
                "This campaign has no NEXTID, so anything added here was numbered from the ids already in the "
                + "save. The game picks its own number when it finds no counter, which could land on the same one.");
        }

        OnPropertyChanged(nameof(HasWarnings));
    }
}
