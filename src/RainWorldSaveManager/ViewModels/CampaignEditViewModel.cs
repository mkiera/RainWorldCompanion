// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists in the referenced assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RainWorldSaveManager.Core.Editing;
using RainWorldSaveManager.Core.Saves;
using RainWorldSaveManager.Core.Saves.Models;

namespace RainWorldSaveManager.ViewModels;

/// <summary>
/// One echo, and what this campaign knows about it.
///
/// Every echo the game has appears, not only the ones the save mentions, because setting one to
/// sensed is exactly the edit a player who has never met it wants to make.
/// </summary>
public sealed class EchoEditRow : ObservableObject
{
    private readonly Action<EchoEditRow>? _changed;
    private int _state;

    public EchoEditRow(string regionCode, string displayName, int state, bool knownToTheGame, Action<EchoEditRow>? changed)
    {
        RegionCode = regionCode;
        DisplayName = displayName;
        KnownToTheGame = knownToTheGame;
        StoredState = state;
        _state = state;
        _changed = changed;
    }

    public string RegionCode { get; }

    public string DisplayName { get; }

    /// <summary>False for a region code found in the save that no known region matches.</summary>
    public bool KnownToTheGame { get; }

    /// <summary>What the save held when the editor opened.</summary>
    public int StoredState { get; }

    /// <summary>
    /// What the player knows about this echo, and the only thing the row stores.
    ///
    /// The three booleans below are views onto this rather than three fields of their own. Three
    /// fields would only ever agree with each other while a radio group was on screen keeping them
    /// in step, which would leave the row correct in the window and wrong everywhere else.
    /// </summary>
    public int State
    {
        get => _state;
        set
        {
            if (_state == value)
            {
                return;
            }

            _state = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NeverSeen));
            OnPropertyChanged(nameof(Sensed));
            OnPropertyChanged(nameof(TalkedTo));
            _changed?.Invoke(this);
        }
    }

    /// <summary>
    /// Setting one of these on moves the row to that state. Setting one off does nothing: a radio
    /// group clears the old button before it sets the new one, and acting on the clear would write
    /// "never seen" on the way to every other value.
    /// </summary>
    public bool NeverSeen
    {
        get => State == DeathPersistentEditor.EchoNeverSeen;
        set => Choose(value, DeathPersistentEditor.EchoNeverSeen);
    }

    public bool Sensed
    {
        get => State == DeathPersistentEditor.EchoSensed;
        set => Choose(value, DeathPersistentEditor.EchoSensed);
    }

    public bool TalkedTo
    {
        get => State == DeathPersistentEditor.EchoTalkedTo;
        set => Choose(value, DeathPersistentEditor.EchoTalkedTo);
    }

    private void Choose(bool value, int state)
    {
        if (value)
        {
            State = state;
        }
    }
}

/// <summary>One gate, and whether this campaign has opened it.</summary>
public sealed partial class GateEditRow : ObservableObject
{
    private readonly Action<GateEditRow>? _changed;

    public GateEditRow(string name, string displayName, bool unlocked, bool knownToTheGame, Action<GateEditRow>? changed)
    {
        Name = name;
        DisplayName = displayName;
        KnownToTheGame = knownToTheGame;
        _changed = changed;
        unlockedField = unlocked;
    }

    public string Name { get; }

    /// <summary>The two regions the gate joins, or the raw name when they are not known.</summary>
    public string DisplayName { get; }

    /// <summary>False for a gate name found in the save that the catalog does not hold.</summary>
    public bool KnownToTheGame { get; }

    [ObservableProperty]
    private bool unlockedField;

    partial void OnUnlockedFieldChanged(bool value) => _changed?.Invoke(this);
}

/// <summary>One flag, with where it is written hidden behind the label a person reads.</summary>
public sealed partial class FlagEditRow : ObservableObject
{
    private readonly Action<bool>? _changed;

    public FlagEditRow(string label, string detail, bool on, Action<bool>? changed)
    {
        Label = label;
        Detail = detail;
        isOn = on;
        _changed = changed;
    }

    public string Label { get; }

    /// <summary>What the flag does, shown on hover, because several of the names do not say.</summary>
    public string Detail { get; }

