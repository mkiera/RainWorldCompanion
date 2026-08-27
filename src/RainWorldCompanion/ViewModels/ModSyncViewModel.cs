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

    public ModSyncRowViewModel(ModSyncRow row)
    {
        _row = row;
        include = row.Include;
    }

    [ObservableProperty]
    private bool include;

    public string Name => _row.Name;

    public string Id => _row.Id;

    public ModSyncAction Action => _row.Action;

    public bool CanChoose => _row.IsChange;

    public string WorkshopUrl => _row.WorkshopId is { Length: > 0 } id && id.All(char.IsAsciiDigit)
        ? ModListDiffViewModel.WorkshopUrlPrefix + id
        : "";

    public bool HasWorkshopPage => WorkshopUrl.Length > 0;

    public string DetailText => _row.Action switch
    {
        ModSyncAction.TurnOn => Join("Off right now", VersionPart(_row.Version)),
        ModSyncAction.TurnOff => Join("On right now, and this save did not use it", VersionPart(_row.Version)),
        ModSyncAction.Install => Join("Not on this machine", VersionPart(_row.RecordedVersion, "the save used ")),
        _ => MatchDetail(),
    };

    public string AccessibleName => $"{Name}, {DetailText}";

    private string MatchDetail()
    {
        if (_row.RecordedVersion is { Length: > 0 } was
            && _row.Version is { Length: > 0 } now
            && !string.Equals(was, now, StringComparison.OrdinalIgnoreCase))
        {
            return $"On at {now}, and the save used {was}.";
        }

        return Join("On", VersionPart(_row.Version));
    }

    partial void OnIncludeChanged(bool value) => _row.Include = value;

    private static string VersionPart(string? version, string prefix = "version ")
        => version is { Length: > 0 } text ? prefix + text : "";

    private static string Join(string lead, string tail)
        => tail.Length == 0 ? lead + "." : lead + ", " + tail + ".";
}

public sealed partial class ModSyncViewModel : ObservableObject
{
    public const string SteamRunUrl = "steam://rungameid/" + CurrentModsReader.SteamAppId;

    private readonly ModSyncService _service;
    private ModSyncPlan? _plan;
    private ModListSnapshot? _recorded;

    public ModSyncViewModel(ModSyncService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        Refresh();
    }

    public ObservableCollection<ModSyncRowViewModel> TurnOn { get; } = new();

    public ObservableCollection<ModSyncRowViewModel> TurnOff { get; } = new();

    public ObservableCollection<ModSyncRowViewModel> Install { get; } = new();

    public ObservableCollection<ModSyncRowViewModel> Matching { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestorePreviousCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool isBusy;

    [ObservableProperty]
    private string sourceText = "Your mods as they are now.";

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

    public bool CanLaunch => AppliedSomething;

    public bool HasTurnOn => TurnOn.Count > 0;

    public bool HasTurnOff => TurnOff.Count > 0;

    public bool HasInstall => Install.Count > 0;

    public bool HasMatching => Matching.Count > 0;

    public string TurnOnHeader => $"Turn on ({TurnOn.Count})";

    public string TurnOffHeader => $"Turn off ({TurnOff.Count})";

    public string InstallHeader => $"Install first ({Install.Count})";

    public string MatchingHeader => $"Already matching ({Matching.Count})";

    public void Match(ModListSnapshot? recorded, string? sourceName)
    {
        _recorded = recorded;
        SourceText = recorded is null
            ? "Your mods as they are now."
            : $"Matching the mods {sourceName ?? "that save"} was played with.";

        ResultText = "";
        Refresh();
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private void Refresh()
    {
        _plan = _service.BuildPlan(_recorded);
        ProblemText = _service.WhyNotNow() ?? "";

        TurnOn.Clear();
        TurnOff.Clear();
        Install.Clear();
        Matching.Clear();

        foreach (ModSyncRow row in _plan.Rows)
        {
            var view = new ModSyncRowViewModel(row);

            switch (row.Action)
            {
                case ModSyncAction.TurnOn:
                    TurnOn.Add(view);
                    break;
                case ModSyncAction.TurnOff:
                    TurnOff.Add(view);
                    break;
                case ModSyncAction.Install:
                    Install.Add(view);
                    break;
                default:
                    Matching.Add(view);
                    break;
            }
        }

        HeadlineText = BuildHeadline();
        RestorePointText = BuildRestorePointText();
        RaiseListStates();
    }

    private bool CanRefresh() => !IsBusy;

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

            // Back to showing the machine rather than the save just stepped away from, because the
            // list on screen is no longer the one that was applied.
            _recorded = null;
            SourceText = "Your mods as they are now.";
            Refresh();
        }
    }

    private bool CanRestorePrevious() => !IsBusy && !HasProblem && _service.ReadRestorePoint() is { UsableForRestore: true };

    public void ReportLinkProblem(string message) => ResultText = "That link could not be opened: " + message;

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
            return $"{Matching.Count + TurnOff.Count} mods are on. Open a backup or a library save to match what it was played with.";
        }

        if (_plan.Diff.NothingWasRecorded)
        {
            return "That save was made before the app recorded mods, so there is nothing to match it against.";
        }

        if (_plan.Diff.RecordedCouldNotLook)
        {
            return "That save recorded that it could not read which mods were on, so there is nothing to match.";
        }

        if (_plan.NothingToDo && Install.Count == 0)
        {
            return "This machine already has the mods that save was played with.";
        }

        int changes = TurnOn.Count + TurnOff.Count;
        string missing = Install.Count == 0 ? "" : $" {Install.Count} would need installing first.";

        return $"{changes} mods would change to match that save.{missing} Clear any you would rather leave alone.";
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

    private void RaiseListStates()
    {
        OnPropertyChanged(nameof(HasTurnOn));
        OnPropertyChanged(nameof(HasTurnOff));
        OnPropertyChanged(nameof(HasInstall));
        OnPropertyChanged(nameof(HasMatching));
        OnPropertyChanged(nameof(TurnOnHeader));
        OnPropertyChanged(nameof(TurnOffHeader));
        OnPropertyChanged(nameof(InstallHeader));
        OnPropertyChanged(nameof(MatchingHeader));
        OnPropertyChanged(nameof(HasProblem));
        OnPropertyChanged(nameof(HasResult));

        ApplyCommand.NotifyCanExecuteChanged();
        RestorePreviousCommand.NotifyCanExecuteChanged();
    }
}
