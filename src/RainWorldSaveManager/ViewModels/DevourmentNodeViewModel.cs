// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists elsewhere in the referenced assembly, so a using written inside the namespace body
// would bind "System" to that namespace instead of the BCL root.
using System.Globalization;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using RainWorldSaveManager.Core.Saves.Models;

namespace RainWorldSaveManager.ViewModels;

/// <summary>
/// One entity in a Devourment stomach chain, and whatever it is carrying.
///
/// The save records a flat list of predator and prey pairs, so the fact that a swallowed lizard
/// is itself holding a spear, or that the player is the one inside something, only shows up once
/// the pairs are linked by entity id. This is the linked form, ready to nest in the panel.
/// </summary>
public sealed partial class DevourmentNodeViewModel : ObservableObject
{
    /// <summary>Shown when the save recorded no food value at all.</summary>
    private const string Missing = "-";

    public DevourmentNodeViewModel(DevourmentNode node, int depth)
    {
        Node = node;
        Depth = depth;
        Type = string.IsNullOrWhiteSpace(node.Type) ? "(unknown)" : node.Type;
        IsItem = node.IsItem;
        IsRoot = node.Status is null;
        RepeatsAncestor = node.RepeatsAncestor;

        Kind = IsRoot ? "" : node.IsItem ? "item" : "creature";
        StatusText = string.IsNullOrWhiteSpace(node.Status) ? "" : node.Status;

        // Items store -1 to mean they are worth no food. Printing that as a number reads like a
        // negative meal, so items say so in words instead.
        FoodText = IsRoot
            ? ""
            : node.IsItem
                ? "none"
                : node.FoodValue.HasValue
                    ? node.FoodValue.Value.ToString(CultureInfo.InvariantCulture)
                    : Missing;

        Contents = node.Contents.Select(child => new DevourmentNodeViewModel(child, depth + 1)).ToList();
        HasContents = Contents.Count > 0;
        ContentsSummary = HasContents
            ? "holds " + node.DescendantCount.ToString(CultureInfo.InvariantCulture)
            : "";

        IsExpanded = true;
        ToolTipText = BuildToolTip(node);
    }

    public DevourmentNode Node { get; }

    /// <summary>0 for a root. Used only to indent the row.</summary>
    public int Depth { get; }

    public string Type { get; }

    /// <summary>"item", "creature", or empty for a root, which is neither swallowed nor held.</summary>
    public string Kind { get; }

    public bool IsItem { get; }

    /// <summary>True when nothing in this save is holding it, so it is the outermost thing.</summary>
    public bool IsRoot { get; }

    public bool HasKind => Kind.Length > 0;

    public string StatusText { get; }

    public bool HasStatus => StatusText.Length > 0;

    public string FoodText { get; }

    public IReadOnlyList<DevourmentNodeViewModel> Contents { get; }

    public bool HasContents { get; }

    /// <summary>For example "holds 15", counting everything below at any depth.</summary>
    public string ContentsSummary { get; }

    /// <summary>
    /// Set when this entity also appears further up its own chain, which no save should describe.
    /// The row is drawn as a leaf and says so rather than being followed round the loop.
    /// </summary>
    public bool RepeatsAncestor { get; }

    public string ToolTipText { get; }

    [ObservableProperty]
    private bool isExpanded;

    /// <summary>Indent for this row, one step per level of nesting.</summary>
    public Thickness Indent => new(Depth * 16, 0, 0, 0);

    private static string BuildToolTip(DevourmentNode node)
    {
        var text = new StringBuilder();
        text.Append(string.IsNullOrWhiteSpace(node.Type) ? "(unknown)" : node.Type);

        if (node.Status is not null)
        {
            text.Append(", ");
            text.Append(node.IsItem ? "item" : "creature");
            text.Append(", ");
            text.Append(node.Status);
        }
        else
        {
            text.Append(", nothing in this save is holding it");
        }

        if (node.EntityId.Length > 0)
        {
            text.Append("\nEntity ");
            text.Append(node.EntityId);
        }

        if (node.HasContents)
        {
            text.Append("\nHolds ");
            text.Append(node.DescendantCount.ToString(CultureInfo.InvariantCulture));
            text.Append(node.DescendantCount == 1 ? " thing" : " things");
        }

        if (node.RepeatsAncestor)
        {
            text.Append("\nThis entity already appears higher up the same chain, so it is not followed again.");
        }

        return text.ToString();
    }
}
