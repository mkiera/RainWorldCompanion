// Usings sit above the namespace: RainWorldCompanion.Core.System would otherwise shadow System.
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.Core.Saves.Models;
using RainWorldCompanion.Services;

namespace RainWorldCompanion.ViewModels;

/// <param name="Footnoted">
/// The tile shows a number the game derived rather than the one on disk, which draws an asterisk
/// pointing at <paramref name="Detail"/>.
/// </param>
public sealed record StatTile(
    string Label,
    string Value,
    bool IsMissing,
    string Detail = "",
    bool Footnoted = false)
{
    public string HoverText => Detail.Length == 0 ? Value : Detail;

    /// <summary>
    /// This value is not what the live slot holds. False when there was no live campaign to
    /// compare against, which is why <see cref="CampaignViewModel.ComparedToLive"/> exists: a
    /// campaign nobody compared must not read as one that matched.
    /// </summary>
    public bool DiffersFromLive { get; init; }
}

public sealed record BadgeTile(string Text, bool On)
{
    public bool DiffersFromLive { get; init; }
}

public sealed record ChipTile(string Text, string Detail);

/// <param name="ProgressText">
/// "5 / 5" against the passage's requirement, the stored tracker text for a passage this app has no
/// requirement for, and blank when nothing was recorded.
/// </param>
public sealed record PassageTile(
    string Name,
    string ProgressText,
    bool Available,
    bool Spent,
    string ToolTipText);

public sealed record KillTile(string Name, string CountText, string CreatureId);

/// <summary>
/// A campaign in a backup or a library save can be taken out and sent to a slot, but not edited or
/// removed where it is: both are copies taken at a moment, and changing one in place would leave it
/// no longer a copy of anything.
/// </summary>
/// <param name="FilePath">
/// Either a save container or a campaign file, which
/// <see cref="RainWorldCompanion.Core.Library.CampaignFile.ReadFrom"/> tells apart.
/// </param>
/// <param name="Label">What to call that file in a sentence, for example "backup 2026-08-24_120000".</param>
public sealed record CampaignSource(
    string FilePath,
    string Label,
    SaveSlotRef? LiveSlot,
    SaveRealm Realm = SaveRealm.Local,
    int SlotNumber = 0,
    string FileName = "")
{
    public bool IsLive => LiveSlot is not null;

    public bool CanBeTaken => FilePath.Length > 0;
}

public sealed class PortraitViewModel
{
    private static readonly Brush FallbackAccent = Freeze(Color.FromRgb(0x9E, 0x9E, 0x9E));

    public PortraitViewModel(SlugcatInfo info, ImageSource image, string? toolTipText = null)
    {
        SlugcatId = info.Id;
        DisplayName = info.DisplayName;
        Image = image;

        var colour = ParseColour(info.ColorHex);
        Accent = colour is null ? FallbackAccent : Freeze(colour.Value);
        ToolTipText = toolTipText ?? DefaultToolTip(info);
    }

    public string SlugcatId { get; }

    public string DisplayName { get; }

    /// <summary>The portrait from the game install, or the drawn stand-in. Never null.</summary>
    public ImageSource Image { get; }

