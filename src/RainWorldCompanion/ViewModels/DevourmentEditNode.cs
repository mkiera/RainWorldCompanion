// Usings sit above the namespace: RainWorldCompanion.Core.System would otherwise shadow System.
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RainWorldCompanion.Core.Editing;
using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.ViewModels;

/// <summary>
/// A node stands for one DEVOURMENTSTATE field, addressed by position, except a root, which stands
/// for a creature nothing is holding. A root has no status or food of its own, because those belong
/// to being swallowed, but it can still be tamed and still remembers the player.
/// </summary>
public sealed partial class DevourmentEditNode : ObservableObject
{
    private readonly DevourmentEditViewModel _owner;

    private bool _loading = true;

    public DevourmentEditNode(
        DevourmentEditViewModel owner,
        int entryIndex,
        string blob,
        string entityId,
        string type,
        bool isItem,
        bool isRoot,
        bool repeatsAncestor,
        int depth,
        IReadOnlyList<DevourmentEditNode> children,
        DevourmentEntry? entry)
    {
        _owner = owner;

        EntryIndex = entryIndex;
        Blob = blob;
        EntityId = entityId;
        IsItem = isItem;
        IsRoot = isRoot;
        RepeatsAncestor = repeatsAncestor;
        Depth = depth;
        Children = children;
        IsWellFormed = entry?.IsWellFormed ?? true;

        DisplayName = type.Length == 0
            ? "(unknown)"
            : isItem
                ? ObjectCatalog.ForName(type).DisplayName
                : CreatureCatalog.ForName(type).DisplayName;

        RawType = type;
        KnownToTheGame = isItem ? ObjectCatalog.IsKnown(type) : CreatureCatalog.IsKnown(type);

        status = entry?.Status ?? "";
        food = entry?.Food ?? "";

        CreatureBlobBuilder.Relation? toward = IsCreature && entityId.Length > 0
            ? owner.State.FeelingTowardPlayer(entityId)
            : null;

        likes = Text(toward?.Like);
        knows = Text(toward?.Know);
        isTamed = IsCreature && entityId.Length > 0 && owner.State.IsTamed(entityId);

        isExpanded = true;

        _loading = false;
    }

    /// <summary>Which DEVOURMENTSTATE field this is, or -1 for a root, which is no field at all.</summary>
    public int EntryIndex { get; }

    /// <summary>The serialized creature or item, which is what a drop target hands over.</summary>
    public string Blob { get; }

    public string EntityId { get; }

    /// <summary>The name as the file spells it, for the tooltip and for a mod's own creature.</summary>
    public string RawType { get; }

    public string DisplayName { get; }

    public bool IsItem { get; }

    /// <summary>True when nothing in this campaign is holding it.</summary>
    public bool IsRoot { get; }

    /// <summary>False for a field this app could not split into the four parts the mod writes.</summary>
    public bool IsWellFormed { get; }

    /// <summary>False for a creature name the catalog does not carry, which a mod can add.</summary>
    public bool KnownToTheGame { get; }

    /// <summary>
    /// Set when this entity is already further up its own chain, which no save should describe. The
    /// row is drawn as a leaf and says so rather than being followed round the loop.
    /// </summary>
    public bool RepeatsAncestor { get; }

    public int Depth { get; }

    public Thickness Indent => new(Depth * 16, 0, 0, 0);

    public IReadOnlyList<DevourmentEditNode> Children { get; }

    public bool HasChildren => Children.Count > 0;

    public string ContentsSummary => HasChildren
        ? "holds " + CountBelow().ToString(CultureInfo.InvariantCulture)
        : "";

    /// <summary>Items have no opinion of anybody and cannot be tamed.</summary>
    public bool IsCreature => !IsItem && IsWellFormed;

    /// <summary>A root is not swallowed, so it has no state to be in and no food left in it.</summary>
    public bool IsSwallowed => !IsRoot && IsWellFormed;

    public bool CanHoldThings => !IsItem && EntityId.Length > 0;

    public IReadOnlyList<string> StatusChoices => DevourmentStatus.All;

    [ObservableProperty]
    private bool isExpanded;

    /// <summary>One row at a time, so the chain stays readable while a part of it is worked on.</summary>
    [ObservableProperty]
    private bool isEditing;

    [ObservableProperty]
    private string status;

    [ObservableProperty]
    private string food;

    [ObservableProperty]
    private string likes;

    [ObservableProperty]
    private string knows;

    [ObservableProperty]
    private bool isTamed;

    partial void OnIsEditingChanged(bool value)
    {
        if (value)
        {
            _owner.OnlyThisRowIsEditing(this);
        }
    }

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

    public float? LikesValue => Number(Likes);

    public float? KnowsValue => Number(Knows);

    // These commands live on the node because a row reaches them through its own DataContext.
    // Going up the visual tree finds the parent node's ItemsControl, which has no commands on it,
    // and the binding then resolves to nothing and the button quietly does nothing.

    [RelayCommand]
    private void Remove() => _owner.RemoveNode(this);

    [RelayCommand]
    private void MoveUp() => _owner.MoveUp(this);

    [RelayCommand]
    private void MoveDown() => _owner.MoveDown(this);

    /// <summary>Every node below this one, at any depth, this one included.</summary>
    public IEnumerable<DevourmentEditNode> Flatten()
    {
        yield return this;

        foreach (DevourmentEditNode child in Children)
        {
            foreach (DevourmentEditNode below in child.Flatten())
            {
                yield return below;
            }
        }
    }

    private int CountBelow() => Children.Sum(child => 1 + child.CountBelow());

    internal static float? Number(string text)
        => float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            ? value
            : null;

    private static string Text(float? value)
        => value?.ToString(CultureInfo.InvariantCulture) ?? "";
}