    [ObservableProperty]
    private bool isOn;

    partial void OnIsOnChanged(bool value) => _changed?.Invoke(value);
}

/// <summary>
/// The editable face of one campaign.
///
/// The read-only <see cref="CampaignViewModel"/> beside this one is left exactly as it was. It is
/// built once from a summary and never changes, which the whole detail panel relies on, so edit
/// state lives here instead of being threaded back through it.
///
/// Every change is pushed into the <see cref="SaveEditSession"/> as it is made rather than
/// collected up and applied on save. The session is then the only place the pending edit lives,
/// which is what makes cancelling it a matter of dropping this object.
///
/// Nothing here refuses an edit. A value the game would find strange produces a warning saying
/// what it will do, and is written anyway. The one exception is text that is not a number in a
/// field the game reads as one, which cannot be sent anywhere useful; the raw field list is where
/// a value like that belongs.
/// </summary>
public sealed partial class CampaignEditViewModel : ObservableObject
{
    private readonly SaveEditSession _session;
    private readonly CampaignRecordRef _campaign;
    private readonly CampaignSummary _original;

    private bool _loading = true;

    public CampaignEditViewModel(SaveEditSession session, CampaignRecordRef campaign, CampaignSummary original)
    {
        _session = session;
        _campaign = campaign;
        _original = original;

        DisplayName = SlugcatCatalog.ForId(campaign.SlugcatId).DisplayName;
        IsHunter = RedsIllness.IsHunter(campaign.SlugcatId);

        cycle = Field(SaveFields.Cycle);
        food = Field(SaveFields.Food);
        totalFoodEaten = Field(SaveFields.TotalFood);
        cyclesThisVersion = Field(SaveFields.CyclesThisVersion);
        denPos = Field(SaveFields.DenPos);
        lastDenPos = Field(SaveFields.LastDenPos);
        timeline = Field(SaveFields.Timeline);
        seed = Field(SaveFields.Seed);

        string blob = DeathPersistentBlob;
        karma = DeathPersistentEditor.GetValue(blob, DeathPersistentEditor.KarmaField) ?? "";
        karmaCap = DeathPersistentEditor.GetValue(blob, DeathPersistentEditor.KarmaCapField) ?? "";
        karmaFlower = DeathPersistentEditor.GetValue(blob, DeathPersistentEditor.ReinforcedKarmaField) ?? "";
        deaths = DeathPersistentEditor.GetValue(blob, DeathPersistentEditor.DeathsField) ?? "";
        survives = DeathPersistentEditor.GetValue(blob, DeathPersistentEditor.SurvivesField) ?? "";
        quits = DeathPersistentEditor.GetValue(blob, DeathPersistentEditor.QuitsField) ?? "";

        Flags = BuildFlags();
        Echoes = BuildEchoes();
        Gates = new ObservableCollection<GateEditRow>(BuildGates());

        ShelterMatches = new ObservableCollection<string>();
        LastShelterMatches = new ObservableCollection<string>();
        Warnings = new ObservableCollection<string>();

        _loading = false;

        RefreshShelterMatches();
        RefreshWarnings();
    }

    /// <summary>The campaign being edited, for a heading that says whose save this is.</summary>
    public string DisplayName { get; }

    public bool IsHunter { get; }

    public IReadOnlyList<FlagEditRow> Flags { get; }

    public IReadOnlyList<EchoEditRow> Echoes { get; }

    public ObservableCollection<GateEditRow> Gates { get; }

    /// <summary>Shelters matching what has been typed into the shelter box.</summary>
    public ObservableCollection<string> ShelterMatches { get; }

    public ObservableCollection<string> LastShelterMatches { get; }

    /// <summary>
    /// What the edits will do that the person making them may not expect. Advice, never a refusal:
    /// every value in this list has already been written to the session.
    /// </summary>
    public ObservableCollection<string> Warnings { get; }

    public bool HasWarnings => Warnings.Count > 0;

    public bool IsDirty => _session.IsDirty;

    /// <summary>One line per edit made so far, which the save confirmation will show.</summary>
    public IReadOnlyList<string> Changes => _session.Changes;