    public Brush Accent { get; }

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
            return null;
        }
    }

    private static Brush Freeze(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// Everything is worked out in the constructor from a <see cref="CampaignSummary"/> already read
/// off disk. A value the save did not record shows as a dash, so a v1 backup manifest, which
/// recorded far less, still renders a complete card.
/// </summary>
public sealed partial class CampaignViewModel : ObservableObject
{
    /// <summary>What a value the save did not record looks like.</summary>
    public const string Missing = "-";

    private const int TopKillCount = 8;

    /// <param name="source">Null when nothing can be done with this campaign.</param>
    /// <param name="live">
    /// The same slugcat's campaign in the live slot this one would be written over, so each tile
    /// can say whether it differs. Null means there was nothing to compare against.
    /// </param>
    public CampaignViewModel(
        CampaignSummary campaign,
        ISlugcatIconProvider icons,
        CampaignSource? source = null,
        CampaignSummary? live = null)
    {
        Summary = campaign;
        Source = source;
        EditableSlot = source?.LiveSlot;

        var info = SlugcatCatalog.ForId(campaign.SlugcatId);
        Portrait = new PortraitViewModel(info, icons.GetIcon(campaign.SlugcatId));

        DisplayName = info.DisplayName;
        SlugcatId = campaign.SlugcatId;
        ShowSlugcatId = campaign.SlugcatId.Length > 0
            && !string.Equals(campaign.SlugcatId, info.DisplayName, StringComparison.OrdinalIgnoreCase);

        KarmaText = campaign.KarmaText;
        HasKarma = campaign.Karma.HasValue;
        KarmaStoredOutOfRange = campaign.KarmaStoredOutOfRange;
        KarmaToolTip = BuildKarmaToolTip(campaign);

        FoodToolTip = BuildFoodToolTip(campaign);

        // Hunter counts down, so the header shows the cycles it has left and the number on disk
        // goes in the tooltip.
        CycleText = campaign.DisplayCycleNum.HasValue
            ? "Cycle " + Number(campaign.DisplayCycleNum.Value)
            : "Cycle " + Missing;
        CycleToolTip = BuildCycleToolTip(campaign);

        DevourmentCount = campaign.DevourmentStateCount;
        HasDevourment = campaign.DevourmentStateCount > 0;
        DevourmentChipText = "Devourment " + Number(campaign.DevourmentStateCount);

        // Built for both sides and compared by what they say, rather than by a second list of
        // field comparisons kept beside them. A tile marked as differing is then differing in
        // exactly the way the reader can see.
        ComparedToLive = live is not null;

        RunStats = Mark(
            BuildRunStats(campaign, CycleToolTip, FoodToolTip),
            live is null ? null : BuildRunStats(live, "", ""));
        KarmaStats = Mark(
            BuildKarmaStats(campaign, KarmaToolTip),
            live is null ? null : BuildKarmaStats(live, ""));
        Badges = Mark(BuildBadges(campaign), live is null ? null : BuildBadges(live));
        ProgressStats = Mark(
            BuildProgressStats(campaign), live is null ? null : BuildProgressStats(live));

        Echoes = campaign.Echoes.Select(BuildEchoTile).ToList();
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

        DevourmentRoots = DevourmentTree
            .Build(campaign.DevourmentStates, campaign.FriendIds)
            .Select(node => new DevourmentNodeViewModel(node, 0))
            .ToList();
        SwallowedItems = campaign.SwallowedItems.Select(item => new ChipTile(item, "")).ToList();
        HeldItems = campaign.HeldItems.Select(item => new ChipTile(item, "")).ToList();
        UnreadDevourmentText = BuildUnreadDevourmentText(campaign);
    }

    public CampaignSummary Summary { get; }

    public PortraitViewModel Portrait { get; }

    public string DisplayName { get; }

    /// <summary>The raw id out of the save, for example "White" where the name is "Survivor".</summary>
    public string SlugcatId { get; }

    public bool ShowSlugcatId { get; }

    /// <summary>Karma as the meter reads it, for example "8 / 10", or a dash when unrecorded.</summary>
    public string KarmaText { get; }

    public bool HasKarma { get; }

    /// <summary>
    /// True when the number on disk is not the one the game plays with. The save is still normal,
    /// the game just clamps it on load.
    /// </summary>
    public bool KarmaStoredOutOfRange { get; }

    public string KarmaToolTip { get; }

    /// <summary>Blank unless the stored food is negative.</summary>
    public string FoodToolTip { get; }

    public string CycleText { get; }

    public string CycleToolTip { get; }

    public int DevourmentCount { get; }

    public bool HasDevourment { get; }

    public string DevourmentChipText { get; }

    [ObservableProperty]
    private bool isExpanded;

    public CampaignSource? Source { get; }

    public SaveSlotRef? EditableSlot { get; }

    public bool CanEdit => EditableSlot is not null;

    public bool CanBeTaken => Source?.CanBeTaken == true;

    public bool HasActions => CanEdit || CanBeTaken;

    /// <summary>
    /// Edit state hangs off the card rather than replacing the read-only tiles beside it, which are
    /// built once in the constructor, so turning editing off puts the card back as it was.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    private CampaignEditViewModel? edit;

    public bool IsEditing => Edit is not null;

    /// <summary>
    /// Whether a live campaign was found to compare against. Without it every tile reads as
    /// matching, which would be a claim nobody checked.
    /// </summary>
    public bool ComparedToLive { get; }

    /// <summary>Any tile or badge that is not what the live slot holds.</summary>
    public bool DiffersFromLive =>
        ComparedToLive
        && (RunStats.Any(tile => tile.DiffersFromLive)
            || KarmaStats.Any(tile => tile.DiffersFromLive)
            || ProgressStats.Any(tile => tile.DiffersFromLive)
            || Badges.Any(badge => badge.DiffersFromLive));

    /// <summary>Said only when it differs, the same way a slot's is.</summary>
    public string LiveComparisonText => DiffersFromLive ? "Differs from live" : "";

    public bool HasLiveComparisonText => LiveComparisonText.Length > 0;

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

    /// <summary>
    /// A root is something nothing else in this save is holding, usually the player but the
    /// predator when the player has been eaten.
    /// </summary>
    public IReadOnlyList<DevourmentNodeViewModel> DevourmentRoots { get; }

    public bool HasDevourmentRows => DevourmentRoots.Count > 0;

    public IReadOnlyList<ChipTile> SwallowedItems { get; }

    public bool HasSwallowedItems => SwallowedItems.Count > 0;

    public IReadOnlyList<ChipTile> HeldItems { get; }

    public bool HasHeldItems => HeldItems.Count > 0;

    /// <summary>
    /// Set when the record held DEVOURMENTSTATE fields this app could not read, so a count larger
    /// than the table does not look like a lost row.
    /// </summary>
    public string UnreadDevourmentText { get; }

    // Counts fields this app could not read, so the header still stands over the line reporting them.
    public bool HasAnyDevourment =>
        Summary.DevourmentStateCount > 0
        || DevourmentRoots.Count > 0
        || SwallowedItems.Count > 0
        || HeldItems.Count > 0;

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;

    private static IReadOnlyList<StatTile> Mark(
        IReadOnlyList<StatTile> mine, IReadOnlyList<StatTile>? live)
    {
        if (live is null)
        {
            return mine;
        }

        var theirs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tile in live)
        {
            theirs[tile.Label] = tile.Value;
        }

        return mine
            .Select(tile => theirs.TryGetValue(tile.Label, out var value) && !string.Equals(value, tile.Value, StringComparison.Ordinal)
                ? tile with { DiffersFromLive = true }
                : tile)
            .ToList();
    }

    private static IReadOnlyList<BadgeTile> Mark(
        IReadOnlyList<BadgeTile> mine, IReadOnlyList<BadgeTile>? live)
    {
        if (live is null)
        {
            return mine;
        }

        var theirs = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var badge in live)
        {
            theirs[badge.Text] = badge.On;
        }

        return mine
            .Select(badge => theirs.TryGetValue(badge.Text, out var on) && on != badge.On
                ? badge with { DiffersFromLive = true }
                : badge)
            .ToList();
    }

    private static IReadOnlyList<StatTile> BuildRunStats(
        CampaignSummary campaign, string cycleToolTip, string foodToolTip) => new[]
    {
        Tile("Cycle", campaign.DisplayCycleNum, cycleToolTip),
        Tile("Cycles this version", campaign.CyclesThisVersion),
        // The pips the run starts with, not the raw field.
        Tile("Food now", campaign.EffectiveFood, foodToolTip, campaign.FoodStoredNegative),
        Tile("Food eaten", campaign.TotalFoodEaten),
        Tile("Playtime", CampaignSummary.FormatPlayTime(campaign.PlayTime)),
        Tile("Shelter", campaign.DenPos),
        Tile("Last shelter", campaign.LastDenPos),
        Tile("Timeline", campaign.Timeline),
        Tile("Seed", campaign.Seed),
    };

    private static IReadOnlyList<StatTile> BuildKarmaStats(CampaignSummary campaign, string toolTip) => new[]
    {
        // The tiles carry the levels a player sees. The numbers on disk are in the tooltip.
        Tile("Karma", campaign.DisplayKarma, toolTip),
        Tile("Karma cap", campaign.DisplayKarmaCap, toolTip),
        ReinforcedKarmaTile(campaign.ReinforcedKarma),
    };

    /// <summary>
    /// DeathPersistentSaveData clamps karma to 0..cap every time it loads a save, so a stored 10
    /// under a cap of 9 is played as 9.
    /// </summary>
    private static string BuildKarmaToolTip(CampaignSummary campaign)
    {
        if (campaign.Karma is not { } stored)
        {
            return campaign.KarmaCap is { } storedCap
                ? "The save did not record karma. It stores cap " + Number(storedCap)
                    + ", counting from zero."
                : "The save did not record karma.";
        }

        // The stored numbers count from zero and the meter counts from one, so each sentence names
        // its scale. Watcher stores karma 5 under cap 4 and its meter shows 5 of 5.
        var storedLine = campaign.KarmaCap is { } cap
            ? "The save stores karma " + Number(stored) + " and cap " + Number(cap) + ", counting from zero."
            : "The save stores karma " + Number(stored) + " and no cap, counting from zero.";

        if (!campaign.KarmaStoredOutOfRange)
        {
            return storedLine;
        }

        var level = campaign.DisplayKarma is { } display ? Number(display) : Missing;
        var levelText = campaign.DisplayKarmaCap is { } displayCap
            ? level + " of " + Number(displayCap)
            : level;

        var rule = stored > campaign.EffectiveKarma
            ? "Rain World clamps karma to the cap when it loads, so the meter shows "
            : "Rain World lifts karma below zero to the lowest level when it loads, so the meter shows ";

        return storedLine + "\n" + rule + levelText + ".";
    }

    /// <summary>
    /// A negative is ordinary rather than damage. SaveState.SessionEnded takes the shelter cost off
    /// the pips banked at the end of every cycle, so a cycle that ended with none left stores the
    /// cost as a negative, and nothing lifts it back up on load.
    /// </summary>
    private static string BuildFoodToolTip(CampaignSummary campaign)
    {
        if (!campaign.FoodStoredNegative || campaign.Food is not { } stored)
        {
            return "";
        }

        var meter = campaign.FoodMeter;

        return "The save stores food " + Number(stored) + "."
            + "\nRain World hands out food only when the stored number is above zero,"
            + " so the run starts with 0 pips."
            + "\n" + SlugcatCatalog.ForId(campaign.SlugcatId).DisplayName
            + " pays " + Number(meter.PipsToHibernate)
            + " pips to hibernate, and the game takes that off at the end of every cycle, so a"
            + " cycle that banked less stores the shortfall.";
    }

    /// <summary>
    /// HUD.Map.CycleLabel and the save select menu both show Hunter RedsIllness.RedsCycles minus
    /// the stored number, so the header would otherwise disagree with both of them.
    /// </summary>
    private static string BuildCycleToolTip(CampaignSummary campaign)
    {
        if (campaign.CycleNum is not { } stored)
        {
            return "The save did not record a cycle number.";
        }

        var storedLine = "The save stores cycle " + Number(stored) + ".";

        if (!RedsIllness.IsHunter(campaign.SlugcatId))
        {
            return storedLine;
        }

        var limit = RedsIllness.RedsCycles(campaign.RedExtraCycles);
        var remaining = campaign.DisplayCycleNum is { } display ? Number(display) : Missing;

        return storedLine
            + "\nHunter has " + Number(limit) + " cycles, so the game counts down and shows "
            + remaining + ".";
    }

    private static StatTile ReinforcedKarmaTile(int? value) => value switch
    {
        null => new StatTile("Karma flower", Missing, true),
        0 => new StatTile("Karma flower", "No", false),
        1 => new StatTile("Karma flower", "Yes", false),
        _ => new StatTile("Karma flower", Number(value.Value), false),
    };

    /// <summary>
    /// Two badges do not carry their save field's name. JUSTBEATGAME serialises
    /// SaveState.skipNextCycleFoodDrain, which does only that and is cleared next session. REDSDEATH
    /// is written on every death or quit save, so the badge is offered to Hunter alone.
    /// </summary>
    private static IReadOnlyList<BadgeTile> BuildBadges(CampaignSummary campaign)
    {
        var badges = new List<BadgeTile>(6)
        {
            new("Mark of communication", campaign.HasTheMark),
            new("The glow", campaign.HasGlow),
            new("Ascended", campaign.Ascended),
            new("No food drain next cycle", campaign.JustBeatGame),
            new("Citizen ID drone", campaign.HasRobo),
        };

        if (RedsIllness.IsHunter(campaign.SlugcatId))
        {
            badges.Add(new BadgeTile("Hunter's death", campaign.EffectiveRedsDeath));
        }

        return badges;
    }

    /// <summary>
    /// The number after a region code is a state, not a tally: SaveState.GhostEncounter stores 2
    /// for an echo the player has spoken to and GhostHunch.Update stores 1 for one only sensed.
    /// </summary>
    private static ChipTile BuildEchoTile(EchoRecord echo) => echo.State switch
    {
        EchoRecord.TalkedTo => new ChipTile(echo.RegionCode, "talked to"),
        EchoRecord.Hunch => new ChipTile(echo.RegionCode, "sensed"),
        _ => new ChipTile(echo.RegionCode, Number(echo.State)),
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

    /// <summary>
    /// A passage this app knows reads as "12 / 12", but one from a mod carries its raw tracker
    /// text, which can run past forty characters. The full text stays in the tooltip.
    /// </summary>
    private const int MaxPassageProgressLength = 12;

    /// <summary>
    /// Menu.EndgameTokens offers a passage when the progress has reached the requirement and the
    /// consumed flag is not set, so that pair drives the chip rather than either one alone.
    /// </summary>
    private static PassageTile BuildPassageTile(PassageRecord passage)
    {
        var goal = passage.Goal;
        var available = goal.Fulfilled == true && !passage.Consumed;

        return new PassageTile(
            passage.Name,
            PassageProgressText(goal),
            available,
            passage.Consumed,
            PassageToolTip(passage, goal));
    }

    private static string PassageToolTip(PassageRecord passage, PassageGoal goal)
    {
        var lines = new List<string>(3) { passage.Name };

        if (passage.Consumed)
        {
            lines.Add("Already used to travel, so the game no longer offers it.");
        }
        else if (goal.Fulfilled == true)
        {
            lines.Add("Earned and unused.");
        }
        else if (goal.Fulfilled == false && goal.Needed is { } needed)
        {
            lines.Add("Needs " + Number(needed) + ".");
        }

        if (passage.Progress.Length != 0)
        {
            lines.Add("Stored progress: " + passage.Progress);
        }

        return string.Join("\n", lines);
    }

    private static string PassageProgressText(PassageGoal goal)
        => goal.Text.Length <= MaxPassageProgressLength
            ? goal.Text
            : goal.Text.Substring(0, MaxPassageProgressLength - 1) + "…";

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

    private static StatTile Tile(string label, int? value, string detail = "", bool footnoted = false)
        => value.HasValue
            ? new StatTile(label, Number(value.Value), false, detail, footnoted)
            : new StatTile(label, Missing, true, detail, footnoted);

    private static StatTile Tile(string label, string? value) => string.IsNullOrWhiteSpace(value)
        ? new StatTile(label, Missing, true)
        : new StatTile(label, value.Trim(), false);

    private static string Blank(string value) => string.IsNullOrWhiteSpace(value) ? Missing : value.Trim();

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
