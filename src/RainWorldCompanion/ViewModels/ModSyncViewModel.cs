using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Mods;

namespace RainWorldCompanion.ViewModels;

// A mod change another window wants previewed and applied here, with the wording that window
// would use for it.
public sealed record ModSyncRequest(
    ModListSnapshot Wanted,
    string SourceText,
    string ButtonText,
    string ApplyReason);

public sealed partial class ModSyncRowViewModel : ObservableObject
{
    private readonly ModSyncRow _row;
    private readonly Action<ModSyncRowViewModel> _changed;

    public ModSyncRowViewModel(ModSyncRow row, Action<ModSyncRowViewModel> changed)
    {
        _row = row;
        _changed = changed;
        wanted = row.Wanted;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    [NotifyPropertyChangedFor(nameof(IsChanging))]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    private bool wanted;

    public string Name => _row.Name;

    public string Id => _row.Id;

    public bool Installed => _row.Installed;

    public bool IsOn => _row.IsOn;

    public bool Recorded => _row.Recorded;

    public string DetailText
    {
        get
        {
            string version = _row.Version is { Length: > 0 } text ? text : "";
            string origin = _row.WorkshopId is { Length: > 0 } ? "workshop" : "local mod";

            return version.Length > 0 ? $"{version}, {origin}" : origin;
        }
    }

    public string WorkshopUrl => _row.WorkshopId is { Length: > 0 } id && id.All(char.IsAsciiDigit)
        ? ModListDiffViewModel.WorkshopUrlPrefix + id
        : "";

    public bool HasWorkshopPage => WorkshopUrl.Length > 0;

    public string StateText
    {
        get
        {
            if (!Installed)
            {
                return _row.RecordedVersion is { Length: > 0 } was
                    ? $"not on this machine, the save used {was}"
                    : "not on this machine";
            }

            if (Wanted == IsOn)
            {
                return _row.OrderChanges ? "will move in load order" : "";
            }

            return Wanted ? "will be turned on" : "will be turned off";
        }
    }

    public bool IsChanging => Installed && Wanted != IsOn;

    public string AccessibleName
        => $"{Name}, {(Wanted ? "on" : "off")}{(StateText.Length > 0 ? ", " + StateText : "")}";

    /// <summary>What this mod needs, from its own modinfo.json. Empty for a mod not on disk.</summary>
    public IReadOnlyList<string> Requirements =>
        _row.OnDisk is { } mod ? mod.Requirements : Array.Empty<string>();

    partial void OnWantedChanged(bool value)
    {
        _row.Wanted = value;
        _changed(this);
    }
}

public sealed class ModListProfileViewModel
{
    public ModListProfileViewModel(ModListProfile profile)
    {
        Profile = profile;
    }

    internal ModListProfile Profile { get; }

    public Guid Id => Profile.Id;

    public string Name => Profile.Name;

    public string DetailText => $"{Count(Profile.Snapshot.Mods.Count)}, updated {Profile.UpdatedAt.LocalDateTime:d MMM HH:mm}";

    public ModListSnapshot Snapshot => Profile.Snapshot;

    private static string Count(int count) => count == 1 ? "1 mod" : $"{count} mods";
}

public sealed class ModListHistoryViewModel
{
    public ModListHistoryViewModel(ModListHistoryEntry entry)
    {
        Entry = entry;
    }

    internal ModListHistoryEntry Entry { get; }

    public Guid Id => Entry.Id;

    public string Reason => Entry.Reason;

    public string DetailText => $"{Count(Entry.Snapshot.Mods.Count)}, saved {Entry.CapturedAt.LocalDateTime:d MMM HH:mm}";

    public ModListSnapshot Snapshot => Entry.Snapshot;

    public DateTimeOffset CapturedAt => Entry.CapturedAt;

    private static string Count(int count) => count == 1 ? "1 mod" : $"{count} mods";
}

public sealed partial class ModSyncViewModel : ObservableObject
{
    public const string SteamRunUrl = "steam://rungameid/" + CurrentModsReader.SteamAppId;

