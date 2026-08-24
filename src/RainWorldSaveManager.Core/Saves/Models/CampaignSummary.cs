// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Globalization;
using System.Text.Json.Serialization;
using RainWorldSaveManager.Core.Saves;

namespace RainWorldSaveManager.Core.Saves.Models;

/// <summary>
/// One campaign read out of a SAVE STATE record: which slugcat, how far the run has got, what it
/// has killed and unlocked, and what the Devourment mod recorded alongside it.
///
/// Every field is optional. The extractor is fail-soft, so a value the record did not carry, or
/// one that would not parse, arrives as null or as an empty collection. Collections are never
/// null, which lets the UI bind to them without a converter.
///
/// That last part holds through deserialisation too. A field initialiser alone would not: a
/// manifest.json carrying an explicit null for one of these arrays overwrites the initialiser,
/// so each collection stores through an init accessor that turns a null into an empty array.
/// </summary>
public sealed class CampaignSummary
{
    private readonly IReadOnlyList<EchoRecord> _echoes = Array.Empty<EchoRecord>();
    private readonly IReadOnlyList<string> _unlockedGates = Array.Empty<string>();
    private readonly IReadOnlyList<PassageRecord> _passages = Array.Empty<PassageRecord>();
    private readonly IReadOnlyList<KillRecord> _kills = Array.Empty<KillRecord>();
    private readonly IReadOnlyList<DevourmentRelationship> _devourmentStates
        = Array.Empty<DevourmentRelationship>();
    private readonly IReadOnlyList<string> _swallowedItems = Array.Empty<string>();
    private readonly IReadOnlyList<string> _heldItems = Array.Empty<string>();

    /// <summary>Value of the "SAV STATE NUMBER" field, for example White, Rivulet, Saint.</summary>
    public string SlugcatId { get; init; } = "";

    public int? CycleNum { get; init; }

    public int? Food { get; init; }

    /// <summary>Value of DENPOS, for example SU_S04.</summary>
    public string? DenPos { get; init; }

    public string? Seed { get; init; }

    /// <summary>Number of DEVOURMENTSTATE fields in the record.</summary>
    public int DevourmentStateCount { get; init; }

    /// <summary>True when the record carries a bare HASTHEGLOW flag.</summary>
    public bool HasGlow { get; init; }

    /// <summary>
    /// In-game name for <see cref="SlugcatId"/>, for example "Survivor" for White. An id the
    /// catalog does not know comes back as the raw id.
    ///
    /// Not serialised. It is read from the catalog, not from the save, and it has no setter, so
    /// writing it to manifest.json would record a value no reader can load back and freeze a
    /// catalog name the catalog may later correct.
    /// </summary>
    [JsonIgnore]
    public string DisplayName => SlugcatCatalog.ForId(SlugcatId).DisplayName;

    /// <summary>
    /// KARMA from DEATHPERSISTENTSAVEDATA, exactly as it sits on disk. This is a 0-based index,
    /// and it can sit outside 0..<see cref="KarmaCap"/>, in which case the game discards it on
    /// load. See <see cref="EffectiveKarma"/>.
    /// </summary>
    public int? Karma { get; init; }

    /// <summary>KARMACAP from DEATHPERSISTENTSAVEDATA, also a 0-based index.</summary>
    public int? KarmaCap { get; init; }

    /// <summary>REINFORCEDKARMA from DEATHPERSISTENTSAVEDATA: the karma flower state.</summary>
    public int? ReinforcedKarma { get; init; }

    /// <summary>
    /// <see cref="Karma"/> as the game holds it after loading this record.
    /// DeathPersistentSaveData.FromString ends with an unconditional clamp to 0..karmaCap, so a
    /// stored value outside that range never reaches play. A null cap leaves the upper bound
    /// unknown, so only the lower bound applies.
    ///
    /// Not serialised, for the same reason as <see cref="DisplayName"/>: it is derived from fields
    /// the manifest already records, and it has no setter.
    /// </summary>
    [JsonIgnore]
    public int? EffectiveKarma => KarmaMath.EffectiveKarma(Karma, KarmaCap);

    /// <summary>
    /// The karma level a player reads off the meter. The save stores a 0-based index, which
    /// HUD.KarmaMeter uses directly as a sprite number over smallKarma0 to smallKarma9, so the
    /// number on screen is one above the stored one. The +1 here is that offset, not a fudge.
    /// </summary>
    [JsonIgnore]
    public int? DisplayKarma => KarmaMath.DisplayKarma(Karma, KarmaCap);

    /// <summary>The karma cap a player sees, one above the 0-based <see cref="KarmaCap"/>.</summary>
    [JsonIgnore]
    public int? DisplayKarmaCap => KarmaMath.DisplayKarmaCap(KarmaCap);

