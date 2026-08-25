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
/// One swallowed thing, as a row that can be edited.
///
/// A row stands for one DEVOURMENTSTATE field, so a creature swallowed twice over has a row each
/// time. What it feels about the player belongs to the creature rather than to the row, though, so
/// setting it goes through the edit state, which writes every copy.
/// </summary>
public sealed partial class DevourmentRowViewModel : ObservableObject
{
    private readonly DevourmentEditViewModel _owner;

    private bool _loading = true;

    public DevourmentRowViewModel(DevourmentEditViewModel owner, DevourmentEntry entry, int index)
    {
        _owner = owner;
        Entry = entry;
        Index = index;

        IsItem = entry.PreyIsItem;
        IsWellFormed = entry.IsWellFormed;
        EntityId = entry.PreyId ?? "";

        DisplayName = IsWellFormed
            ? CreatureCatalog.ForName(entry.PreyType).DisplayName
            : "A field this app cannot read";

        PredatorName = IsWellFormed
            ? CreatureCatalog.ForName(entry.PredatorType).DisplayName
            : "";

        PredatorId = entry.PredatorId ?? "";
        KnownToTheGame = IsItem || CreatureCatalog.IsKnown(entry.PreyType);

        status = entry.Status;
        food = entry.Food;

        CreatureBlobBuilder.Relation? toward = IsItem ? null : owner.State.FeelingTowardPlayer(EntityId);
        likes = Text(toward?.Like);
        knows = Text(toward?.Know);

        isTamed = !IsItem && EntityId.Length > 0 && owner.State.IsTamed(EntityId);

        _loading = false;
    }

    public DevourmentEntry Entry { get; }

    /// <summary>Where the row sits in the list, which is the order the record stores it in.</summary>
    public int Index { get; }

    /// <summary>The number a person counts from, shown at the front of the row.</summary>
    public string Position => (Index + 1).ToString(CultureInfo.InvariantCulture);

    public string DisplayName { get; }

    public string EntityId { get; }

    public string PredatorId { get; }

    /// <summary>What is holding it, for the line under the name.</summary>
    public string PredatorName { get; }

    public bool IsItem { get; }

    /// <summary>False for a field this app could not split into the four parts the mod writes.</summary>
    public bool IsWellFormed { get; }

    /// <summary>False for a creature name the catalog does not carry, which a mod can add.</summary>
    public bool KnownToTheGame { get; }

    /// <summary>Items have no opinion of anybody and cannot be tamed.</summary>
    public bool IsCreature => !IsItem && IsWellFormed;

    public IReadOnlyList<string> StatusChoices => DevourmentStatus.All;

    [ObservableProperty]
    private string status;

    [ObservableProperty]
    private string food;

    /// <summary>How much it likes the player, as text so it can be cleared.</summary>
    [ObservableProperty]
    private string likes;

    /// <summary>How well it knows the player.</summary>
    [ObservableProperty]
    private string knows;

    [ObservableProperty]
    private bool isTamed;

    partial void OnStatusChanged(string value)
    {
        if (!_loading)
        {
            _owner.SetStatus(this, value);
        }
    }

    partial void OnFoodChanged(string value)
    {
        if (!_loading)
        {
            _owner.SetFood(this, value);
        }
    }

    partial void OnLikesChanged(string value)
    {
        if (!_loading)
        {
            _owner.SetFeeling(this);
        }
    }

    partial void OnKnowsChanged(string value)
    {
        if (!_loading)
        {
            _owner.SetFeeling(this);
        }
    }

    partial void OnIsTamedChanged(bool value)
    {
        if (!_loading)
        {
            _owner.SetTamed(this, value);
        }
    }

    /// <summary>The value as a number, or null when the box is empty or holds something else.</summary>
    public float? LikesValue => Number(Likes);

    public float? KnowsValue => Number(Knows);

    internal static float? Number(string text)
        => float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            ? value
            : null;

    private static string Text(float? value)
        => value?.ToString(CultureInfo.InvariantCulture) ?? "";
}

