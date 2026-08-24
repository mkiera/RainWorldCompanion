using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RainWorldSaveManager.Core.Backups;
using RainWorldSaveManager.Core.Settings;
using RainWorldSaveManager.Core.System;
using RainWorldSaveManager.Views;

namespace RainWorldSaveManager.ViewModels;

/// <summary>
/// State and commands for the main window. Every call that touches disk runs on a background thread.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private const string SteamGuidance =
        "Launch Rain World through Steam before you restart Steam.\n" +
        "If a Steam Cloud Conflict dialog appears, choose the option that keeps the local files (Upload to Steam Cloud).";

    private readonly SettingsStore _settingsStore;
    private readonly IGameProcessDetector _gameDetector;
    private readonly string _appVersion;
    private readonly DispatcherTimer _gameTimer;

    private AppSettings _settings;
    private BackupService? _backupService;
    private bool _pollInFlight;

    public MainViewModel(SettingsStore settingsStore, IGameProcessDetector gameDetector, string appVersion)
    {
        _settingsStore = settingsStore;
        _gameDetector = gameDetector;
        _appVersion = appVersion;
        _settings = AppSettings.CreateDefault();

        _gameTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _gameTimer.Tick += OnGameTimerTick;
    }

    public ObservableCollection<string> LiveSlots { get; } = new();

    public ObservableCollection<BackupItemViewModel> Backups { get; } = new();

    // The banner is not enough on its own. Without these, Restore stays enabled while the game
    // is open and the user is walked all the way through the destructive confirmation before
    // Core refuses the job.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NewBackupCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    private bool isGameRunning;

    [ObservableProperty]
    private string gameStatusText = "Checking whether Rain World is running";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(NewBackupCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    [NotifyCanExecuteChangedFor(nameof(VerifyCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenSettingsCommand))]
    private bool isBusy;

    [ObservableProperty]
    private string busyTitle = "";

    [ObservableProperty]
    private string busyMessage = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    [NotifyCanExecuteChangedFor(nameof(VerifyCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    private BackupItemViewModel? selectedBackup;

    [ObservableProperty]
    private string savePathText = "";

    [ObservableProperty]
    private string backupRootText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConfigProblem))]
    private string configProblem = "";

    public bool HasConfigProblem => ConfigProblem.Length > 0;

    public bool HasLiveSlots => LiveSlots.Count > 0;

    public bool HasNoLiveSlots => LiveSlots.Count == 0;

    public bool HasBackups => Backups.Count > 0;

    public bool HasNoBackups => Backups.Count == 0;

    public string BackupCountText => Backups.Count == 1 ? "1 backup" : Backups.Count + " backups";

    /// <summary>Called once when the window is loaded.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            _settings = await Task.Run(_settingsStore.Load);
        }
        catch (Exception ex)
        {
            _settings = AppSettings.CreateDefault();
            ShowMessage("The settings file could not be read, so defaults are in use.\n\n" + ex.Message,
                "Settings", MessageBoxImage.Warning);
        }

        await FillInMissingPathsAsync();
        await ApplySettingsAsync();

        _gameTimer.Start();
        await PollGameAsync();

        if (_backupService is null)
        {
            var reason = HasConfigProblem
                ? ConfigProblem
                : "Choose the Rain World save folder and a folder to keep backups in.";
            await ShowSettingsAsync(reason);
            return;
        }

        await ReloadAsync();
    }

    /// <summary>Called when the window closes.</summary>
    public void Shutdown()
    {
        _gameTimer.Stop();
        _gameTimer.Tick -= OnGameTimerTick;
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync() => await ReloadAsync();

    private bool CanRefresh() => !IsBusy && _backupService is not null;

    [RelayCommand(CanExecute = nameof(CanCreateBackup))]
    private async Task NewBackupAsync()
    {
        var service = _backupService;
        if (service is null)
        {
            return;
        }

        var dialog = new NewBackupDialog();
        if (ShowDialog(dialog) != true)
        {
            return;
        }

        var label = dialog.BackupLabel;
        var note = dialog.BackupNote;
        var progress = new Progress<string>(message => BusyMessage = message);

        BackupSnapshot? created = null;
        Exception? failure = null;

        BeginBusy("Creating backup", "Collecting files");
        try
        {
            created = await Task.Run(() => service.CreateBackup(label, note, BackupKind.Manual, progress, CancellationToken.None));
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            EndBusy();
        }

        if (failure is not null)
        {
            Report("The backup could not be created.", failure);
            return;
        }

        await ReloadAsync();
        SelectById(created!.Id);
    }

    private bool CanCreateBackup() => !IsBusy && !IsGameRunning && _backupService is not null;

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task RestoreAsync()
    {
        var service = _backupService;
        var item = SelectedBackup;
        if (service is null || item is null)
        {
            return;
        }

        if (!item.CanRestore)
        {
            ShowMessage("This snapshot is incomplete, so it cannot be restored.\n\n" + item.StateText,
                "Restore", MessageBoxImage.Warning);
            return;
        }

        RestorePlan? plan = null;
        Exception? failure = null;

        BeginBusy("Restore", "Working out what would change");
        try
        {
            plan = await Task.Run(() => service.PlanRestore(item.Snapshot));
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            EndBusy();
        }

        if (failure is not null)
        {
            Report("The restore plan could not be built.", failure);
            return;
        }

        var dialog = new RestoreConfirmDialog(plan!, item.DisplayName);
        if (ShowDialog(dialog) != true)
        {
            return;
        }

        var progress = new Progress<string>(message => BusyMessage = message);
        RestoreResult? result = null;

        BeginBusy("Restoring backup", "Taking a safety snapshot");
        try
        {
            result = await Task.Run(() => service.RestoreBackup(item.Snapshot, progress, CancellationToken.None));
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            EndBusy();
        }

        await ReloadAsync();

        if (failure is not null)
        {
            ReportRestoreFailure(failure);
            return;
        }

        ReportRestoreResult(item, result!);
    }

    private bool CanRestore() =>
        !IsBusy && !IsGameRunning && _backupService is not null && SelectedBackup is { CanRestore: true };

    [RelayCommand(CanExecute = nameof(CanUseSelection))]
    private async Task VerifyAsync()
    {
        var service = _backupService;
        var item = SelectedBackup;
        if (service is null || item is null)
        {
            return;
        }

        VerifyResult? result = null;
        Exception? failure = null;

        BeginBusy("Verifying backup", "Re-hashing the snapshot files");
        try
        {
            result = await Task.Run(() => service.Verify(item.Snapshot));
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            EndBusy();
        }

        if (failure is not null)
        {
            Report("The snapshot could not be verified.", failure);
            return;
        }

        item.VerifiedOk = result!.Ok;

        if (result.Ok)
        {
            ShowMessage("Verified. Every file matches the manifest.\n\n" + item.DisplayName,
                "Verify", MessageBoxImage.Information);
            return;
        }

        ShowMessage("Problems were found in this snapshot.\n\n" + item.DisplayName + "\n\n" + FormatList(result.Problems),
            "Verify", MessageBoxImage.Warning);
    }

    [RelayCommand(CanExecute = nameof(CanUseSelection))]
    private void OpenFolder()
    {
        var item = SelectedBackup;
        if (item is null)
        {
            return;
        }

        try
        {
            var info = new ProcessStartInfo("explorer.exe", "\"" + item.Snapshot.DirectoryPath + "\"")
            {
                UseShellExecute = true,
            };
            Process.Start(info);
        }
        catch (Exception ex)
        {
            Report("The snapshot folder could not be opened.", ex);
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseSelection))]
    private async Task DeleteAsync()
    {
        var service = _backupService;
        var item = SelectedBackup;
        if (service is null || item is null)
        {
            return;
        }

        var confirm = AskYesNo(
            "Delete this backup for good?\n\n" + item.DisplayName + "\n" + item.SizeText + "\nFolder: " + item.Snapshot.Id,
            "Delete backup");
        if (!confirm)
        {
            return;
        }

        Exception? failure = null;

        BeginBusy("Deleting backup", item.DisplayName);
        try
        {
            await Task.Run(() => service.DeleteBackup(item.Snapshot));
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            EndBusy();
        }

        await ReloadAsync();

        if (failure is not null)
        {
            Report("The backup could not be deleted.", failure);
        }
    }

    private bool CanUseSelection() => !IsBusy && _backupService is not null && SelectedBackup is not null;

    [RelayCommand(CanExecute = nameof(CanOpenSettings))]
    private async Task OpenSettingsAsync() => await ShowSettingsAsync(null);

    private bool CanOpenSettings() => !IsBusy;

    private async Task ShowSettingsAsync(string? reason)
    {
        var viewModel = new SettingsViewModel(_settingsStore, _settings, reason);
        var dialog = new SettingsDialog(viewModel);
        if (ShowDialog(dialog) != true || viewModel.Result is null)
        {
            return;
        }

        _settings = viewModel.Result;
        await ApplySettingsAsync();
        await ReloadAsync();
    }

    private async Task FillInMissingPathsAsync()
    {
        if (string.IsNullOrWhiteSpace(_settings.GameSavePath))
        {
            _settings.GameSavePath = await Task.Run<string>(() =>
            {
                try
                {
                    return SavePathResolver.FindSavePath() ?? SavePathResolver.DefaultSavePath;
                }
                catch (Exception)
                {
                    return SavePathResolver.DefaultSavePath;
                }
            });
        }

        if (string.IsNullOrWhiteSpace(_settings.BackupRootPath))
        {
            _settings.BackupRootPath = AppSettings.CreateDefault().BackupRootPath;
        }
    }

    /// <summary>
    /// Rebuilds the backup service from the current settings. A null service means the buttons stay disabled.
    ///
    /// The folder probe runs on a background thread. Directory.Exists on a save path that points
    /// at a share whose machine is off blocks for as long as SMB takes to give up, and on the
    /// dispatcher that is an unpainted window Windows marks as not responding.
    /// </summary>
    private async Task ApplySettingsAsync()
    {
        var savePath = _settings.GameSavePath ?? "";
        var backupRoot = _settings.BackupRootPath ?? "";
        SavePathText = savePath.Length == 0 ? "not set" : savePath;
        BackupRootText = backupRoot.Length == 0 ? "not set" : backupRoot;

        var ownsBusy = !IsBusy;
        if (ownsBusy)
        {
            BeginBusy("Checking the folders", savePath.Length == 0 ? "Reading the settings" : savePath);
        }

        try
        {
            await ApplySettingsCoreAsync(savePath, backupRoot);
        }
        finally
        {
            if (ownsBusy)
            {
                EndBusy();
            }
        }
    }

    private async Task ApplySettingsCoreAsync(string savePath, string backupRoot)
    {
        var problem = await Task.Run(() => SettingsValidation.Validate(savePath, backupRoot));
        if (problem is not null)
        {
            _backupService = null;
            ConfigProblem = problem;
        }
        else if (!await Task.Run(() => DirectoryExists(savePath)))
        {
            _backupService = null;
            ConfigProblem = "The save folder does not exist: " + savePath;
        }
        else
        {
            var built = await Task.Run<(BackupService? Service, string? Error)>(() =>
            {
                try
                {
                    return (new BackupService(savePath, backupRoot, _gameDetector, _appVersion), null);
                }
                catch (Exception ex)
                {
                    return (null, ex.Message);
                }
            });

            _backupService = built.Service;
            ConfigProblem = built.Error is null ? "" : "The backup service could not be started: " + built.Error;
        }

        RaiseCommandStates();
    }

    private static bool DirectoryExists(string path)
    {
        try
        {
            return path.Length > 0 && Directory.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task ReloadAsync()
    {
        var service = _backupService;
        var keepId = SelectedBackup?.Id;

        if (service is null)
        {
            LiveSlots.Clear();
            Backups.Clear();
            SelectedBackup = null;
            RaiseListStates();
            return;
        }

        var ownsBusy = !IsBusy;
        if (ownsBusy)
        {
            BeginBusy("Refreshing", "Reading the save folder and the backup folder");
        }

        Exception? failure = null;

        try
        {
            var data = await Task.Run(() =>
            {
                var slots = service.ReadLiveSlots();
                var snapshots = service.ListBackups();
                return (Slots: slots, Snapshots: snapshots);
            });

            LiveSlots.Clear();
            foreach (var slot in data.Slots)
            {
                var line = slot.Describe();
                LiveSlots.Add(string.IsNullOrWhiteSpace(line) ? slot.FileName : line);
            }

            Backups.Clear();
            foreach (var snapshot in data.Snapshots)
            {
                Backups.Add(new BackupItemViewModel(snapshot));
            }

            SelectedBackup = FindById(keepId) ?? Backups.FirstOrDefault();
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            if (ownsBusy)
            {
                EndBusy();
            }

            RaiseListStates();
        }

        if (failure is not null)
        {
            Report("The save folder or the backup folder could not be read.", failure);
        }
    }

    private BackupItemViewModel? FindById(string? id) =>
        id is null ? null : Backups.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));

    private void SelectById(string id)
    {
        var match = FindById(id);
        if (match is not null)
        {
            SelectedBackup = match;
        }
    }

    private async void OnGameTimerTick(object? sender, EventArgs e) => await PollGameAsync();

    private async Task PollGameAsync()
    {
        if (_pollInFlight)
        {
            return;
        }

        _pollInFlight = true;
        try
        {
            var detector = _gameDetector;
            var running = await Task.Run(() => detector.IsGameRunning(out _));

            IsGameRunning = running;
            GameStatusText = running
                ? "Rain World is running - close it before backing up or restoring"
                : "Rain World is closed";
        }
        catch (Exception)
        {
            IsGameRunning = false;
            GameStatusText = "Could not check whether Rain World is running";
        }
        finally
        {
            _pollInFlight = false;
        }
    }

    private void BeginBusy(string title, string message)
    {
        BusyTitle = title;
        BusyMessage = message;
        IsBusy = true;
    }

    private void EndBusy()
    {
        IsBusy = false;
        BusyTitle = "";
        BusyMessage = "";
    }

    private void RaiseCommandStates()
    {
        RefreshCommand.NotifyCanExecuteChanged();
        NewBackupCommand.NotifyCanExecuteChanged();
        RestoreCommand.NotifyCanExecuteChanged();
        VerifyCommand.NotifyCanExecuteChanged();
        OpenFolderCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        OpenSettingsCommand.NotifyCanExecuteChanged();
    }

    private void RaiseListStates()
    {
        OnPropertyChanged(nameof(HasLiveSlots));
        OnPropertyChanged(nameof(HasNoLiveSlots));
        OnPropertyChanged(nameof(HasBackups));
        OnPropertyChanged(nameof(HasNoBackups));
        OnPropertyChanged(nameof(BackupCountText));
    }

    private void ReportRestoreResult(BackupItemViewModel item, RestoreResult result)
    {
        var safetyName = result.SafetySnapshot?.Id ?? "none was recorded";
        var text = new StringBuilder();

        // The headline is built by Core, so a restore that wrote to the save folder can never be
        // reported with the same wording as one that refused to start.
        text.Append(result.Headline()).Append("\n\n");

        if (result.Success)
        {
            text.Append("Restored from: ").Append(item.DisplayName).Append('\n');
            text.Append("Safety snapshot of your previous save: ").Append(safetyName).Append("\n\n");
            text.Append(SteamGuidance);

            if (result.Warnings.Count > 0)
            {
                text.Append("\n\nNotes:\n").Append(FormatList(result.Warnings));
            }

            ShowMessage(text.ToString(), "Restore", MessageBoxImage.Information);
            return;
        }

        text.Append(FormatList(result.Errors));

        if (result.Warnings.Count > 0)
        {
            text.Append("\n\nNotes:\n").Append(FormatList(result.Warnings));
        }

        if (result.LiveFolderModified && result.SafetySnapshot is not null)
        {
            text.Append("\n\nTo put the save folder back as it was, select backup ")
                .Append(safetyName)
                .Append(" in the list and restore it.");
        }

        ShowMessage(text.ToString(), "Restore", MessageBoxImage.Error);
    }

    /// <summary>
    /// An exception thrown out of RestoreBackup carries no result, and the restore may already
    /// have overwritten the save folder. The safety snapshot is the only way back, so it is
    /// found in the refreshed list and named here rather than being lost with the result.
    /// </summary>
    private void ReportRestoreFailure(Exception failure)
    {
        if (failure is GameRunningException running)
        {
            ReportGameRunning(running);
            return;
        }

        var text = new StringBuilder("The restore failed.\n\n");
        text.Append(failure.Message);

        var safety = Backups.FirstOrDefault(backup => backup.Snapshot.Kind == BackupKind.PreRestoreSafety);
        if (safety is not null)
        {
            text.Append("\n\nThe save folder may be part restored. The safety snapshot taken before the restore is ")
                .Append(safety.Id)
                .Append(". Restoring it puts the save folder back as it was.");
        }

        ShowMessage(text.ToString(), "Restore", MessageBoxImage.Error);
    }

    private void ReportGameRunning(GameRunningException ex)
    {
        ShowMessage(
            ex.ProcessName + " is running. Close Rain World and try again.\n\n" +
            "Backups and restores are blocked while the game is open because it writes to the save files.",
            "Rain World is running",
            MessageBoxImage.Warning);
    }

    /// <summary>
    /// Every command failure lands here, so an IOException reads as a message instead of ending the app.
    /// </summary>
    private void Report(string headline, Exception ex)
    {
        if (ex is GameRunningException running)
        {
            ReportGameRunning(running);
            return;
        }

        ShowMessage(headline + "\n\n" + ex.Message, "Rain World Save Manager", MessageBoxImage.Error);
    }

    private static string FormatList(IReadOnlyList<string> items)
    {
        if (items.Count == 0)
        {
            return "No details were reported.";
        }

        const int limit = 20;
        var text = new StringBuilder();
        for (var i = 0; i < items.Count && i < limit; i++)
        {
            text.Append("  ").Append(items[i]).Append('\n');
        }

        if (items.Count > limit)
        {
            text.Append("  and ").Append(items.Count - limit).Append(" more");
        }

        return text.ToString().TrimEnd('\n');
    }

    private static Window? OwnerWindow
    {
        get
        {
            var window = Application.Current?.MainWindow;
            return window is not null && window.IsLoaded ? window : null;
        }
    }

    private static bool? ShowDialog(Window dialog)
    {
        var owner = OwnerWindow;
        if (owner is not null && !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
        }

        return dialog.ShowDialog();
    }

    private static void ShowMessage(string message, string title, MessageBoxImage icon)
    {
        var owner = OwnerWindow;
        if (owner is not null)
        {
            MessageBox.Show(owner, message, title, MessageBoxButton.OK, icon);
        }
        else
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, icon);
        }
    }

    private static bool AskYesNo(string message, string title)
    {
        var owner = OwnerWindow;
        var result = owner is not null
            ? MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
            : MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
        return result == MessageBoxResult.Yes;
    }
}