    public string ChangeCountText => _session.Changes.Count switch
    {
        0 => "No changes yet",
        1 => "1 change",
        int count => count.ToString(CultureInfo.InvariantCulture) + " changes",
    };

    // ---- run ----

    [ObservableProperty]
    private string cycle;

    [ObservableProperty]
    private string food;

    [ObservableProperty]
    private string totalFoodEaten;

    [ObservableProperty]
    private string cyclesThisVersion;

    [ObservableProperty]
    private string denPos;

    [ObservableProperty]
    private string lastDenPos;

    [ObservableProperty]
    private string timeline;

    [ObservableProperty]
    private string seed;

    // ---- karma and progress ----

    [ObservableProperty]
    private string karma;

    [ObservableProperty]
    private string karmaCap;

    [ObservableProperty]
    private string karmaFlower;

    [ObservableProperty]
    private string deaths;

    [ObservableProperty]
    private string survives;

    [ObservableProperty]
    private string quits;

    // ---- gates ----

    [ObservableProperty]
    private string newGateName = "";

    partial void OnCycleChanged(string value) => SetNumber(SaveFields.Cycle, value);

    partial void OnFoodChanged(string value) => SetNumber(SaveFields.Food, value);

    partial void OnTotalFoodEatenChanged(string value) => SetNumber(SaveFields.TotalFood, value);

    partial void OnCyclesThisVersionChanged(string value)
        => SetNumber(SaveFields.CyclesThisVersion, value);

    partial void OnDenPosChanged(string value)
    {
        SetText(SaveFields.DenPos, value);
        RefreshShelterMatches();
    }

    partial void OnLastDenPosChanged(string value)
    {
        SetText(SaveFields.LastDenPos, value);
        RefreshShelterMatches();
    }

    partial void OnTimelineChanged(string value) => SetText(SaveFields.Timeline, value);

    partial void OnSeedChanged(string value) => SetText(SaveFields.Seed, value);

    partial void OnKarmaChanged(string value) => SetDeathPersistentNumber(DeathPersistentEditor.KarmaField, value);

    partial void OnKarmaCapChanged(string value)
        => SetDeathPersistentNumber(DeathPersistentEditor.KarmaCapField, value);

    partial void OnKarmaFlowerChanged(string value)
        => SetDeathPersistentNumber(DeathPersistentEditor.ReinforcedKarmaField, value);

    partial void OnDeathsChanged(string value) => SetDeathPersistentNumber(DeathPersistentEditor.DeathsField, value);

    partial void OnSurvivesChanged(string value)
        => SetDeathPersistentNumber(DeathPersistentEditor.SurvivesField, value);

    partial void OnQuitsChanged(string value) => SetDeathPersistentNumber(DeathPersistentEditor.QuitsField, value);

    /// <summary>Puts a shelter from the suggestion list into the shelter box.</summary>
    [RelayCommand]
    private void UseShelter(string? room)
    {
        if (!string.IsNullOrWhiteSpace(room))
        {
            DenPos = room.Trim();
        }
    }

    [RelayCommand]
    private void UseLastShelter(string? room)
    {
        if (!string.IsNullOrWhiteSpace(room))
        {
            LastDenPos = room.Trim();
        }
    }

    /// <summary>
    /// Adds a gate the catalog does not list, so a gate from a mod can be opened by typing its name.
    /// </summary>
    [RelayCommand]
    private void AddGate()
    {
        string name = NewGateName.Trim();
        if (name.Length == 0)
        {
            return;
        }

        GateEditRow? existing = Gates.FirstOrDefault(
            g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.UnlockedField = true;
        }
        else
        {
            GateInfo info = GateCatalog.ForName(name);
            Gates.Add(new GateEditRow(name, GateDisplay(info), true, GateCatalog.IsKnown(name), GateChanged));
            ApplyGates();
        }

        NewGateName = "";
    }

    // ---- applying ----

    private string DeathPersistentBlob
        => _session.GetFieldValue(_campaign, SaveFields.DeathPersistent) ?? "";

    private string Field(string key) => _session.GetFieldValue(_campaign, key) ?? "";

    private void SetText(string key, string value)
    {
        if (_loading)
        {
            return;
        }

        string trimmed = value.Trim();

        if (trimmed.Length == 0)
        {
            _session.RemoveField(_campaign, key);
        }
        else
        {
            _session.SetField(_campaign, key, trimmed);
        }

        AfterChange();
    }