/// <summary>
/// The Devourment state of one campaign, as a flat list in the order the record stores it.
///
/// The read-only panel beside this one draws the same data as a tree, because what a person wants
/// to see is what is inside what. Editing wants the opposite: stored order is the order, dragging a
/// row moves it there, and a row is one field rather than one place in a chain.
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

        Rows = new ObservableCollection<DevourmentRowViewModel>();
        Predators = new ObservableCollection<PredatorChoice>();
        Warnings = new ObservableCollection<string>();

        Rebuild();
    }

    internal DevourmentEditState State { get; private set; }

    public ObservableCollection<DevourmentRowViewModel> Rows { get; }

    /// <summary>Every creature in the campaign that something could be put inside.</summary>
    public ObservableCollection<PredatorChoice> Predators { get; }

    /// <summary>What the edits will do that may not be expected. Advice, never a refusal.</summary>
    public ObservableCollection<string> Warnings { get; }

    public bool HasWarnings => Warnings.Count > 0;

    public bool HasRows => Rows.Count > 0;

    public string CountText => Rows.Count switch
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

    [RelayCommand]
    private void RemoveRow(DevourmentRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        State.RemoveAt(row.Index);

        Apply(
            "devourment|removed|" + row.EntityId + "|" + row.Index.ToString(CultureInfo.InvariantCulture),
            $"took the {row.DisplayName} out");
    }

    // ---- order ----

    [RelayCommand]
    private void MoveUp(DevourmentRowViewModel? row) => Move(row, -1);

    [RelayCommand]
    private void MoveDown(DevourmentRowViewModel? row) => Move(row, 1);

    private void Move(DevourmentRowViewModel? row, int by)
    {
        if (row is null)
        {
            return;
        }

        MoveTo(row.Index, row.Index + by);
    }

    /// <summary>
    /// Moves a row, which is what dropping one somewhere comes to. Out of range does nothing rather
    /// than clamping, so a drag that ends off the end of the list leaves it alone.
    /// </summary>
    public void MoveTo(int from, int to)
    {
        if (from == to || to < 0 || to >= Rows.Count)
        {
            return;
        }

        State.Move(from, to);

        // One line however many times rows are dragged about, because the order is one thing.
        Apply("devourment|order", "changed the order of what is swallowed");
    }

    // ---- one row ----

    internal void SetStatus(DevourmentRowViewModel row, string status)
    {
        State.SetStatus(row.Index, status);

        Apply(
            "devourment|status|" + row.EntityId,
            $"set the {row.DisplayName} to {status}",
            rebuild: false);
    }

    internal void SetFood(DevourmentRowViewModel row, string food)
    {
        State.SetFood(row.Index, food);

        Apply(
            "devourment|food|" + row.EntityId,
            $"set the {row.DisplayName} to {food} food",
            rebuild: false);
    }

    /// <summary>
    /// Sets what one creature feels about the player, in every copy of it the record holds.
    ///
    /// An empty box means the number is not written, which the game reads as zero. Setting the
    /// liking to zero, or clearing it, takes the whole relationship out and what the creature knew
    /// of the player with it, because that is what the game's own writer does. The warning says so.
    /// </summary>
    internal void SetFeeling(DevourmentRowViewModel row)
    {
        if (!row.IsCreature || row.EntityId.Length == 0)
        {
            return;
        }

        State.SetFeelingTowardPlayer(row.EntityId, row.LikesValue, row.KnowsValue);

        Apply(
            "devourment|feeling|" + row.EntityId,
            row.LikesValue is null or 0f
                ? $"made the {row.DisplayName} forget you"
                : $"set the {row.DisplayName} to like you {row.LikesValue.Value.ToString("0.##", CultureInfo.InvariantCulture)}",
            rebuild: false);
    }

    internal void SetTamed(DevourmentRowViewModel row, bool tamed)
    {
        if (!row.IsCreature || row.EntityId.Length == 0)
        {
            return;
        }

        State.SetTamed(row.EntityId, tamed);

        Apply(
            "devourment|tamed|" + row.EntityId,
            tamed ? $"tamed the {row.DisplayName}" : $"untamed the {row.DisplayName}",
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
        // record holds now. Otherwise a row's index and the field it addresses drift apart.
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

    private void Rebuild()
    {
        Rows.Clear();

        for (int i = 0; i < State.Entries.Count; i++)
        {
            Rows.Add(new DevourmentRowViewModel(this, State.Entries[i], i));
        }

        RebuildPredators();
        RefreshWarnings();

        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(CountText));
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

    private void RefreshWarnings()
    {
        Warnings.Clear();

        foreach (DevourmentRowViewModel row in Rows)
        {
            if (row.IsWellFormed && !DevourmentStatus.IsKnown(row.Status))
            {
                Warnings.Add(
                    $"{row.DisplayName} is set to \"{row.Status}\", which the mod does not know. It reads that "
                    + "back with Enum.Parse, so the save will not load until the name is one of the six.");
            }

            if (row.IsWellFormed && row.Food.Trim().Length > 0 && !int.TryParse(
                    row.Food.Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out _))
            {
                Warnings.Add($"{row.DisplayName} has food \"{row.Food}\", which the mod reads as a number.");
            }

            if (row.IsCreature && row.Likes.Trim().Length > 0 && row.LikesValue is null)
            {
                Warnings.Add($"{row.DisplayName} has a liking of \"{row.Likes}\", which is not a number, so it was left alone.");
            }

            if (row.IsCreature && row.LikesValue is 0f && row.KnowsValue is > 0f)
            {
                Warnings.Add(
                    $"{row.DisplayName} likes you zero, so the game writes no relationship at all and what it "
                    + "knows of you goes with it.");
            }

            if (!row.KnownToTheGame && row.IsWellFormed)
            {
                Warnings.Add($"{row.DisplayName} is not a creature this app knows of. If it came from a mod this is fine.");
            }
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
