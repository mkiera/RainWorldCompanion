// RainWorldCompanion.Core.System exists in this assembly, so a using written inside the namespace
// body would bind "System" to that namespace instead of the BCL root.
using System.Globalization;
using System.Text.Json.Serialization;
using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Core.Saves.Models;

/// <summary>
/// One campaign read out of a SAVE STATE record. Every field is optional: a value the record did not
/// carry arrives as null or as an empty collection. Each collection stores through an init accessor
/// that turns a null into an empty array, because a manifest.json carrying an explicit null for one
/// of them would otherwise overwrite the field initialiser.
/// </summary>
public sealed class CampaignSummary
{
    private readonly IReadOnlyList<EchoRecord> _echoes = Array.Empty<EchoRecord>();
    private readonly IReadOnlyList<string> _unlockedGates = Array.Empty<string>();
    private readonly IReadOnlyList<PassageRecord> _passages = Array.Empty<PassageRecord>();
    private readonly IReadOnlyList<KillRecord> _kills = Array.Empty<KillRecord>();
    private readonly IReadOnlyList<DevourmentRelationship> _devourmentStates
        = Array.Empty<DevourmentRelationship>();
    private readonly IReadOnlyList<string> _friendIds = Array.Empty<string>();
    private readonly IReadOnlyList<string> _swallowedItems = Array.Empty<string>();
    private readonly IReadOnlyList<string> _heldItems = Array.Empty<string>();

    /// <summary>Value of the "SAV STATE NUMBER" field, for example White, Rivulet, Saint.</summary>
    public string SlugcatId { get; init; } = "";

    public int? CycleNum { get; init; }

    /// <summary>FOOD, exactly as it sits on disk. Routinely negative: SaveState.SessionEnded
    /// subtracts the shelter cost every cycle, so a cycle that banked less stores the shortfall.
    /// See <see cref="EffectiveFood"/>.</summary>
    public int? Food { get; init; }

    /// <summary>Value of DENPOS, for example SU_S04.</summary>
    public string? DenPos { get; init; }

    public string? Seed { get; init; }

    /// <summary>Number of DEVOURMENTSTATE fields in the record.</summary>
    public int DevourmentStateCount { get; init; }

    /// <summary>True when the record carries a bare HASTHEGLOW flag.</summary>
    public bool HasGlow { get; init; }

    /// <summary>In-game name, for example "Survivor" for White. An id the catalog does not know comes
    /// back as the raw id.</summary>
    [JsonIgnore]
    public string DisplayName => SlugcatCatalog.ForId(SlugcatId).DisplayName;

    /// <summary>KARMA from DEATHPERSISTENTSAVEDATA, a 0-based index. It can sit outside
    /// 0..<see cref="KarmaCap"/>, in which case the game discards it on load.</summary>
    public int? Karma { get; init; }

    /// <summary>KARMACAP from DEATHPERSISTENTSAVEDATA, also a 0-based index.</summary>
    public int? KarmaCap { get; init; }

    /// <summary>REINFORCEDKARMA from DEATHPERSISTENTSAVEDATA: the karma flower state.</summary>
    public int? ReinforcedKarma { get; init; }

    /// <summary>DeathPersistentSaveData.FromString ends with an unconditional clamp to 0..karmaCap.
    /// A null cap leaves the upper bound unknown, so only the lower bound applies.</summary>
    [JsonIgnore]
    public int? EffectiveKarma => KarmaMath.EffectiveKarma(Karma, KarmaCap);

    /// <summary>The karma level a player reads off the meter, which is one above the stored 0-based
    /// index because HUD.KarmaMeter uses it as a sprite number over smallKarma0 to smallKarma9.</summary>
    [JsonIgnore]
    public int? DisplayKarma => KarmaMath.DisplayKarma(Karma, KarmaCap);

    /// <summary>The karma cap a player sees, one above the 0-based <see cref="KarmaCap"/>.</summary>
    [JsonIgnore]
    public int? DisplayKarmaCap => KarmaMath.DisplayKarmaCap(KarmaCap);

