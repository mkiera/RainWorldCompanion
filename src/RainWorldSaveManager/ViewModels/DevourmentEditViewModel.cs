// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists in the referenced assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RainWorldSaveManager.Core.Editing;
using RainWorldSaveManager.Core.Saves;
using RainWorldSaveManager.Views.Behaviors;

namespace RainWorldSaveManager.ViewModels;

/// <summary>A creature that could be given something to swallow, as the picker lists it.</summary>
/// <param name="Blob">The serialized creature, which is what an entry stores.</param>
public sealed record PredatorChoice(string Blob, string EntityId, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// What a campaign has swallowed, as the same nested chains the read-only panel draws.
///
/// The nesting is the thing worth seeing, so editing keeps it rather than flattening it out. What
/// a save actually stores is a flat list of predator and prey pairs, and the chains are implied by
/// one entity id being prey in one pair and predator in another. That makes moving something into
/// something else an edit to one field's predator, which is what a drop does.
///
/// Every edit goes into the campaign's <see cref="SaveEditSession"/> as it is made, the same way
/// the boxes above do, so cancelling the editor is still a matter of dropping the object.
/// </summary>
public sealed partial class DevourmentEditViewModel : ObservableObject, IReorderable
{
    private readonly SaveEditSession _session;
    private readonly CampaignRecordRef _campaign;
    private readonly string _denPos;
    private readonly Action _changed;

    public DevourmentEditViewModel(
        SaveEditSession session,
        CampaignRecordRef campaign,
        string denPos,
        Action changed)
    {
        _session = session;
        _campaign = campaign;
        _denPos = denPos.Trim();
        _changed = changed;

        State = DevourmentEditState.Read(session.GetRecordBody(campaign));

        Roots = new ObservableCollection<DevourmentEditNode>();
        Predators = new ObservableCollection<PredatorChoice>();
        Warnings = new ObservableCollection<string>();

        Rebuild();
    }

    internal DevourmentEditState State { get; private set; }

    /// <summary>The outermost things, each holding whatever is inside it.</summary>
    public ObservableCollection<DevourmentEditNode> Roots { get; }

    /// <summary>Every creature in the campaign that something could be put inside.</summary>
    public ObservableCollection<PredatorChoice> Predators { get; }

    /// <summary>What the edits will do that may not be expected. Advice, never a refusal.</summary>
    public ObservableCollection<string> Warnings { get; }

    public bool HasWarnings => Warnings.Count > 0;

    public bool HasRows => State.Entries.Count > 0;

    /// <summary>Every node in every chain, for the checks that do not care about shape.</summary>
    public IEnumerable<DevourmentEditNode> AllNodes => Roots.SelectMany(root => root.Flatten());

    public string CountText => State.Entries.Count switch
    {
        0 => "Nothing swallowed",
        1 => "1 thing swallowed",
        int count => count.ToString(CultureInfo.InvariantCulture) + " things swallowed",
    };

    // ---- adding ----

    [ObservableProperty]
    private string newCreatureSearch = "";

    [ObservableProperty]
    private PredatorChoice? newCreaturePredator;

    /// <summary>Creatures matching what has been typed. Anything can be typed, matched or not.</summary>
    public IEnumerable<CreatureKind> CreatureMatches => CreatureCatalog.Search(NewCreatureSearch).Take(14);

    partial void OnNewCreatureSearchChanged(string value) => OnPropertyChanged(nameof(CreatureMatches));

    /// <summary>Puts a creature in the chosen stomach, giving it an id nothing else is using.</summary>
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
        NewCreatureSearch = "";

        Apply(
            "devourment|added|" + id,
            $"put a {CreatureCatalog.ForName(name).DisplayName} inside {predator.DisplayName}");
    }

    /// <summary>
    /// Takes one thing out of the chain. Whatever it was holding moves up to whatever was holding
    /// it, because the alternative is quietly removing everything below as well.
    /// </summary>
    [RelayCommand]
    private void RemoveNode(DevourmentEditNode? node)
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

    // ---- order and nesting ----

    /// <summary>What a drop means here: the thing dragged goes inside the thing it was dropped on.</summary>
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
            // The only way this fails on a real gesture is a loop, which is worth saying rather
            // than letting the row snap back with no reason given.
            NoteRefusedMove(moved, target);
            return;
        }

        _refusedMove = null;

        Apply(
            "devourment|moved|" + moved.EntityId,
            $"moved the {moved.DisplayName} inside {target.DisplayName}");
    }

    [RelayCommand]
    private void MoveUp(DevourmentEditNode? node) => Shuffle(node, -1);

    [RelayCommand]
    private void MoveDown(DevourmentEditNode? node) => Shuffle(node, 1);

    /// <summary>
    /// Moves a row past the one beside it, among the things sharing its stomach. The arrows change
    /// order without changing what holds what, which is the half of the job a drag does not do.
    /// </summary>
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

    // ---- one row ----

    /// <summary>Closes every other row's editors, so one is open at a time.</summary>
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
    /// Sets what one creature feels about the player, in every copy of it the record holds.
    ///
    /// An empty box means the number is not written, which the game reads as zero. Setting the
    /// liking to zero, or clearing it, takes the whole relationship out and what the creature knew
    /// of the player with it, because that is what the game's own writer does. The warning says so.
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

    // ---- writing it into the session ----

    /// <summary>
    /// Pushes the state into the record and rebuilds what is on screen from it.
    ///
    /// Applying is safe to repeat: the entries are removed and written again from what the state
    /// holds, so putting the same state over an already-written record gives the same record back.
    /// </summary>
    private void Apply(string changeKey, string note, bool rebuild = true)
    {
        string body = State.Apply(_session.GetRecordBody(_campaign));

        _session.ReplaceRecordBody(_campaign, body, changeKey, note);

        // The state was read from the record before this edit, so it is read again from what the
        // record holds now. Otherwise a node's index and the field it addresses drift apart.
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

    // ---- building the tree ----

    /// <summary>
    /// Builds the chains from the flat list, the same way the read-only panel does, keeping which
    /// row is open so an edit does not close the thing being edited.
    /// </summary>
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

            // A field with no predator id cannot be linked to anything, so it gets a key of its
            // own and comes out as a chain of one rather than being dropped.
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

        // Anything left was only reachable through a loop, so it has no root to hang from. Promote
        // it rather than letting a malformed save hide rows.
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
    /// Everything a creature could be put inside: every creature the entries mention, in either
    /// slot, plus the player.
    ///
    /// The player is offered even when the campaign has swallowed nothing, because that is exactly
    /// when there is no entry to read a player blob out of, and it is the stomach most edits are
    /// about. The blob written for them is the one the game writes: the slugcat, the id the game
    /// gives player one, and the shelter the campaign is sitting in.
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

    // ---- advice ----

    private string? _refusedMove;

    private void NoteRefusedMove(DevourmentEditNode moved, DevourmentEditNode target)
    {
        _refusedMove =
            $"The {target.DisplayName} is inside the {moved.DisplayName}, so putting one inside the other "
            + "would make a loop with no way out of it. Nothing was moved.";

        RefreshWarnings();
        _changed();
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