    /// <summary>
    /// True when <see cref="Karma"/> differs from <see cref="EffectiveKarma"/>, so the value on
    /// disk is one the game will discard.
    /// </summary>
    [JsonIgnore]
    public bool KarmaStoredOutOfRange => KarmaMath.IsStoredOutOfRange(Karma, KarmaCap);

    /// <summary>
    /// Player-facing karma as "8 / 10", or "8" when the cap is unknown, or "-" when the record
    /// carried no karma at all.
    /// </summary>
    [JsonIgnore]
    public string KarmaText => KarmaMath.FormatKarma(Karma, KarmaCap);

    /// <summary>True when DEATHPERSISTENTSAVEDATA carries a bare HASTHEMARK flag.</summary>
    public bool HasTheMark { get; init; }

    /// <summary>True when DEATHPERSISTENTSAVEDATA carries a bare ASCENDED flag.</summary>
    public bool Ascended { get; init; }

    /// <summary>True when the record carries a bare HASROBO flag.</summary>
    public bool HasRobo { get; init; }

    /// <summary>
    /// True when the record carries a bare JUSTBEATGAME flag.
    ///
    /// Despite the name the game gives it, this is SaveState.skipNextCycleFoodDrain: a one cycle
    /// marker whose only reader is MoreSlugcats.PlayerNPCState.CycleTick, which skips one cycle of
    /// food drain. SaveState.SessionEnded clears it at the end of the next session, and
    /// Watcher.SpinningTop.MarkSpinningTopEncountered sets it on merely meeting Spinning Top, so
    /// it is not a record of having beaten the game. That record lives in PlayerProgression's
    /// miscProgressionData, which this app does not read.
    /// </summary>
    public bool JustBeatGame { get; init; }

    /// <summary>
    /// True when DEATHPERSISTENTSAVEDATA carries a bare REDSDEATH token.
    ///
    /// The token is not the flag. DeathPersistentSaveData.SaveToString writes it whenever the save
    /// is written as a death or a quit, whatever the flag holds, which is why eight of the nine
    /// campaigns in a real slot carry it. See <see cref="EffectiveRedsDeath"/>.
    /// </summary>
    public bool RedsDeathStored { get; init; }

    /// <summary>
    /// True when either REDEXTRACYCLES token is present. The game writes one in the SAVE STATE
    /// record and one in DEATHPERSISTENTSAVEDATA, and SaveState.RedExtraCycles is true when either
    /// is set, so this is the two of them together.
    /// </summary>
    public bool RedExtraCycles { get; init; }

    /// <summary>
    /// The redsDeath flag as the game holds it after loading this record. SaveState.LoadGame
    /// clears it while <see cref="CycleNum"/> is below Hunter's cycle limit.
    ///
    /// Not serialised, for the same reason as <see cref="DisplayName"/>: it is derived from fields
    /// the manifest already records, and it has no setter.
    /// </summary>
    [JsonIgnore]
    public bool EffectiveRedsDeath
        => RedsIllness.EffectiveRedsDeath(RedsDeathStored, CycleNum, RedExtraCycles);

    /// <summary>
    /// The cycle number the game puts on screen. Hunter is shown the cycles remaining rather than
    /// the cycles played, by both HUD.Map.CycleLabel and the save select menu, so for that one
    /// campaign this is the limit minus <see cref="CycleNum"/>.
    ///
    /// Not serialised, for the same reason as <see cref="EffectiveRedsDeath"/>.
    /// </summary>
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

    /// <summary>
    /// Every kill in <see cref="Kills"/> added up.
    ///
    /// Not serialised, for the same reason as <see cref="DisplayName"/>: it is derived from
    /// <see cref="Kills"/>, which the manifest already records, and it has no setter. A v1
    /// manifest has no kills at all, so a stored total there would contradict the file it sits in.
    /// </summary>
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

    /// <summary>
    /// Devourment relationships that parsed. This can be shorter than
    /// <see cref="DevourmentStateCount"/>, which counts every DEVOURMENTSTATE field including the
    /// ones this app could not read.
    /// </summary>
    public IReadOnlyList<DevourmentRelationship> DevourmentStates
    {
        get => _devourmentStates;
        init => _devourmentStates = value ?? Array.Empty<DevourmentRelationship>();
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

        if (Food.HasValue)
        {
            parts.Add("food " + Food.Value.ToString(CultureInfo.InvariantCulture));
        }

        return string.Join("  ", parts);
    }

    /// <summary>
    /// Playtime as "14h 28m", or "48m" under an hour. A null span gives an empty string, so a
    /// caller can bind it straight to a label.
    /// </summary>
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
