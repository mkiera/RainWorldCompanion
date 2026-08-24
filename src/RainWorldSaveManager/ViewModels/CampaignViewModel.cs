// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists in the referenced assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RainWorldSaveManager.Core.Saves;
using RainWorldSaveManager.Core.Saves.Models;
using RainWorldSaveManager.Services;

namespace RainWorldSaveManager.ViewModels;

/// <summary>One labelled number or word in a detail group.</summary>
/// <param name="IsMissing">True when the save did not record the value, so it shows as a dash.</param>
public sealed record StatTile(string Label, string Value, bool IsMissing);

/// <summary>A flag that is either set or not, drawn filled when set and outlined when not.</summary>
public sealed record BadgeTile(string Text, bool On);

/// <summary>A small pill. <paramref name="Detail"/> is the trailing count, blank when there is none.</summary>
public sealed record ChipTile(string Text, string Detail);

/// <summary>
/// One endgame passage.
/// </summary>
/// <param name="CountText">
/// The trailing text on the chip: "x17" for a passage taken 17 times, the stored tracker text
/// for a value the save recorded in some other shape, and blank when there is no progress.
/// </param>
/// <param name="ToolTipText">The passage name and, when there is one, the tracker as stored.</param>
public sealed record PassageTile(string Name, string CountText, bool Earned, string ToolTipText);

/// <summary>One creature and how many of it this campaign has killed.</summary>
public sealed record KillTile(string Name, string CountText, string CreatureId);

/// <summary>One Devourment relationship, formatted for the table in the detail panel.</summary>
public sealed record DevourmentRow(
    string Predator,
    string Prey,
    string PreyKind,
    string Status,
    string FoodText,
    bool PreyIsItem);

/// <summary>
/// A slugcat's face and colours. Built once per campaign or per list row, so the icon lookup and
/// the brush parsing happen in one place instead of in a converter on every redraw.
///
/// The icon comes from <see cref="ISlugcatIconProvider"/> and is never null: a slugcat with no
/// portrait in the install, or no install at all, gets a drawn head in the same colour.
/// </summary>
public sealed class PortraitViewModel
{
    private static readonly Brush FallbackAccent = Freeze(Color.FromRgb(0x9E, 0x9E, 0x9E));
    private static readonly Brush FallbackAccentSoft = Freeze(Color.FromRgb(0xF0, 0xF0, 0xEE));

    public PortraitViewModel(SlugcatInfo info, ImageSource image, string? toolTipText = null)
    {
        SlugcatId = info.Id;
        DisplayName = info.DisplayName;
        Image = image;

        var colour = ParseColour(info.ColorHex);
        Accent = colour is null ? FallbackAccent : Freeze(colour.Value);
        AccentSoft = colour is null ? FallbackAccentSoft : Freeze(Soften(colour.Value));
        ToolTipText = toolTipText ?? DefaultToolTip(info);
    }

    public string SlugcatId { get; }

    public string DisplayName { get; }

    /// <summary>The portrait from the game install, or the drawn stand-in. Never null.</summary>
    public ImageSource Image { get; }

    /// <summary>The slugcat's own colour, used for thin accents.</summary>
    public Brush Accent { get; }

    /// <summary>The same colour washed out far enough to sit behind text.</summary>
    public Brush AccentSoft { get; }

    public string ToolTipText { get; }

    private static string DefaultToolTip(SlugcatInfo info) =>
        string.IsNullOrEmpty(info.Id) || string.Equals(info.Id, info.DisplayName, StringComparison.Ordinal)
            ? info.DisplayName
            : info.DisplayName + " (" + info.Id + ")";

    private static Color? ParseColour(string hex)
    {
        try
        {
            var converted = ColorConverter.ConvertFromString(hex);
            return converted is Color colour ? colour : null;
        }
        catch (Exception)
        {
            // A catalog entry from a mod could carry anything. A colour that will not parse is
            // not worth reporting: the neutral grey stands in for it.
            return null;
        }
    }