    private const string FreeText = "Turn mods on and off. Open a backup or a library save to match what it was played with.";

    private readonly ModSyncService _service;
    private ModSyncPlan? _plan;
    private ModListSnapshot? _recorded;
    private ModListSnapshot? _imported;
    private ModListHistoryViewModel? _latestHistory;
    private string _applyReason = "Before manual mod changes";

    public ModSyncViewModel(ModSyncService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        Refresh();
    }

    public ObservableCollection<ModSyncRowViewModel> Mods { get; } = new();

    public ObservableCollection<ModSyncRowViewModel> Missing { get; } = new();

    public ObservableCollection<ModListProfileViewModel> Profiles { get; } = new();

    public ObservableCollection<ModListHistoryViewModel> History { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(MatchTheSaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevertChangesCommand))]
    private bool isBusy;

    [ObservableProperty]
    private string sourceText = FreeText;

    [ObservableProperty]
    private string headlineText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private string problemText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private string resultText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCatalogMessage))]
    private string catalogMessageText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCurrentListSelected))]
    [NotifyPropertyChangedFor(nameof(IsSavedListsSelected))]
    private int selectedTabIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveImportedAsProfile))]
    private string suggestedProfileName = "";

    [ObservableProperty]
    private string matchListButtonText = "Match the save";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLaunch))]
    private bool appliedSomething;

    public bool HasProblem => ProblemText.Length > 0;

    public bool HasResult => ResultText.Length > 0;

    public bool HasCatalogMessage => CatalogMessageText.Length > 0;

    public bool IsCurrentListSelected
    {
        get => SelectedTabIndex == 0;
        set
        {
            if (value)
            {
                SelectedTabIndex = 0;
            }
        }
    }

    public bool IsSavedListsSelected
    {
        get => SelectedTabIndex == 1;
        set
        {
            if (value)
            {
                SelectedTabIndex = 1;
            }
        }
    }

    public bool CanSaveImportedAsProfile => _imported is not null && SuggestedProfileName.Length > 0;

    public bool HasProfiles => Profiles.Count > 0;

    public bool HasHistory => History.Count > 0;

    public bool HasLatestHistory => _latestHistory is not null;

    public string LatestHistoryText => _latestHistory is null
        ? ""
        : $"Previous list: {Count(_latestHistory.Snapshot.Mods.Count)}, saved {_latestHistory.CapturedAt.LocalDateTime:d MMM HH:mm}";

    public bool CanLaunch => AppliedSomething && OfferLaunch;

    // False when a restore or a load is waiting behind this window. Starting the game there would
    // play the save that is about to be replaced.
    public bool OfferLaunch { get; init; } = true;

    public bool HasMissing => Missing.Count > 0;

    public string MissingHeader => $"Not installed ({Missing.Count})";

    public string ModsHeader => $"Installed ({Mods.Count})";

    public bool IsMatchingASave => _recorded is not null;

    public string ChangeText
    {
        get
        {
            if (_plan is null)
            {
                return "";
            }

            int on = _plan.Rows.Count(row => row.TurningOn);
            int off = _plan.Rows.Count(row => row.TurningOff);
            int reordered = _plan.Rows.Count(row => row.OrderChanges);

            if (on == 0 && off == 0)
            {
                return reordered == 0
                    ? $"{_plan.OnCount} on. Nothing to apply."
                    : $"{_plan.OnCount} on once applied. Restoring load order for {Count(reordered)}.";
            }

            string order = reordered == 0 ? "" : $" Restoring load order for {Count(reordered)}.";

            return (on, off) switch
            {
                ( > 0, 0) => $"{_plan.OnCount} on once applied. Turning on {on}." + order,
                (0, > 0) => $"{_plan.OnCount} on once applied. Turning off {off}." + order,
                _ => $"{_plan.OnCount} on once applied. Turning on {on}, turning off {off}." + order,
            };
        }
    }

    public void Match(ModListSnapshot? recorded, string? sourceName)
    {
        _recorded = recorded;
        _imported = null;
        SuggestedProfileName = "";
        OnPropertyChanged(nameof(CanSaveImportedAsProfile));
        _applyReason = recorded is null
            ? "Before manual mod changes"
            : $"Before matching save \"{sourceName ?? "selected save"}\"";
        MatchListButtonText = "Match the save";
        SourceText = recorded is null
            ? FreeText
            : $"Matching the mods {sourceName ?? "that save"} was played with.";
        SelectedTabIndex = 0;

        ResultText = "";
        Refresh();
    }

    // The list a lobby needs is a recorded list like any other, so it previews and applies through
    // the same path a save's list does.
    public void MatchWanted(ModSyncRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        LoadPreview(request.Wanted, request.SourceText, request.ButtonText, request.ApplyReason);
    }

    public void ImportList(string path)
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            ModListSnapshot imported = _service.ImportList(path);
            string name = Path.GetFileNameWithoutExtension(path);

            _recorded = imported;
            _imported = imported;
            SuggestedProfileName = name;
            _applyReason = $"Before applying imported list \"{name}\"";
            MatchListButtonText = "Match imported list";
            SourceText = $"Matching the imported mod list \"{name}\".";
            SelectedTabIndex = 0;
            ResultText = "";
            Refresh();
            OnPropertyChanged(nameof(CanSaveImportedAsProfile));
            ResultText = $"Imported {Count(imported.Mods.Count)}. Review the ticks, then press Apply.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ResultText = "The mod list could not be imported: " + ex.Message;
        }
    }

    public void ExportList(string path)
    {
        if (IsBusy || _plan is null)
        {
            return;
        }

        try
        {
            int count = _service.ExportList(_plan, path);
            ResultText = $"Exported {Count(count)} to {path}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ResultText = "The mod list could not be exported: " + ex.Message;
        }
    }

    public void SaveImportedProfile(string name)
    {
        if (_imported is null || _plan is null)
        {
            return;
        }

        if (SaveProfile(name, _service.Snapshot(_plan)))
        {
            _imported = null;
            SuggestedProfileName = "";
            OnPropertyChanged(nameof(CanSaveImportedAsProfile));
        }
    }

    public void SaveCurrentProfile(string name)
    {
        if (_plan is not null)
        {
            SaveProfile(name, _service.Snapshot(_plan));
        }
    }

    public void RenameProfile(ModListProfileViewModel profile, string name)
    {
        ArgumentNullException.ThrowIfNull(profile);
        RunCatalogCommand(
            new RainWorldCompanion.Core.Mods.RenameProfile(profile.Id, name),
            $"Renamed saved list to \"{name.Trim()}\".");
    }

    public void ReplaceProfile(ModListProfileViewModel profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (_plan is not null)
        {
            RunCatalogCommand(
                new RainWorldCompanion.Core.Mods.ReplaceProfile(profile.Id, _service.Snapshot(_plan)),
                $"Replaced \"{profile.Name}\" with the currently ticked list.");
        }
    }

    public void DeleteProfile(ModListProfileViewModel profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        RunCatalogCommand(
            new RainWorldCompanion.Core.Mods.DeleteProfile(profile.Id),
            $"Deleted saved list \"{profile.Name}\".");
    }

    public void LoadProfile(ModListProfileViewModel profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        LoadPreview(
            profile.Snapshot,
            $"Previewing saved list \"{profile.Name}\".",
            "Match saved list",
            $"Before applying profile \"{profile.Name}\"");
    }

    public void LoadHistory(ModListHistoryViewModel history)
    {
        ArgumentNullException.ThrowIfNull(history);
        string when = history.CapturedAt.LocalDateTime.ToString("d MMM HH:mm");
        LoadPreview(
            history.Snapshot,
            $"Previewing the mod list saved {when}.",
            "Match saved list",
            $"Before loading mod history from {when}");
    }

    public void PreviewLatestHistory()
    {
        if (_latestHistory is { } latest)
        {
            LoadHistory(latest);
        }
    }

    public void ExportSnapshot(ModListSnapshot snapshot, string path)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        try
        {
            ModListFile.Write(path, snapshot);
            ResultText = $"Exported {Count(snapshot.Mods.Count)} to {path}.";
            CatalogMessageText = ResultText;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ResultText = "The mod list could not be exported: " + ex.Message;
            CatalogMessageText = ResultText;
        }
    }

    private bool SaveProfile(string name, ModListSnapshot snapshot)
    {
        return RunCatalogCommand(
            new RainWorldCompanion.Core.Mods.SaveProfile(name, snapshot),
            $"Saved \"{name.Trim()}\" as a profile.");
    }

    private bool RunCatalogCommand(ModListCatalogCommand command, string success)
    {
        ModListCatalogResult result = _service.Catalog.Execute(command);
        if (!result.Succeeded)
        {
            CatalogMessageText = result.Problem ?? "The saved lists could not be changed.";
            ResultText = CatalogMessageText;
            return false;
        }

        LoadCatalog(result.View, result.Warning);
        ResultText = success;
        return true;
    }

    private void LoadPreview(ModListSnapshot snapshot, string source, string matchButton, string applyReason)
    {
        _recorded = snapshot;
        _imported = null;
        SuggestedProfileName = "";
        OnPropertyChanged(nameof(CanSaveImportedAsProfile));
        _applyReason = applyReason;
        MatchListButtonText = matchButton;
        SourceText = source;
        SelectedTabIndex = 0;
        ResultText = "";
        Refresh();
    }

    private void LoadCatalog(ModListCatalogView catalog, string? warning = null)
    {
        Profiles.Clear();
        History.Clear();

        foreach (ModListProfile profile in catalog.Profiles)
        {
            Profiles.Add(new ModListProfileViewModel(profile));
        }

        foreach (ModListHistoryEntry entry in catalog.History.Take(ModListCatalog.HistoryLimit))
        {
            History.Add(new ModListHistoryViewModel(entry));
        }

        _latestHistory = History.FirstOrDefault();

        string unreadable = catalog.UnreadableEntryCount switch
        {
            0 => "",
            1 => "1 saved mod list could not be read.",
            int count => $"{count} saved mod lists could not be read.",
        };
        CatalogMessageText = string.Join(
            " ",
            new[] { warning, unreadable }.Where(text => !string.IsNullOrWhiteSpace(text)));

        OnPropertyChanged(nameof(HasProfiles));
        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(HasLatestHistory));
        OnPropertyChanged(nameof(LatestHistoryText));
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private void Refresh()
    {
        _plan = _service.BuildPlan(_recorded);
        ProblemText = _service.WhyNotNow() ?? "";

        Mods.Clear();
        Missing.Clear();

        foreach (ModSyncRow row in _plan.Rows)
        {
            var view = new ModSyncRowViewModel(row, OnRowWanted);

            if (row.Installed)
            {
                Mods.Add(view);
            }
            else
            {
                Missing.Add(view);
            }
        }

        HeadlineText = BuildHeadline();
        LoadCatalog(_service.ReadCatalog());
        RaiseListStates();
    }

    private bool CanRefresh() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanMatchTheSave))]
    private void MatchTheSave()
    {
        _plan?.WantWhatTheSaveHad();
        PullWantedFromPlan();
    }

    private bool CanMatchTheSave() => !IsBusy && IsMatchingASave;

    [RelayCommand(CanExecute = nameof(CanRevertChanges))]
    private void RevertChanges()
    {
        _plan?.WantEverythingOnNow();
        PullWantedFromPlan();
    }

    private bool CanRevertChanges() => !IsBusy && _plan is { NothingToDo: false };

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        if (_plan is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            Report(_service.Apply(_plan, _applyReason));
        }
        catch (GameRunningException ex)
        {
            ResultText = ex.Message;
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    private bool CanApply() => !IsBusy && !HasProblem && _plan is { NothingToDo: false };

    public void ReportLinkProblem(string message) => ResultText = "That link could not be opened: " + message;

    private void PullWantedFromPlan()
    {
        if (_plan is null)
        {
            return;
        }

        var wanted = _plan.Rows.ToDictionary(row => row.Id, row => row.Wanted, StringComparer.OrdinalIgnoreCase);

        foreach (ModSyncRowViewModel row in Mods.Concat(Missing))
        {
            if (wanted.TryGetValue(row.Id, out bool value))
            {
                row.Wanted = value;
            }
        }

        RaiseChangeStates();
    }

    private void Report(ModSyncResult result)
    {
        ResultText = result.Headline;

        if (result.Applied)
        {
            AppliedSomething = true;
        }
    }

    private string BuildHeadline()
    {
        if (_plan is null)
        {
            return "";
        }

        if (_recorded is null)
        {
            return "Tick a mod to turn it on, clear it to turn it off, then press Apply.";
        }

        if (_plan.Diff.NothingWasRecorded)
        {
            return "That save was made before the app recorded mods, so there is nothing to match it against.";
        }

        if (_plan.Diff.RecordedCouldNotLook)
        {
            return "That save recorded that it could not read which mods were on, so there is nothing to match.";
        }

        return _plan.NothingToDo
            ? "This machine already has the selected mod list."
            : "Ticked to match the selected mod list. Change any you would rather leave alone, then press Apply.";
    }

    /// <summary>
    /// Turning a mod on turns on what it needs, which is what the game's own Remix menu does. Held
    /// shut while it sweeps, because setting Wanted on each one lands straight back here.
    /// </summary>
    private void OnRowWanted(ModSyncRowViewModel row)
    {
        if (!_cascading && row.Wanted)
        {
            _cascading = true;

            try
            {
                TurnOnWhatItNeeds(row);
            }
            finally
            {
                _cascading = false;
            }
        }

        RaiseChangeStates();
    }

    private bool _cascading;

    private void TurnOnWhatItNeeds(ModSyncRowViewModel row)
    {
        var installed = _plan?.Rows
            .Where(candidate => candidate.OnDisk is not null)
            .Select(candidate => candidate.OnDisk!)
            .ToList();

        var required = ModRequirements.Closure(row.Id, installed);
        if (required.Count == 0)
        {
            return;
        }

        var byId = Mods.ToDictionary(candidate => candidate.Id, StringComparer.OrdinalIgnoreCase);
        var turnedOn = new List<string>();

        foreach (var id in required)
        {
            // A requirement nothing on this machine provides sits in Missing, where nothing can be
            // turned on. It is named anyway, so the reason the mod may still not work is said.
            if (!byId.TryGetValue(id, out ModSyncRowViewModel? needed))
            {
                turnedOn.Add(id + " (not installed)");
                continue;
            }

            if (!needed.Wanted)
            {
                needed.Wanted = true;
                turnedOn.Add(needed.Name);
            }
        }

        if (turnedOn.Count > 0)
        {
            ResultText = $"{row.Name} needs {string.Join(", ", turnedOn)}, so " +
                (turnedOn.Count == 1 ? "it was turned on too." : "they were turned on too.");
        }
    }

    private void RaiseChangeStates()
    {
        OnPropertyChanged(nameof(ChangeText));
        ApplyCommand.NotifyCanExecuteChanged();
        RevertChangesCommand.NotifyCanExecuteChanged();
    }

    private void RaiseListStates()
    {
        OnPropertyChanged(nameof(HasMissing));
        OnPropertyChanged(nameof(MissingHeader));
        OnPropertyChanged(nameof(ModsHeader));
        OnPropertyChanged(nameof(HasProblem));
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(IsMatchingASave));

        MatchTheSaveCommand.NotifyCanExecuteChanged();
        RaiseChangeStates();
    }

    private static string Count(int count) => count == 1 ? "1 mod" : $"{count} mods";
}