    /// <summary>True when the value on disk is one the game will discard.</summary>
    [JsonIgnore]
    public bool KarmaStoredOutOfRange => KarmaMath.IsStoredOutOfRange(Karma, KarmaCap);

    /// <summary>"8 / 10", or "8" when the cap is unknown, or "-" when there was no karma at all.</summary>
    [JsonIgnore]
    public string KarmaText => KarmaMath.FormatKarma(Karma, KarmaCap);

    /// <summary><see cref="Food"/> as the pips a run starts with. The RainWorldGame constructor hands
    /// out food only while the stored number is above zero, so a negative is 0 pips.</summary>
    [JsonIgnore]
    public int? EffectiveFood => FoodMath.EffectiveFood(Food);

    /// <summary>True when the value on disk is one the game will not hand to the player.</summary>
    [JsonIgnore]
    public bool FoodStoredNegative => FoodMath.IsStoredNegative(Food);

    /// <summary>How many pips this slugcat's meter holds and what a shelter costs. Read from
    /// <see cref="SlugcatId"/>, not from the save, which stores neither.</summary>
    [JsonIgnore]
    public FoodMeter FoodMeter => FoodMath.MeterFor(SlugcatId);

    /// <summary>True when DEATHPERSISTENTSAVEDATA carries a bare HASTHEMARK flag.</summary>
    public bool HasTheMark { get; init; }

    /// <summary>True when DEATHPERSISTENTSAVEDATA carries a bare ASCENDED flag.</summary>
    public bool Ascended { get; init; }

    /// <summary>True when the record carries a bare HASROBO flag.</summary>
    public bool HasRobo { get; init; }

    /// <summary>True when the record carries a bare JUSTBEATGAME flag. Despite the name, this is
    /// SaveState.skipNextCycleFoodDrain, a one cycle marker that SpinningTop sets on merely meeting
    /// it, so it is not a record of having beaten the game.</summary>
    public bool JustBeatGame { get; init; }

    /// <summary>True when DEATHPERSISTENTSAVEDATA carries a bare REDSDEATH token. The token is not
    /// the flag: SaveToString writes it whenever the save is written as a death or a quit, whatever
    /// the flag holds. See <see cref="EffectiveRedsDeath"/>.</summary>
    public bool RedsDeathStored { get; init; }

    /// <summary>The game writes one REDEXTRACYCLES token in the SAVE STATE record and one in
    /// DEATHPERSISTENTSAVEDATA, and SaveState.RedExtraCycles is true when either is set.</summary>
    public bool RedExtraCycles { get; init; }

    /// <summary>SaveState.LoadGame clears the redsDeath flag while <see cref="CycleNum"/> is below
    /// Hunter's cycle limit.</summary>
    [JsonIgnore]
    public bool EffectiveRedsDeath
        => RedsIllness.EffectiveRedsDeath(RedsDeathStored, CycleNum, RedExtraCycles);

    /// <summary>Hunter is shown the cycles remaining rather than the cycles played, so for that one
    /// campaign this is the limit minus <see cref="CycleNum"/>.</summary>
    [JsonIgnore]
    public int? DisplayCycleNum => RedsIllness.DisplayCycle(SlugcatId, CycleNum, RedExtraCycles);

    public int? Deaths { get; init; }

    public int? Survives { get; init; }

    public int? Quits { get; init; }

    /// <summary>TOTFOOD: food eaten across the whole campaign, not the current cycle.</summary>
    public int? TotalFoodEaten { get; init; }

    /// <summary>TOTTIME, which the game stores in seconds.</summary>
    public TimeSpan? PlayTime { get; init; }

    /// <summary>CURRVERCYCLES: cycles played on the current game version.</summary>
    public int? CyclesThisVersion { get; init; }

    /// <summary>TIMELINE, the Watcher-era timeline point this campaign sits on.</summary>
    public string? Timeline { get; init; }