    private static Color Soften(Color colour) => Color.FromRgb(
        (byte)(colour.R + ((255 - colour.R) * 0.82)),
        (byte)(colour.G + ((255 - colour.G) * 0.82)),
        (byte)(colour.B + ((255 - colour.B) * 0.82)));

    private static Brush Freeze(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// One campaign inside a slot, as the detail panel shows it: a one line summary when collapsed
/// and four groups of detail when open.
///
/// Everything is worked out in the constructor from a <see cref="CampaignSummary"/> that has
/// already been read off disk. A value the save did not record shows as a dash rather than as a
/// gap, so a v1 backup manifest, which recorded far less, still renders a complete card.
/// </summary>
public sealed partial class CampaignViewModel : ObservableObject
{
    /// <summary>What a value the save did not record looks like.</summary>
    public const string Missing = "-";

    private const int TopKillCount = 8;

    public CampaignViewModel(CampaignSummary campaign, ISlugcatIconProvider icons)
    {
        Summary = campaign;

        var info = SlugcatCatalog.ForId(campaign.SlugcatId);
        Portrait = new PortraitViewModel(info, icons.GetIcon(campaign.SlugcatId));

        DisplayName = info.DisplayName;
        SlugcatId = campaign.SlugcatId;
        ShowSlugcatId = campaign.SlugcatId.Length > 0
            && !string.Equals(campaign.SlugcatId, info.DisplayName, StringComparison.OrdinalIgnoreCase);

        KarmaText = FormatKarma(campaign.Karma, campaign.KarmaCap);
        HasKarma = campaign.Karma.HasValue;
        CycleText = campaign.CycleNum.HasValue
            ? "Cycle " + Number(campaign.CycleNum.Value)
            : "Cycle " + Missing;

        DevourmentCount = campaign.DevourmentStateCount;
        HasDevourment = campaign.DevourmentStateCount > 0;
        DevourmentChipText = "Devourment " + Number(campaign.DevourmentStateCount);

        RunStats = BuildRunStats(campaign);
        KarmaStats = BuildKarmaStats(campaign);
        Badges = BuildBadges(campaign);
        ProgressStats = BuildProgressStats(campaign);

        Echoes = campaign.Echoes
            .Select(echo => new ChipTile(echo.RegionCode, "x" + Number(echo.Count)))
            .ToList();
        Gates = campaign.UnlockedGates.Select(gate => new ChipTile(gate, "")).ToList();
        GateCountText = Gates.Count == 0
            ? "No gates recorded"
            : Number(Gates.Count) + (Gates.Count == 1 ? " gate unlocked" : " gates unlocked");

        Passages = campaign.Passages.Select(BuildPassageTile).ToList();

        TopKills = campaign.Kills
            .OrderByDescending(kill => kill.Count)
            .ThenBy(kill => kill.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(TopKillCount)
            .Select(kill => new KillTile(kill.DisplayName, Number(kill.Count), kill.CreatureId))
            .ToList();
        KillSummaryText = BuildKillSummary(campaign);

        DevourmentRows = campaign.DevourmentStates.Select(BuildDevourmentRow).ToList();
        SwallowedItems = campaign.SwallowedItems.Select(item => new ChipTile(item, "")).ToList();
        HeldItems = campaign.HeldItems.Select(item => new ChipTile(item, "")).ToList();
        UnreadDevourmentText = BuildUnreadDevourmentText(campaign);
    }

    public CampaignSummary Summary { get; }

    public PortraitViewModel Portrait { get; }

    /// <summary>The in-game name, for example "Survivor".</summary>
    public string DisplayName { get; }

    /// <summary>The raw id out of the save, for example "White".</summary>
    public string SlugcatId { get; }

    /// <summary>False when the id and the display name are the same word, so it is not shown twice.</summary>
    public bool ShowSlugcatId { get; }

    public string KarmaText { get; }

    public bool HasKarma { get; }

    public string CycleText { get; }

    public int DevourmentCount { get; }

    public bool HasDevourment { get; }

    public string DevourmentChipText { get; }

    [ObservableProperty]
    private bool isExpanded;

    public IReadOnlyList<StatTile> RunStats { get; }

    public IReadOnlyList<StatTile> KarmaStats { get; }

    public IReadOnlyList<BadgeTile> Badges { get; }

    public IReadOnlyList<StatTile> ProgressStats { get; }

    public IReadOnlyList<ChipTile> Echoes { get; }

    public bool HasEchoes => Echoes.Count > 0;

    public IReadOnlyList<ChipTile> Gates { get; }

    public bool HasGates => Gates.Count > 0;

    public string GateCountText { get; }

    public IReadOnlyList<PassageTile> Passages { get; }

    public bool HasPassages => Passages.Count > 0;

    public IReadOnlyList<KillTile> TopKills { get; }

    public bool HasKills => TopKills.Count > 0;

    public string KillSummaryText { get; }

    public IReadOnlyList<DevourmentRow> DevourmentRows { get; }

    public bool HasDevourmentRows => DevourmentRows.Count > 0;

    public IReadOnlyList<ChipTile> SwallowedItems { get; }

    public bool HasSwallowedItems => SwallowedItems.Count > 0;

    public IReadOnlyList<ChipTile> HeldItems { get; }

    public bool HasHeldItems => HeldItems.Count > 0;

    /// <summary>
    /// Set when the record held DEVOURMENTSTATE fields this app could not read, so a count that
    /// is larger than the table is explained rather than looking like a lost row.
    /// </summary>
    public string UnreadDevourmentText { get; }

    /// <summary>
    /// True only when the record held nothing at all. The unread count is part of the test: a
    /// campaign with DEVOURMENTSTATE fields this app could not read has no rows to show, but
    /// saying it recorded nothing would contradict both the count on the collapsed header and
    /// <see cref="UnreadDevourmentText"/>.
    /// </summary>
    public bool HasNothingDevourment =>
        Summary.DevourmentStateCount == 0
        && DevourmentRows.Count == 0
        && SwallowedItems.Count == 0
        && HeldItems.Count == 0;

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;

    private static IReadOnlyList<StatTile> BuildRunStats(CampaignSummary campaign) => new[]
    {
        Tile("Cycle", campaign.CycleNum),
        Tile("Cycles this version", campaign.CyclesThisVersion),
        Tile("Food now", campaign.Food),
        Tile("Food eaten", campaign.TotalFoodEaten),
        Tile("Playtime", CampaignSummary.FormatPlayTime(campaign.PlayTime)),
        Tile("Shelter", campaign.DenPos),
        Tile("Last shelter", campaign.LastDenPos),
        Tile("Timeline", campaign.Timeline),
        Tile("Seed", campaign.Seed),
    };

    private static IReadOnlyList<StatTile> BuildKarmaStats(CampaignSummary campaign) => new[]
    {
        // Karma is stored as the game holds it. It can sit above the cap, and that is a real
        // state rather than a reading error, so nothing is clamped here.
        Tile("Karma", campaign.Karma),
        Tile("Karma cap", campaign.KarmaCap),
        ReinforcedKarmaTile(campaign.ReinforcedKarma),
    };

    private static StatTile ReinforcedKarmaTile(int? value) => value switch
    {
        null => new StatTile("Karma flower", Missing, true),
        0 => new StatTile("Karma flower", "No", false),
        1 => new StatTile("Karma flower", "Yes", false),
        _ => new StatTile("Karma flower", Number(value.Value), false),
    };

    private static IReadOnlyList<BadgeTile> BuildBadges(CampaignSummary campaign) => new[]
    {
        new BadgeTile("Mark of communication", campaign.HasTheMark),
        new BadgeTile("The glow", campaign.HasGlow),
        new BadgeTile("Ascended", campaign.Ascended),
        new BadgeTile("Beat the game", campaign.JustBeatGame),
        new BadgeTile("Citizen ID drone", campaign.HasRobo),
        new BadgeTile("Hunter's death", campaign.RedsDeath),
    };

    private static IReadOnlyList<StatTile> BuildProgressStats(CampaignSummary campaign) => new[]
    {
        Tile("Deaths", campaign.Deaths),
        Tile("Survives", campaign.Survives),
        Tile("Quits", campaign.Quits),
    };

    private static string BuildKillSummary(CampaignSummary campaign)
    {
        if (campaign.Kills.Count == 0)
        {
            return "No kills recorded";
        }

        var total = Number(campaign.TotalKills);
        var types = campaign.Kills.Count;
        var summary = total + (campaign.TotalKills == 1 ? " kill" : " kills")
            + " across " + Number(types) + (types == 1 ? " creature" : " creatures");

        return types > TopKillCount
            ? summary + ", top " + Number(TopKillCount) + " shown"
            : summary;
    }

    private static DevourmentRow BuildDevourmentRow(DevourmentRelationship state)
    {
        // Items store -1 to mean they are worth no food. Printing that as a number reads like a
        // negative meal, so items say so in words instead.
        var food = state.PreyIsItem
            ? "none"
            : state.FoodValue.HasValue ? Number(state.FoodValue.Value) : Missing;

        return new DevourmentRow(
            Blank(state.PredatorType),
            Blank(state.PreyType),
            state.PreyIsItem ? "item" : "creature",
            Blank(state.Status),
            food,
            state.PreyIsItem);
    }

    /// <summary>
    /// The longest tracker text a passage chip carries before it is cut short. Gourmand's stores
    /// one flag per slugcat, which runs to more than forty characters and would stretch the chip
    /// across the panel. The full text stays in the tooltip.
    /// </summary>
    private const int MaxPassageProgressLength = 12;

    private static PassageTile BuildPassageTile(PassageRecord passage)
    {
        var toolTip = passage.Progress.Length == 0
            ? passage.Name
            : passage.Name + "\nStored progress: " + passage.Progress;

        return new PassageTile(passage.Name, PassageCountText(passage), passage.Earned, toolTip);
    }

    private static string PassageCountText(PassageRecord passage)
    {
        if (passage.Count > 0)
        {
            return "x" + Number(passage.Count);
        }

        // A tracker that read as an int is already in Count, so a zero there means zero progress
        // and the chip stays bare. Anything else is a float or a dotted flag string, which is
        // real progress the count cannot hold, so the stored text is shown instead of nothing.
        if (passage.Progress.Length == 0
            || int.TryParse(passage.Progress, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return "";
        }

        return passage.Progress.Length <= MaxPassageProgressLength
            ? passage.Progress
            : passage.Progress.Substring(0, MaxPassageProgressLength - 1) + "…";
    }

    private static string BuildUnreadDevourmentText(CampaignSummary campaign)
    {
        var unread = campaign.DevourmentStateCount - campaign.DevourmentStates.Count;
        if (unread <= 0)
        {
            return "";
        }

        return unread == 1
            ? "1 more relationship was recorded in a shape this app could not read."
            : Number(unread) + " more relationships were recorded in a shape this app could not read.";
    }

    private static string FormatKarma(int? karma, int? cap)
    {
        if (!karma.HasValue && !cap.HasValue)
        {
            return Missing;
        }

        var left = karma.HasValue ? Number(karma.Value) : Missing;
        var right = cap.HasValue ? Number(cap.Value) : Missing;
        return left + " / " + right;
    }

    private static StatTile Tile(string label, int? value) => value.HasValue
        ? new StatTile(label, Number(value.Value), false)
        : new StatTile(label, Missing, true);

    private static StatTile Tile(string label, string? value) => string.IsNullOrWhiteSpace(value)
        ? new StatTile(label, Missing, true)
        : new StatTile(label, value.Trim(), false);

    private static string Blank(string value) => string.IsNullOrWhiteSpace(value) ? Missing : value.Trim();

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