    /// <summary>
    /// Writes a number, or says why it did not. Text that is not a number is the one edit this
    /// panel does not make: the game reads these fields as numbers, and there is nowhere useful to
    /// put "abc". The raw field list writes anything, and the warning points at it.
    /// </summary>
    private void SetNumber(string key, string value)
    {
        if (_loading)
        {
            return;
        }

        string trimmed = value.Trim();

        if (trimmed.Length == 0)
        {
            _session.RemoveField(_campaign, key);
            AfterChange();
            return;
        }

        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            _session.SetField(_campaign, key, parsed.ToString(CultureInfo.InvariantCulture));
        }

        AfterChange();
    }

    private void SetDeathPersistentNumber(string key, string value)
    {
        if (_loading)
        {
            return;
        }

        string trimmed = value.Trim();
        string blob = DeathPersistentBlob;

        string updated = trimmed.Length == 0
            ? DeathPersistentEditor.Remove(blob, key)
            : int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                ? DeathPersistentEditor.SetInt(blob, key, parsed)
                : blob;

        WriteDeathPersistent(updated);
    }

    private void WriteDeathPersistent(string blob)
    {
        if (!string.Equals(blob, DeathPersistentBlob, StringComparison.Ordinal))
        {
            _session.SetField(_campaign, SaveFields.DeathPersistent, blob);
        }

        AfterChange();
    }

    private void EchoChanged(EchoEditRow row)
    {
        WriteDeathPersistent(DeathPersistentEditor.SetEcho(DeathPersistentBlob, row.RegionCode, row.State));
    }

    private void GateChanged(GateEditRow row) => ApplyGates();

    private void ApplyGates()
    {
        if (_loading)
        {
            return;
        }

        WriteDeathPersistent(DeathPersistentEditor.SetGates(
            DeathPersistentBlob,
            Gates.Where(g => g.UnlockedField).Select(g => g.Name).ToList()));
    }

    private void AfterChange()
    {
        RefreshWarnings();
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(Changes));
        OnPropertyChanged(nameof(ChangeCountText));
    }

    // ---- building the rows ----

    private IReadOnlyList<FlagEditRow> BuildFlags()
    {
        string blob = DeathPersistentBlob;

        var flags = new List<FlagEditRow>
        {
            new(
                "Mark of communication",
                "Lets the player understand echoes and read pearls.",
                DeathPersistentReader.Read(blob).HasTheMark,
                on => SetDeathPersistentFlag(DeathPersistentEditor.HasTheMarkField, on)),

            new(
                "The glow",
                "The neuron glow, which lights the way and is visible to creatures.",
                _session.HasField(_campaign, SaveFields.Glow),
                on => SetRecordFlag(SaveFields.Glow, on)),

            new(
                "Ascended",
                "The campaign has ascended, which is what the ending screen reads.",
                DeathPersistentReader.Read(blob).Ascended,
                on => SetDeathPersistentFlag(DeathPersistentEditor.AscendedField, on)),

            new(
                "No food drain next cycle",
                "Skips one cycle of food drain. The game clears it at the end of the next session.",
                _session.HasField(_campaign, SaveFields.JustBeatGame),
                on => SetRecordFlag(SaveFields.JustBeatGame, on)),

            new(
                "Citizen ID drone",
                "The drone that follows Artificer and Spearmaster.",
                _session.HasField(_campaign, SaveFields.Robo),
                on => SetRecordFlag(SaveFields.Robo, on)),
        };

        if (IsHunter)
        {
            flags.Add(new FlagEditRow(
                "Hunter's death",
                "Hunter has run out of cycles. The game clears this while the cycle count is inside the limit.",
                DeathPersistentReader.Read(blob).RedsDeathStored,
                on => SetDeathPersistentFlag(DeathPersistentEditor.RedsDeathField, on)));

            flags.Add(new FlagEditRow(
                "Extra cycles",
                "Gives Hunter the longer cycle limit.",
                _original.RedExtraCycles,
                SetRedExtraCycles));
        }

        return flags;
    }

    private void SetRecordFlag(string key, bool on)
    {
        if (_loading)
        {
            return;
        }

        _session.SetFlag(_campaign, key, on);
        AfterChange();
    }

    private void SetDeathPersistentFlag(string key, bool on)
        => WriteDeathPersistent(DeathPersistentEditor.SetFlag(DeathPersistentBlob, key, on));

    /// <summary>
    /// REDEXTRACYCLES is written in two places and SaveState.RedExtraCycles is true when either is
    /// set, so turning it off has to clear both. Turning it on writes the record's copy, which is
    /// the one the game itself writes.
    /// </summary>
    private void SetRedExtraCycles(bool on)
    {
        if (_loading)
        {
            return;
        }

        _session.SetFlag(_campaign, SaveFields.RedExtraCycles, on);

        if (!on)
        {
            WriteDeathPersistent(DeathPersistentEditor.SetFlag(
                DeathPersistentBlob,
                DeathPersistentEditor.RedExtraCyclesField,
                false));
            return;
        }

        AfterChange();
    }

    private IReadOnlyList<EchoEditRow> BuildEchoes()
    {
        var stored = DeathPersistentReader.Read(DeathPersistentBlob).Echoes
            .ToDictionary(e => e.RegionCode, e => e.State, StringComparer.OrdinalIgnoreCase);

        var rows = new List<EchoEditRow>();

        foreach (WorldRegion region in RegionCatalog.WithEchoes)
        {
            stored.TryGetValue(region.Code, out int state);
            rows.Add(new EchoEditRow(region.Code, region.DisplayName, state, true, EchoChanged));
            stored.Remove(region.Code);
        }

        // Anything left is an echo this app does not know the region of, which a mod can add. It
        // is still in the save, so it is still editable.
        foreach (var leftover in stored.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(new EchoEditRow(leftover.Key, leftover.Key, leftover.Value, false, EchoChanged));
        }

        return rows;
    }

    private IReadOnlyList<GateEditRow> BuildGates()
    {
        var open = new HashSet<string>(
            DeathPersistentReader.Read(DeathPersistentBlob).UnlockedGates,
            StringComparer.OrdinalIgnoreCase);

        var rows = new List<GateEditRow>();

        foreach (GateInfo gate in GateCatalog.Known)
        {
            rows.Add(new GateEditRow(gate.Name, GateDisplay(gate), open.Contains(gate.Name), true, GateChanged));
            open.Remove(gate.Name);
        }

        foreach (string leftover in open.OrderBy(g => g, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(new GateEditRow(leftover, GateDisplay(GateCatalog.ForName(leftover)), true, false, GateChanged));
        }

        return rows;
    }

    private static string GateDisplay(GateInfo gate)
        => gate.FromRegion.Length == 0 ? gate.Name : gate.DisplayName;

    private void RefreshShelterMatches()
    {
        Fill(ShelterMatches, DenPos);
        Fill(LastShelterMatches, LastDenPos);

        static void Fill(ObservableCollection<string> target, string query)
        {
            const int Limit = 12;

            target.Clear();

            // A box holding exactly one shelter has nothing left to suggest, so the list gets out
            // of the way rather than offering the value already in the box.
            if (ShelterCatalog.IsKnown(query))
            {
                return;
            }

            foreach (string room in ShelterCatalog.Search(query).Take(Limit))
            {
                target.Add(room);
            }
        }
    }

    /// <summary>
    /// Works out what to say about the edits made so far. Everything here has already been written:
    /// these lines explain consequences, they do not stand in the way of one.
    /// </summary>
    private void RefreshWarnings()
    {
        Warnings.Clear();

        AddNotANumber(Cycle, "Cycle");
        AddNotANumber(Food, "Food now");
        AddNotANumber(TotalFoodEaten, "Food eaten");
        AddNotANumber(CyclesThisVersion, "Cycles this version");
        AddNotANumber(Karma, "Karma");
        AddNotANumber(KarmaCap, "Karma cap");
        AddNotANumber(KarmaFlower, "Karma flower");
        AddNotANumber(Deaths, "Deaths");
        AddNotANumber(Survives, "Survives");
        AddNotANumber(Quits, "Quits");

        AddKarmaWarning();
        AddHunterCycleWarning();
        AddShelterWarning(DenPos, "Shelter");
        AddShelterWarning(LastDenPos, "Last shelter");

        OnPropertyChanged(nameof(HasWarnings));
    }

    private void AddNotANumber(string value, string label)
    {
        string trimmed = value.Trim();

        if (trimmed.Length == 0 || int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return;
        }

        Warnings.Add($"{label} is not a whole number, so it was left as it was. Use the raw field list to write text the game does not read as a number.");
    }

    private void AddKarmaWarning()
    {
        if (!TryNumber(Karma, out int karma) || !TryNumber(KarmaCap, out int cap))
        {
            return;
        }

        int effective = KarmaMath.EffectiveKarma(karma, cap) ?? karma;

        if (effective == karma)
        {
            return;
        }

        int? shown = KarmaMath.DisplayKarma(karma, cap);
        int? shownCap = KarmaMath.DisplayKarmaCap(cap);

        Warnings.Add(
            $"Karma {karma} is outside 0 to {cap}, so the game clamps it when it loads and the meter will read "
            + (shown.HasValue && shownCap.HasValue ? $"{shown} of {shownCap}." : "differently."));
    }

    /// <summary>
    /// Hunter's cycle number is not only a counter. SaveState.LoadGame clears the death flag while
    /// the count is inside the limit, so moving the cycle across that line changes whether the
    /// campaign is over. The edit still goes through; this only says what it did.
    /// </summary>
    private void AddHunterCycleWarning()
    {
        if (!IsHunter || !TryNumber(Cycle, out int cycle))
        {
            return;
        }

        bool storedDeath = DeathPersistentReader.Read(DeathPersistentBlob).RedsDeathStored;
        bool wasDead = RedsIllness.EffectiveRedsDeath(storedDeath, _original.CycleNum, _original.RedExtraCycles);
        bool nowDead = RedsIllness.EffectiveRedsDeath(storedDeath, cycle, RedExtraCyclesNow);

        if (wasDead == nowDead)
        {
            return;
        }

        int limit = RedsIllness.RedsCycles(RedExtraCyclesNow);

        Warnings.Add(nowDead
            ? $"Cycle {cycle} is past Hunter's limit of {limit}, so this campaign now counts as having died of the rot."
            : $"Cycle {cycle} is inside Hunter's limit of {limit}, so this campaign no longer counts as having died of the rot.");
    }

    private bool RedExtraCyclesNow
        => _session.HasField(_campaign, SaveFields.RedExtraCycles)
            || DeathPersistentReader.Read(DeathPersistentBlob).RedExtraCycles;

    private void AddShelterWarning(string value, string label)
    {
        string trimmed = value.Trim();

        if (trimmed.Length == 0 || ShelterCatalog.IsKnown(trimmed))
        {
            return;
        }

        string region = ShelterCatalog.RegionOf(trimmed) ?? "";

        Warnings.Add(RegionCatalog.IsKnown(region)
            ? $"{label} {trimmed} is not a shelter this app knows of in {RegionCatalog.ForCode(region).DisplayName}. If it came from a mod this is fine."
            : $"{label} {trimmed} is not a shelter this app knows of. The game puts the player in the wrong place, or nowhere, if the room does not exist.");
    }

    private static bool TryNumber(string value, out int parsed)
        => int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);
}

/// <summary>
/// The SAVE STATE field names the editor writes. They are the game's own spellings, kept in one
/// place so the panel and any later editor agree about them.
/// </summary>
internal static class SaveFields
{
    public const string Cycle = "CYCLENUM";
    public const string Food = "FOOD";
    public const string TotalFood = "TOTFOOD";
    public const string CyclesThisVersion = "CURRVERCYCLES";
    public const string DenPos = "DENPOS";
    public const string LastDenPos = "LASTVDENPOS";
    public const string Timeline = "TIMELINE";
    public const string Seed = "SEED";
    public const string Glow = "HASTHEGLOW";
    public const string Robo = "HASROBO";
    public const string JustBeatGame = "JUSTBEATGAME";
    public const string RedExtraCycles = "REDEXTRACYCLES";
    public const string DeathPersistent = "DEATHPERSISTENTSAVEDATA";
}
