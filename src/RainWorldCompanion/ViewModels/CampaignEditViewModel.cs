// Usings sit above the namespace: RainWorldCompanion.Core.System would otherwise shadow System.
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RainWorldCompanion.Core.Editing;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.Core.Saves.Models;
using RainWorldCompanion.Core.System;

namespace RainWorldCompanion.ViewModels;

/// <summary>Every echo the game has gets a row, not only the ones the save mentions.</summary>
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

    /// <summary>The three booleans below are views onto this, not fields of their own.</summary>
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
    /// Setting one off does nothing: a radio group clears the old button before it sets the new
    /// one, and acting on the clear would write "never seen" on the way to every other value.
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
/// Every change is pushed into the <see cref="SaveEditSession"/> as it is made, so the session is
/// the only place the pending edit lives and cancelling is a matter of dropping this object.
/// Nothing here refuses an edit: a value the game would find strange warns and is written anyway.
/// </summary>
public sealed partial class CampaignEditViewModel : ObservableObject
{
    /// <summary>An edit to one of these in the raw list has to be read back into the named boxes.</summary>
    private static readonly HashSet<string> CuratedKeys = new(StringComparer.Ordinal)
    {
        SaveFields.Cycle,
        SaveFields.Food,
        SaveFields.TotalFood,
        SaveFields.CyclesThisVersion,
        SaveFields.DenPos,
        SaveFields.LastDenPos,
        SaveFields.Timeline,
        SaveFields.Seed,
        SaveFields.Glow,
        SaveFields.Robo,
        SaveFields.JustBeatGame,
        SaveFields.RedExtraCycles,
        SaveFields.DeathPersistent,
    };

    private readonly SaveEditSession _session;
    private readonly CampaignRecordRef _campaign;
    private readonly CampaignSummary _original;
    private readonly List<RawFieldRow> _rawFields = new();

    private bool _loading = true;
    private string? _splitNote;

    /// <param name="expansions">Optional. Without it the panel says less rather than nothing.</param>
    public CampaignEditViewModel(
        SaveEditSession session,
        CampaignRecordRef campaign,
        CampaignSummary original,
        ExpansionPresence? expansions = null,
        bool devourmentInstalled = false)
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

        Flags = new ObservableCollection<FlagEditRow>(BuildFlags());
        Echoes = new ObservableCollection<EchoEditRow>(BuildEchoes());
        Gates = new ObservableCollection<GateEditRow>(BuildGates());

        ShelterMatches = new ObservableCollection<string>();
        LastShelterMatches = new ObservableCollection<string>();
        Warnings = new ObservableCollection<string>();
        VisibleRawFields = new ObservableCollection<RawFieldRow>();

        BuildRawFields();

        Devourment = new DevourmentEditViewModel(
            session, campaign, denPos, AfterChange, expansions, devourmentInstalled);

        _loading = false;