    /// <summary>LASTVDENPOS: the den before the current one.</summary>
    public string? LastDenPos { get; init; }

    /// <summary>Echoes met, from the GHOSTS field.</summary>
    public IReadOnlyList<EchoRecord> Echoes
    {
        get => _echoes;
        init => _echoes = value ?? Array.Empty<EchoRecord>();
    }

    /// <summary>Gate names from UNLOCKEDGATES, for example GATE_SU_HI.</summary>
    public IReadOnlyList<string> UnlockedGates
    {
        get => _unlockedGates;
        init => _unlockedGates = value ?? Array.Empty<string>();
    }

    /// <summary>Endgame passages from WINSTATE.</summary>
    public IReadOnlyList<PassageRecord> Passages
    {
        get => _passages;
        init => _passages = value ?? Array.Empty<PassageRecord>();
    }

    /// <summary>Per-creature kill counts from KILLS.</summary>
    public IReadOnlyList<KillRecord> Kills
    {
        get => _kills;
        init => _kills = value ?? Array.Empty<KillRecord>();
    }

    [JsonIgnore]
    public int TotalKills
    {
        get
        {
            int total = 0;
            for (int i = 0; i < Kills.Count; i++)
            {
                total += Kills[i].Count;
            }

            return total;
        }
    }

    /// <summary>The relationships that parsed, which can be fewer than
    /// <see cref="DevourmentStateCount"/>.</summary>
    public IReadOnlyList<DevourmentRelationship> DevourmentStates
    {
        get => _devourmentStates;
        init => _devourmentStates = value ?? Array.Empty<DevourmentRelationship>();
    }

    /// <summary>Entity ids from FRIENDS: the game's own record of taming, which is not the same as a
    /// high like value in social memory. A creature can like the player completely and not be here.</summary>
    public IReadOnlyList<string> FriendIds
    {
        get => _friendIds;
        init => _friendIds = value ?? Array.Empty<string>();
    }

    /// <summary>Item types from SWALLOWEDITEMS, for example PebblesPearl.</summary>
    public IReadOnlyList<string> SwallowedItems
    {
        get => _swallowedItems;
        init => _swallowedItems = value ?? Array.Empty<string>();
    }

    /// <summary>Item names from UNRECOGNIZEDPLAYERGRASPS, which is where Devourment bones sit.</summary>
    public IReadOnlyList<string> HeldItems
    {
        get => _heldItems;
        init => _heldItems = value ?? Array.Empty<string>();
    }

    /// <summary>Short single line for the UI, for example "White  cycle 17  food 3".</summary>
    public string Describe()
    {
        string name = string.IsNullOrEmpty(SlugcatId) ? UnknownSlugcat : SlugcatId;
        var parts = new List<string>(3) { name };

        if (CycleNum.HasValue)
        {
            parts.Add("cycle " + CycleNum.Value.ToString(CultureInfo.InvariantCulture));
        }

        // The pips the run starts with, not the raw field, which can be negative.
        if (EffectiveFood is { } food)
        {
            parts.Add("food " + food.ToString(CultureInfo.InvariantCulture));
        }

        return string.Join("  ", parts);
    }

    /// <summary>"14h 28m", or "48m" under an hour. A null span gives an empty string.</summary>
    public static string FormatPlayTime(TimeSpan? playTime)
    {
        if (playTime is not { } span || span < TimeSpan.Zero)
        {
            return "";
        }

        int hours = (int)span.TotalHours;
        int minutes = span.Minutes;

        if (hours > 0)
        {
            return hours.ToString(CultureInfo.InvariantCulture) + "h "
                + minutes.ToString(CultureInfo.InvariantCulture) + "m";
        }

        return minutes.ToString(CultureInfo.InvariantCulture) + "m";
    }

    /// <summary>Stand-in when a SAVE STATE record has no slugcat field.</summary>
    public const string UnknownSlugcat = "(unknown)";
}
