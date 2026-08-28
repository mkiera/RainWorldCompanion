using System.Collections.ObjectModel;
using System.Linq;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Mods;

namespace RainWorldCompanion.ViewModels;

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
                return "";
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

public sealed partial class ModSyncViewModel : ObservableObject
{
    public const string SteamRunUrl = "steam://rungameid/" + CurrentModsReader.SteamAppId;

    private const string FreeText = "Turn mods on and off. Open a backup or a library save to match what it was played with.";

    private readonly ModSyncService _service;
    private ModSyncPlan? _plan;
    private ModListSnapshot? _recorded;

    public ModSyncViewModel(ModSyncService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        Refresh();
    }

    public ObservableCollection<ModSyncRowViewModel> Mods { get; } = new();

    public ObservableCollection<ModSyncRowViewModel> Missing { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestorePreviousCommand))]
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
    [NotifyCanExecuteChangedFor(nameof(RestorePreviousCommand))]
    private string problemText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private string resultText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRestorePoint))]
    private string restorePointText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLaunch))]
    private bool appliedSomething;

    public bool HasProblem => ProblemText.Length > 0;

    public bool HasResult => ResultText.Length > 0;

    public bool HasRestorePoint => RestorePointText.Length > 0;

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

            return (on, off) switch
            {
                (0, 0) => $"{_plan.OnCount} on. Nothing to apply.",
                ( > 0, 0) => $"{_plan.OnCount} on once applied. Turning on {on}.",
                (0, > 0) => $"{_plan.OnCount} on once applied. Turning off {off}.",
                _ => $"{_plan.OnCount} on once applied. Turning on {on}, turning off {off}.",
            };
        }
    }

    public void Match(ModListSnapshot? recorded, string? sourceName)
    {
        _recorded = recorded;
        SourceText = recorded is null
            ? FreeText
            : $"Matching the mods {sourceName ?? "that save"} was played with.";

        ResultText = "";
        Refresh();
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
        RestorePointText = BuildRestorePointText();
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
            Report(_service.Apply(_plan));
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

    [RelayCommand(CanExecute = nameof(CanRestorePrevious))]
    private void RestorePrevious()
    {
        IsBusy = true;
        try
        {
            Report(_service.RestorePrevious());
        }
        catch (GameRunningException ex)
        {
            ResultText = ex.Message;
        }
        finally
        {
            IsBusy = false;
            _recorded = null;
            SourceText = FreeText;
            Refresh();
        }
    }

    private bool CanRestorePrevious() => !IsBusy && !HasProblem && _service.ReadRestorePoint() is { UsableForRestore: true };

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
            ? "This machine already has the mods that save was played with."
            : "Ticked to match that save. Change any you would rather leave alone, then press Apply.";
    }

    private string BuildRestorePointText()
    {
        if (_service.ReadRestorePoint() is not { UsableForRestore: true } point)
        {
            return "";
        }

        int count = point.Mods?.Mods.Count ?? 0;
        return $"Your previous list, {count} mods, saved {point.TakenAt.LocalDateTime:d MMM HH:mm}.";
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
        RestorePreviousCommand.NotifyCanExecuteChanged();
        RaiseChangeStates();
    }
}
