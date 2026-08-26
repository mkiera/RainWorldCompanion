// Usings sit above the namespace declaration on purpose. RainWorldCompanion.Core.System
// exists in the referenced assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.Core.Saves.Models;
using RainWorldCompanion.Services;

namespace RainWorldCompanion.ViewModels;

/// <summary>One labelled number or word in a detail group.</summary>
/// <param name="IsMissing">True when the save did not record the value, so it shows as a dash.</param>
/// <param name="Detail">
/// Extra explanation to show on hover. Blank for most tiles, which hover with the value itself so a
/// value the tile clipped can still be read in full.
/// </param>
/// <param name="Footnoted">
/// True when the tile shows a number the game derived rather than the one on disk, which draws an
/// asterisk pointing at <paramref name="Detail"/>. The same mark the karma chip carries.
/// </param>
public sealed record StatTile(
    string Label,
    string Value,
    bool IsMissing,
    string Detail = "",
    bool Footnoted = false)
{
    /// <summary>What hovering the tile shows.</summary>
    public string HoverText => Detail.Length == 0 ? Value : Detail;
}

/// <summary>A flag that is either set or not, drawn filled when set and outlined when not.</summary>
public sealed record BadgeTile(string Text, bool On);

/// <summary>A small pill. <paramref name="Detail"/> is the trailing count, blank when there is none.</summary>
public sealed record ChipTile(string Text, string Detail);

/// <summary>
/// One endgame passage.
/// </summary>
/// <param name="ProgressText">
/// The trailing text on the chip: "5 / 5" against the passage's requirement, the stored tracker
/// text for a passage this app has no requirement for, and blank when nothing was recorded.
/// </param>
/// <param name="Available">
/// True when the run has met the requirement and has not spent the passage, which is exactly when
/// Menu.EndgameTokens draws a token for it in game.
/// </param>
/// <param name="Spent">True when the passage has already been used to travel.</param>
/// <param name="ToolTipText">The passage name, what it needs, and the tracker as stored.</param>
public sealed record PassageTile(
    string Name,
    string ProgressText,
    bool Available,
    bool Spent,
    string ToolTipText);

/// <summary>One creature and how many of it this campaign has killed.</summary>
public sealed record KillTile(string Name, string CountText, string CreatureId);

