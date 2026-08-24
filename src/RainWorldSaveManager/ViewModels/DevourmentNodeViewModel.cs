// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists elsewhere in the referenced assembly, so a using written inside the namespace body
// would bind "System" to that namespace instead of the BCL root.
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using RainWorldSaveManager.Core.Saves;
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

        IsTamedFriend = node.IsTamedFriend;

        DevourmentEntity? detail = node.Detail;
        PearlType = detail?.PearlType ?? "";
        PearlInfo = PearlCatalog.ForId(PearlType);
        HasPearl = PearlType.Length > 0;
        IsLorePearl = PearlInfo?.IsLore == true;
        PearlBrush = BuildPearlBrush(PearlInfo);

        // Five Pebbles' own pearls all carry the type name PebblesPearl, so repeating it beside an
        // item already called PebblesPearl says nothing. The number is what tells them apart.
        PearlLabel = detail?.PebblesPearlNumber is { } pebblesNumber
            ? "no " + pebblesNumber.ToString(CultureInfo.InvariantCulture)
            : PearlType;

        MeatText = detail?.MeatLeft is { } meat
            ? "meat " + meat.ToString(CultureInfo.InvariantCulture)
            : "";

        SocialRelationship? toward = detail?.TowardPlayer;
        LikeValue = toward?.Like;
        SocialText = BuildSocialText(toward);
        SpearText = BuildSpearText(detail?.Spear);

        IsExpanded = true;
        ToolTipText = BuildToolTip(node, detail, toward);
    }

    /// <summary>True when this creature is on the campaign's FRIENDS list.</summary>
    public bool IsTamedFriend { get; }

    /// <summary>The stored DataPearlType, for example SL_moon. Empty for anything else.</summary>
    public string PearlType { get; }

    public PearlCatalog.PearlInfo? PearlInfo { get; }

    public bool HasPearl { get; }

    /// <summary>False for the generic Misc and Broadcast pickups, which carry no lore.</summary>
    public bool IsLorePearl { get; }

    /// <summary>The colour the game paints this pearl, or a muted grey when it is not known.</summary>
    public Brush PearlBrush { get; }

    /// <summary>What the pearl chip reads: the pearl type, or the number for a Pebbles pearl.</summary>
    public string PearlLabel { get; }

    public string MeatText { get; }

    public bool HasMeat => MeatText.Length > 0;

    /// <summary>For example "likes you 1.00". Empty when the creature remembers nothing.</summary>
    public string SocialText { get; }

    public bool HasSocial => SocialText.Length > 0;

    /// <summary>Negative means it dislikes the player.</summary>
    public float? LikeValue { get; }

    public bool DislikesPlayer => LikeValue is < 0f;

    /// <summary>For example "explosive". Empty for an ordinary spear or anything else.</summary>
    public string SpearText { get; }

    public bool HasSpear => SpearText.Length > 0;

    private static Brush BuildPearlBrush(PearlCatalog.PearlInfo? info)
    {
        if (info is not null)
        {
            try
            {
                var converted = (Color)ColorConverter.ConvertFromString(info.ColorHex);
                var brush = new SolidColorBrush(converted);
                brush.Freeze();
                return brush;
            }
            catch (FormatException)
            {
                // fall through to the neutral brush
            }
        }

        var fallback = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));
        fallback.Freeze();
        return fallback;
    }

    private static string BuildSocialText(SocialRelationship? toward)
    {
        if (toward?.Like is not { } like)
        {
            return "";
        }

        string verb = like < 0f ? "dislikes you" : "likes you";
        return verb + " " + like.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private static string BuildSpearText(SpearState? spear)
    {
        if (spear is null || !spear.IsSpecial)
        {
            return "";
        }

        var parts = new List<string>(4);
        if (spear.Explosive) { parts.Add("explosive"); }
        if (spear.Electric) { parts.Add("electric"); }
        if (spear.Needle) { parts.Add("needle"); }
        if (spear.Poison > 0f) { parts.Add("poison"); }

        return string.Join(", ", parts);
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

    private static string BuildToolTip(
        DevourmentNode node,
        DevourmentEntity? detail,
        SocialRelationship? toward)
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

        if (node.IsTamedFriend)
        {
            text.Append("\nOn this campaign's friends list, so the game keeps it with you between cycles.");
        }

        if (detail?.PearlType is { Length: > 0 } pearl)
        {
            text.Append("\nPearl type ");
            text.Append(pearl);
            PearlCatalog.PearlInfo? info = PearlCatalog.ForId(pearl);
            if (info is not null)
            {
                text.Append(info.IsLore ? ", carries lore" : ", a generic pearl with no lore");
            }
        }

        if (toward is not null)
        {
            text.Append("\nSocial memory of you:");
            AppendValue(text, " like ", toward.Like);
            AppendValue(text, " fear ", toward.Fear);
            AppendValue(text, " know ", toward.Know);
            text.Append("\nThat is how it feels about you, which is not the same as being tamed.");
        }

        if (detail?.MeatLeft is { } meat)
        {
            text.Append("\n");
            text.Append(meat.ToString(CultureInfo.InvariantCulture));
            text.Append(" meat left, so something has already eaten from it.");
        }

        if (node.RepeatsAncestor)
        {
            text.Append("\nThis entity already appears higher up the same chain, so it is not followed again.");
        }

        return text.ToString();
    }

    private static void AppendValue(StringBuilder text, string label, float? value)
    {
        if (value is not { } number)
        {
            return;
        }

        text.Append(label);
        text.Append(number.ToString("0.00", CultureInfo.InvariantCulture));
    }
}