        RefreshShelterMatches();
        RefreshWarnings();
    }

    public string DisplayName { get; }

    public bool IsHunter { get; }

    public ObservableCollection<FlagEditRow> Flags { get; }

    public ObservableCollection<EchoEditRow> Echoes { get; }

    public ObservableCollection<GateEditRow> Gates { get; }

    /// <summary>All of them, so a field this app never modelled is still findable and editable.</summary>
    public ObservableCollection<RawFieldRow> VisibleRawFields { get; }

    /// <summary>Every field, whether the search is showing it or not.</summary>
    public IReadOnlyList<RawFieldRow> RawFields => _rawFields;

    /// <summary>Built for every campaign, and it decides for itself whether it is shown.</summary>
    public DevourmentEditViewModel Devourment { get; }

    public string RawFieldCountText => _rawFields.Count == VisibleRawFields.Count
        ? _rawFields.Count.ToString(CultureInfo.InvariantCulture) + " fields"
        : $"{VisibleRawFields.Count} of {_rawFields.Count} fields";

    public ObservableCollection<string> ShelterMatches { get; }

    public ObservableCollection<string> LastShelterMatches { get; }

    /// <summary>Advice, never a refusal: every value here has already been written to the session.</summary>
    public ObservableCollection<string> Warnings { get; }

    public bool HasWarnings => Warnings.Count > 0;

    public bool IsDirty => _session.IsDirty;

    public IReadOnlyList<string> Changes => _session.Changes;

    /// <summary>Touches no files, so a problem is found while the edit is still only in memory.</summary>
    public SaveWritePlan BuildWritePlan() => _session.BuildWritePlan();

    public string ChangeCountText => _session.Changes.Count switch
    {
        0 => "No changes yet",
        1 => "1 change",
        int count => count.ToString(CultureInfo.InvariantCulture) + " changes",
    };

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

    [ObservableProperty]
    private string newGateName = "";

    [ObservableProperty]
    private string rawSearch = "";

    [ObservableProperty]
    private string newFieldKey = "";

    [ObservableProperty]
    private string newFieldValue = "";

    /// <summary>A bare token and a key with an empty value are different things in the file.</summary>
    [ObservableProperty]
    private bool newFieldIsFlag;

    partial void OnRawSearchChanged(string value) => RefreshVisibleRawFields();

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

    /// <summary>A gate the catalog does not list can be opened by typing its name.</summary>
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
            ApplyGates("Gate " + name, "closed", "open");
        }

        NewGateName = "";
    }

    [RelayCommand]
    private void RevertRawField(RawFieldRow? row) => row?.Revert();

    /// <summary>There is no undo short of cancelling the editor.</summary>
    [RelayCommand]
    private void RemoveRawField(RawFieldRow? row)
    {
        if (row is null)
        {
            return;
        }

        _session.RemoveField(_campaign, row.Key, row.Occurrence);
        AfterRawChange(row.Key);
    }

    /// <summary>A key the record already carries adds a second one, as the game itself does.</summary>
    [RelayCommand]
    private void AddRawField()
    {
        string key = NewFieldKey.Trim();

        if (key.Length == 0)
        {
            return;
        }

        if (NewFieldIsFlag)
        {
            NoteAnySplit(key, key);
            _session.SetFlag(_campaign, key, true);
        }
        else
        {
            int occurrence = _rawFields.Count(row => string.Equals(row.Key, key, StringComparison.Ordinal));
            NoteAnySplit(key, key + NewFieldValue);
            _session.SetField(_campaign, key, NewFieldValue, occurrence);
        }

        NewFieldKey = "";
        NewFieldValue = "";

        AfterRawChange(key);
    }

    private void RawValueChanged(RawFieldRow row)
    {
        if (_loading)
        {
            return;
        }

        NoteAnySplit(row.Label, row.Value);
        _session.SetField(_campaign, row.Key, row.Value, row.Occurrence);
        AfterRawChange(row.Key);
    }

    /// <summary>A raw edit can move a field one of the named boxes above is also showing.</summary>
    private void AfterRawChange(string key)
    {
        if (CuratedKeys.Contains(key))
        {
            PullCuratedValues();
            RebuildCuratedRows();
        }

        AfterChange();
    }

    private void BuildRawFields()
    {
        _rawFields.Clear();

        foreach (RawField field in _session.EnumerateFields(_campaign))
        {
            _rawFields.Add(new RawFieldRow(
                field.Key,
                field.Occurrence,
                field.IsFlag,
                field.Value ?? "",
                RawValueChanged));
        }

        RefreshVisibleRawFields();
    }

    /// <summary>
    /// Values are pushed into the rows already there, so typing does not rebuild the list under the
    /// cursor. A record that gained, lost or reshuffled a field is rebuilt instead: a row addresses
    /// its field by position, so a stale row would write over a field it is not showing.
    /// </summary>
    private void SyncRawFields()
    {
        IReadOnlyList<RawField> fields = _session.EnumerateFields(_campaign);

        if (fields.Count != _rawFields.Count)
        {
            BuildRawFields();
            return;
        }

        for (int i = 0; i < fields.Count; i++)
        {
            if (!string.Equals(fields[i].Key, _rawFields[i].Key, StringComparison.Ordinal)
                || fields[i].Occurrence != _rawFields[i].Occurrence
                || fields[i].IsFlag != _rawFields[i].IsFlag)
            {
                BuildRawFields();
                return;
            }
        }

        for (int i = 0; i < fields.Count; i++)
        {
            _rawFields[i].PullValue(fields[i].Value ?? "");
        }
    }

    private void RefreshVisibleRawFields()
    {
        string query = RawSearch.Trim();

        VisibleRawFields.Clear();

        foreach (RawFieldRow row in _rawFields)
        {
            if (query.Length == 0
                || row.Key.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.Value.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                VisibleRawFields.Add(row);
            }
        }

        OnPropertyChanged(nameof(RawFieldCountText));
    }

    /// <summary>Reads the named boxes back out of the record, without writing them back into it.</summary>
    private void PullCuratedValues()
    {
        bool wasLoading = _loading;
        _loading = true;

        Cycle = Field(SaveFields.Cycle);
        Food = Field(SaveFields.Food);
        TotalFoodEaten = Field(SaveFields.TotalFood);
        CyclesThisVersion = Field(SaveFields.CyclesThisVersion);
        DenPos = Field(SaveFields.DenPos);
        LastDenPos = Field(SaveFields.LastDenPos);
        Timeline = Field(SaveFields.Timeline);
        Seed = Field(SaveFields.Seed);

        string blob = DeathPersistentBlob;
        Karma = DeathPersistentEditor.GetValue(blob, DeathPersistentEditor.KarmaField) ?? "";
        KarmaCap = DeathPersistentEditor.GetValue(blob, DeathPersistentEditor.KarmaCapField) ?? "";
        KarmaFlower = DeathPersistentEditor.GetValue(blob, DeathPersistentEditor.ReinforcedKarmaField) ?? "";
        Deaths = DeathPersistentEditor.GetValue(blob, DeathPersistentEditor.DeathsField) ?? "";
        Survives = DeathPersistentEditor.GetValue(blob, DeathPersistentEditor.SurvivesField) ?? "";
        Quits = DeathPersistentEditor.GetValue(blob, DeathPersistentEditor.QuitsField) ?? "";

        _loading = wasLoading;

        RefreshShelterMatches();
    }

    /// <summary>Each row takes its state through its constructor, so nothing here writes back.</summary>
    private void RebuildCuratedRows()
    {
        bool wasLoading = _loading;
        _loading = true;

        Replace(Flags, BuildFlags());
        Replace(Echoes, BuildEchoes());
        Replace(Gates, BuildGates());

        _loading = wasLoading;

        static void Replace<T>(ObservableCollection<T> target, IReadOnlyList<T> rows)
        {
            target.Clear();

            foreach (T row in rows)
            {
                target.Add(row);
            }
        }
    }

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

    /// <summary>Text that is not a number is the one edit this panel does not make.</summary>
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
        string? before = DeathPersistentEditor.GetValue(blob, key);

        if (trimmed.Length == 0)
        {
            WriteDeathPersistent(DeathPersistentEditor.Remove(blob, key), key, before, null);
            return;
        }

        if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            // Nothing to write. The warning list says so.
            AfterChange();
            return;
        }

        string after = parsed.ToString(CultureInfo.InvariantCulture);
        WriteDeathPersistent(DeathPersistentEditor.SetInt(blob, key, parsed), key, before, after);
    }

    /// <summary>
    /// The blob is one field holding karma, every echo and every gate, so the session is told which
    /// part moved. Without that, two unrelated edits read as two edits of the same field.
    /// </summary>
    private void WriteDeathPersistent(string blob, string partName, string? before, string? after)
    {
        _session.SetFieldPart(_campaign, SaveFields.DeathPersistent, blob, partName, before, after);
        AfterChange();
    }

    private void EchoChanged(EchoEditRow row)
    {
        if (_loading)
        {
            return;
        }

        string blob = DeathPersistentBlob;
        int before = DeathPersistentReader.Read(blob).Echoes
            .FirstOrDefault(e => string.Equals(e.RegionCode, row.RegionCode, StringComparison.OrdinalIgnoreCase))
            ?.State ?? DeathPersistentEditor.EchoNeverSeen;

        WriteDeathPersistent(
            DeathPersistentEditor.SetEcho(blob, row.RegionCode, row.State),
            "Echo " + row.RegionCode,
            EchoStateText(before),
            EchoStateText(row.State));
    }

    private static string EchoStateText(int state) => state switch
    {
        DeathPersistentEditor.EchoSensed => "sensed",
        DeathPersistentEditor.EchoTalkedTo => "talked to",
        DeathPersistentEditor.EchoNeverSeen => "never seen",
        _ => state.ToString(CultureInfo.InvariantCulture),
    };

    private void GateChanged(GateEditRow row)
    {
        if (_loading)
        {
            return;
        }

        ApplyGates("Gate " + row.Name, row.UnlockedField ? "closed" : "open", row.UnlockedField ? "open" : "closed");
    }

    private void ApplyGates(string partName, string? before, string? after)
    {
        WriteDeathPersistent(
            DeathPersistentEditor.SetGates(
                DeathPersistentBlob,
                Gates.Where(g => g.UnlockedField).Select(g => g.Name).ToList()),
            partName,
            before,
            after);
    }

    private void AfterChange()
    {
        SyncRawFields();
        RefreshWarnings();
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(Changes));
        OnPropertyChanged(nameof(ChangeCountText));
    }

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
    {
        if (_loading)
        {
            return;
        }

        string blob = DeathPersistentBlob;
        bool before = DeathPersistentEditor.GetValue(blob, key) is not null
            || DelimitedFlagIsSet(blob, key);

        WriteDeathPersistent(
            DeathPersistentEditor.SetFlag(blob, key, on),
            key,
            before ? "on" : "off",
            on ? "on" : "off");
    }

    /// <summary>GetValue answers null for a bare flag as well as for an absent field.</summary>
    private static bool DelimitedFlagIsSet(string blob, string key)
        => !string.Equals(DeathPersistentEditor.SetFlag(blob, key, false), blob, StringComparison.Ordinal);

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
            SetDeathPersistentFlag(DeathPersistentEditor.RedExtraCyclesField, false);
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

        // Anything left is an echo whose region this app does not know, which a mod can add.
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

            // A box holding exactly one shelter has nothing left to suggest.
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

        // The Devourment editor works out its own, and it refreshes them before it calls back here.
        foreach (string warning in Devourment.Warnings)
        {
            Warnings.Add(warning);
        }

        if (_splitNote is not null)
        {
            Warnings.Add(_splitNote);
        }

        OnPropertyChanged(nameof(HasWarnings));
    }

    /// <summary>
    /// Written in the past tense on purpose: a value carrying a field separator stops being one
    /// field the moment it is written, so by the time the list is read back there is nothing left
    /// to point at. The note stands until the next raw edit.
    /// </summary>
    private void NoteAnySplit(string label, string value)
    {
        _splitNote = value.Contains(SavePayloadReader.RecordSeparator, StringComparison.Ordinal)
            ? $"{label} was given {SavePayloadReader.RecordSeparator}, which is what the file splits records on, "
                + "so what followed it now sits outside this campaign."
            : value.Contains(SavePayloadReader.FieldSeparator, StringComparison.Ordinal)
                ? $"{label} was given {SavePayloadReader.FieldSeparator}, which is what the file splits fields on, "
                    + "so it is now more than one field."
                : null;
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
    /// SaveState.LoadGame clears the death flag while the count is inside the limit, so moving the
    /// cycle across that line changes whether the campaign is over.
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

/// <summary>The game's own spellings of the SAVE STATE field names.</summary>
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