/// <summary>
/// Where a campaign was read from, and so what can be done with it.
///
/// The same card is drawn for a campaign in the live save folder, one in a backup and one in a
/// library save. All three can be taken out and sent to a slot, because that is a read of the file
/// they are in and a write to a different one. Only the live folder can be edited or have a campaign
/// removed: a backup and a library save are copies taken at a moment, and changing one in place
/// would leave it no longer a copy of anything.
/// </summary>
/// <param name="FilePath">
/// The file holding this campaign. Either a save container or a campaign file, which
/// <see cref="RainWorldCompanion.Core.Library.CampaignFile.ReadFrom"/> tells apart.
/// </param>
/// <param name="Label">What to call that file in a sentence, for example "backup 2026-08-24_120000".</param>
/// <param name="LiveSlot">The slot this is in, when it is one of the game's own. Null otherwise.</param>
/// <param name="Realm">Which set the campaign came from, recorded when it is stored.</param>
/// <param name="SlotNumber">The slot number it came from, or 0 when it came from no numbered slot.</param>
public sealed record CampaignSource(
    string FilePath,
    string Label,
    SaveSlotRef? LiveSlot,
    SaveRealm Realm = SaveRealm.Local,
    int SlotNumber = 0,
    string FileName = "")
{
    /// <summary>True when this is a live slot, the only place an edit can be written.</summary>
    public bool IsLive => LiveSlot is not null;

    /// <summary>True when the campaign can be read back out of the file it is in.</summary>
    public bool CanBeTaken => FilePath.Length > 0;
}

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

    /// <param name="source">
    /// The file this campaign was read out of, or null when nothing can be done with it. A backup
    /// and a library save pass one that is not live: their campaigns can be taken out and sent to a
    /// slot, but not edited or removed where they are.
    /// </param>
    public CampaignViewModel(CampaignSummary campaign, ISlugcatIconProvider icons, CampaignSource? source = null)
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

        // Karma is shown the way the game shows it: the stored number clamped to the cap, then
        // read as a level rather than as the 0-based index the save holds. CampaignSummary does
        // that arithmetic, the panel only formats it.
        KarmaText = campaign.KarmaText;
        HasKarma = campaign.Karma.HasValue;
        KarmaStoredOutOfRange = campaign.KarmaStoredOutOfRange;
        KarmaToolTip = BuildKarmaToolTip(campaign);

        // Food is shown the same way: the pips the run will start with, not the raw field, which
        // the game leaves negative whenever a cycle banked less than a shelter costs.
        FoodToolTip = BuildFoodToolTip(campaign);

        // Hunter counts down. The game shows that campaign the cycles it has left, so the header
        // and the Cycle tile do too, and the number on disk goes in the tooltip.
        CycleText = campaign.DisplayCycleNum.HasValue
            ? "Cycle " + Number(campaign.DisplayCycleNum.Value)
            : "Cycle " + Missing;
        CycleToolTip = BuildCycleToolTip(campaign);

        DevourmentCount = campaign.DevourmentStateCount;
        HasDevourment = campaign.DevourmentStateCount > 0;
        DevourmentChipText = "Devourment " + Number(campaign.DevourmentStateCount);

        RunStats = BuildRunStats(campaign, CycleToolTip, FoodToolTip);
        KarmaStats = BuildKarmaStats(campaign, KarmaToolTip);
        Badges = BuildBadges(campaign);
        ProgressStats = BuildProgressStats(campaign);

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

    /// <summary>The in-game name, for example "Survivor".</summary>
    public string DisplayName { get; }

    /// <summary>The raw id out of the save, for example "White".</summary>
    public string SlugcatId { get; }

    /// <summary>False when the id and the display name are the same word, so it is not shown twice.</summary>
    public bool ShowSlugcatId { get; }

    /// <summary>Karma as the meter reads it, for example "8 / 10", or a dash when unrecorded.</summary>
    public string KarmaText { get; }

    public bool HasKarma { get; }

    /// <summary>
    /// True when the number on disk is not the one the game plays with, so the chip carries a mark
    /// pointing at the tooltip. The save is still normal, the game just clamps it on load.
    /// </summary>
    public bool KarmaStoredOutOfRange { get; }

    /// <summary>The stored numbers, and what the game makes of them when they need explaining.</summary>
    public string KarmaToolTip { get; }

    /// <summary>
    /// What the Food now tile shows on hover: blank unless the stored number is negative, in which
    /// case it gives that number and what the game does with it.
    /// </summary>
    public string FoodToolTip { get; }

    public string CycleText { get; }

    /// <summary>What the save stores under CYCLENUM, and for Hunter what the game counts from it.</summary>
    public string CycleToolTip { get; }

    public int DevourmentCount { get; }

    public bool HasDevourment { get; }

    public string DevourmentChipText { get; }

    [ObservableProperty]
    private bool isExpanded;

    /// <summary>The file this campaign was read out of, or null when nothing can be done with it.</summary>
    public CampaignSource? Source { get; }

    /// <summary>The slot this campaign lives in, when it is one that can be edited.</summary>
    public SaveSlotRef? EditableSlot { get; }

    /// <summary>True when an Edit button belongs on this card, which is the live save folder only.</summary>
    public bool CanEdit => EditableSlot is not null;

    /// <summary>
    /// True when this campaign can be taken out and sent somewhere, which is anywhere it can be
    /// read from: the live folder, a backup, or a library save.
    /// </summary>
    public bool CanBeTaken => Source?.CanBeTaken == true;

    /// <summary>
    /// True when there is a bar of buttons to draw at all. Editing needs a live slot and a live slot
    /// is a file, so anything that can be edited can also be taken, and this is the wider of the two.
    /// </summary>
    public bool HasActions => CanEdit || CanBeTaken;

    /// <summary>
    /// The open editor for this campaign, or null when it is only being read.
    ///
    /// The read-only tiles beside this are built once in the constructor and never change, which
    /// the rest of the panel depends on. Edit state hangs off the card rather than replacing any
    /// of them, so turning editing off puts the card back exactly as it was.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    private CampaignEditViewModel? edit;

    public bool IsEditing => Edit is not null;

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
    /// The stomach chains, outermost first. A root is something nothing else in this save is
    /// holding, which is usually the player but is the predator when the player has been eaten.
    /// </summary>
    public IReadOnlyList<DevourmentNodeViewModel> DevourmentRoots { get; }

    public bool HasDevourmentRows => DevourmentRoots.Count > 0;

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
        && DevourmentRoots.Count == 0
        && SwallowedItems.Count == 0
        && HeldItems.Count == 0;

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;

    private static IReadOnlyList<StatTile> BuildRunStats(
        CampaignSummary campaign, string cycleToolTip, string foodToolTip) => new[]
    {
        Tile("Cycle", campaign.DisplayCycleNum, cycleToolTip),
        Tile("Cycles this version", campaign.CyclesThisVersion),
        // The pips the run starts with. A campaign whose stored number the game will not use gets
        // the asterisk, same as karma.
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
    /// The karma tooltip: what the save holds, and, when the game will not use that number, what
    /// it uses instead. DeathPersistentSaveData clamps karma to 0..cap every time it loads a save,
    /// so a stored 10 under a cap of 9 is played as 9.
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

        // The stored numbers count from zero and the numbers on the meter count from one. Saying
        // which scale each one is on is what keeps the two sentences from reading as a comparison:
        // Watcher stores karma 5 under cap 4 and its meter shows 5 of 5, and without the scales
        // named that pair reads as though the clamp did nothing and the cap grew.
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
    /// The food tooltip, blank for almost every campaign because the stored number is the number
    /// the run starts with and the tile already shows it.
    ///
    /// A negative is the case worth explaining, and it is ordinary rather than damage.
    /// SaveState.SessionEnded takes the shelter cost off the pips banked at the end of every cycle,
    /// so a cycle that ended with none left stores the cost as a negative. Nothing lifts it back
    /// up on load: the RainWorldGame constructor hands out food only while the stored number is
    /// above zero, and the save select screen clamps it to the meter before drawing.
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
    /// The cycle tooltip. Every slugcat gets the stored number, and Hunter gets the sum the game
    /// does to it: HUD.Map.CycleLabel and the save select menu both show that campaign
    /// RedsIllness.RedsCycles minus the stored number, so the header would otherwise disagree with
    /// both of them.
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
    /// The flags the card draws. Two of them do not say what their save field is called.
    ///
    /// JUSTBEATGAME serialises SaveState.skipNextCycleFoodDrain, which is read by one method that
    /// skips a cycle of food drain and is cleared at the end of the next session, so the badge says
    /// that rather than claiming the campaign has beaten the game.
    ///
    /// REDSDEATH only means anything in Hunter's campaign, and the token is written on every death
    /// or quit save whatever the flag holds, so the badge is only offered to Hunter and reads the
    /// flag the way SaveState.LoadGame leaves it.
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
    /// One echo chip. The number after a region code is a state, not a tally:
    /// SaveState.GhostEncounter stores 2 for an echo the player has spoken to and GhostHunch.Update
    /// stores 1 for one the player has only sensed. Nothing adds to it.
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
    /// The longest progress text a passage chip carries before it is cut short. A passage this app
    /// knows reads as "12 / 12" and fits, but one from a mod carries its raw tracker text, which
    /// runs to more than forty characters where the mod stores a flag per slugcat. The full text
    /// stays in the tooltip.
    /// </summary>
    private const int MaxPassageProgressLength = 12;

    /// <summary>
    /// One passage chip. The save records progress and a consumed flag, and neither one on its own
    /// is what a player sees: Menu.EndgameTokens offers a passage when the progress has reached the
    /// requirement and the flag is not set, so that pair drives the chip.
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
