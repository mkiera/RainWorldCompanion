using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RainWorldSaveManager.Core.Backups;
using RainWorldSaveManager.Core.Saves;
using RainWorldSaveManager.Core.Saves.Models;
using RainWorldSaveManager.Core.Settings;
using RainWorldSaveManager.Core.System;
using RainWorldSaveManager.Services;
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
    private readonly SlugcatIconProvider _icons;
    private readonly string _appVersion;
    private readonly DispatcherTimer _gameTimer;

    /// <summary>Cancelled by <see cref="Shutdown"/>, so nothing started before the window closed
    /// tries to write to the view model afterwards.</summary>
    private readonly CancellationTokenSource _shutdown = new();

    private AppSettings _settings;
    private BackupService? _backupService;

    /// <summary>Built beside the backup service, because it borrows that service's safety snapshot.</summary>
    private SlotCopyService? _copyService;

    // 1 while a poll is running. Interlocked because the poll's own continuation clears it on a
    // worker thread while the timer tick sets it on the dispatcher.
    private int _pollInFlight;

    // Set while one selection is being moved out of the way for the other. The detail panel is
    // rebuilt once, by the outer set, instead of once per property that changes on the way.
    private bool _movingSelection;

    private IReadOnlyList<SlotMetadata> _liveSlotData = Array.Empty<SlotMetadata>();
    private long _liveSizeBytes;
    private int _liveFileCount;

    // meadow.json for the live folder and for each snapshot, read during the refresh with the rest
    // of the disk work. Null means the folder holds no such file, which is what a save folder
    // without Rain Meadow looks like and is why the panel leaves the section out rather than
    // reporting a missing file. A snapshot with no entry here is the same case.
    private MeadowProfile? _liveMeadow;
    private IReadOnlyDictionary<string, MeadowProfile> _backupMeadow =
        new Dictionary<string, MeadowProfile>(StringComparer.OrdinalIgnoreCase);

    public MainViewModel(
        SettingsStore settingsStore,
        IGameProcessDetector gameDetector,
        SlugcatIconProvider icons,
        string appVersion)
    {
        _settingsStore = settingsStore;
        _gameDetector = gameDetector;
        _icons = icons;
        _appVersion = appVersion;

        // Empty on purpose. This runs on the dispatcher inside App.OnStartup, before the window
        // is shown, and every way of guessing a path from here touches disk. InitializeAsync
        // loads the real settings on a worker and FillInMissingPathsAsync fills the gaps.
        _settings = new AppSettings();

        _gameTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _gameTimer.Tick += OnGameTimerTick;
    }

    /// <summary>The three save files as they are on disk, shown as the top card in the list column.</summary>
    public ObservableCollection<SlotViewModel> LiveSlots { get; } = new();

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

    /// <summary>
    /// True when the live save card is the selection. It and <see cref="SelectedBackup"/> are two
    /// halves of one selection: picking either one clears the other.
    /// </summary>
    [ObservableProperty]
    private bool isLiveSelected;

    /// <summary>Whatever the detail panel is showing, or null before the first load.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    [NotifyPropertyChangedFor(nameof(HasNoDetail))]
    private SnapshotDetailViewModel? detail;

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

    public bool HasDetail => Detail is not null;

    public bool HasNoDetail => Detail is null;

    public string BackupCountText => Backups.Count == 1 ? "1 backup" : Backups.Count + " backups";

    /// <summary>
    /// The one line under the LIVE SAVE heading, for example "3 slots   11 campaigns".
    ///
    /// The campaign count is built by the same helper the detail header and the backup rows use.
    /// Counting the rows on this card alone made the card and the header beside it print two
    /// different totals for the same folder, because the header counted the Rain Meadow online
    /// saves too and the card lists only the local ones.
    /// </summary>
    public string LiveSummaryText
    {
        get
        {
            if (LiveSlots.Count == 0)
            {
                return "";
            }

            var local = 0;
            foreach (var slot in LiveSlots)
            {
                local += slot.Campaigns.Count;
            }

            var online = 0;
            foreach (var slot in _liveSlotData)
            {
                if (slot.Realm == SaveRealm.Online)
                {
                    online += slot.Campaigns.Count;
                }
            }

            var slots = LiveSlots.Count == 1 ? "1 slot" : LiveSlots.Count + " slots";
            return slots + "   " + CampaignCount.Describe(local, online);
        }
    }

    /// <summary>
    /// The Rain Meadow line under the live save card, or empty when the folder holds no online
    /// save. The card lists the local slots only, so without this an online save would be invisible
    /// until the detail panel was opened.
    /// </summary>
    public string LiveOnlineText
    {
        get
        {
            var online = 0;
            foreach (var slot in _liveSlotData)
            {
                if (slot.Realm == SaveRealm.Online)
                {
                    online++;
                }
            }

            return online switch
            {
                0 => "",
                1 => "1 Rain Meadow online save",
                _ => online + " Rain Meadow online saves",
            };
        }
    }

    /// <summary>
    /// What a screen reader announces for the live save card. The card is a button wrapping a
    /// panel of text blocks, which on its own gives the container no name at all.
    /// </summary>
    public string LiveAccessibleName
    {
        get
        {
            var summary = LiveSummaryText;
            var online = LiveOnlineText.Length == 0 ? "" : ", " + LiveOnlineText;

            return summary.Length == 0
                ? "Live save, no save files found" + online
                : "Live save, " + summary.Replace("   ", ", ") + online;
        }
    }

    /// <summary>Called once when the window is loaded.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            _settings = await Task.Run(_settingsStore.Load);
        }
        catch (Exception ex)
        {
            // Blank rather than CreateDefault, for the same reason as the constructor: this is
            // the dispatcher. FillInMissingPathsAsync fills both paths on a worker next.
            _settings = new AppSettings();
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

        // Stopping the timer cannot cancel a poll that is already inside the process
        // enumeration. This is what tells that poll to drop its result instead of writing it to a
        // window that has gone.
        _shutdown.Cancel();
    }

    // The copy buttons in the detail panel are their own commands, so the attributes on IsBusy and
    // IsGameRunning cannot reach them. These two put the copy buttons behind the same gate as New
    // Backup and Restore.
    partial void OnIsBusyChanged(bool value) => Detail?.RaiseCopyStates();

    partial void OnIsGameRunningChanged(bool value) => Detail?.RaiseCopyStates();

    /// <summary>Shows the save folder as it is on disk, so a backup can be read against it.</summary>
    [RelayCommand]
    private void SelectLive() => IsLiveSelected = true;

    partial void OnIsLiveSelectedChanged(bool value)
    {
        if (_movingSelection)
        {
            return;
        }

        _movingSelection = true;
        try
        {
            if (value)
            {
                SelectedBackup = null;
            }
        }
        finally
        {
            _movingSelection = false;
        }

        RebuildDetail();
    }

    partial void OnSelectedBackupChanged(BackupItemViewModel? value)
    {
        if (_movingSelection)
        {
            return;
        }

        _movingSelection = true;
        try
        {
            if (value is not null)
            {
                IsLiveSelected = false;
            }
        }
        finally
        {
            _movingSelection = false;
        }

        RebuildDetail();
    }

    /// <summary>
    /// Fills the detail panel from whatever is selected. Both sources are already in memory: the
    /// live slots were read during the last refresh and a backup carries its own manifest, so
    /// selecting a row costs no disk read.
    /// </summary>
    private void RebuildDetail()
    {
        if (IsLiveSelected)
        {
            Detail = SnapshotDetailViewModel.ForLive(
                _liveSlotData,
                SavePathText,
                _liveSizeBytes,
                _liveFileCount,
                _liveMeadow,
                BuildCopyGate(),
                _icons);
            return;
        }

        Detail = SelectedBackup is { } item
            ? SnapshotDetailViewModel.ForBackup(item, FindMeadow(item.Id), _icons)
            : null;
    }

    /// <summary>
    /// What the copy buttons in the detail panel talk to, or null when there is no service to copy
    /// with. Null is what keeps the buttons off a panel built before the settings were valid.
    /// </summary>
    private SlotCopyGate? BuildCopyGate() =>
        _copyService is null ? null : new SlotCopyGate(CanCopySlot, RequestSlotCopy);

    private bool CanCopySlot() => !IsBusy && !IsGameRunning && _copyService is not null;

    // async void because the gate hands the panel a plain Action. Everything CopySlotAsync can
    // fail at is already reported as a message box, and this catch is for the rest.
    private async void RequestSlotCopy(int slot, bool toOnline)
    {
        try
        {
            await CopySlotAsync(slot, toOnline);
        }
        catch (Exception ex)
        {
            Report("The copy could not be started.", ex);
        }
    }

    private MeadowProfile? FindMeadow(string id) =>
        _backupMeadow.TryGetValue(id, out var profile) ? profile : null;

    /// <summary>
    /// Copies one whole slot file onto the other half of the same slot number.
    ///
    /// <paramref name="toOnline"/> true means the local save is copied onto the Rain Meadow online
    /// save, false means the other way. Core does the work: this asks it for a plan, shows that
    /// plan, and runs the copy on a worker with the busy overlay up, the same shape as Restore.
    /// </summary>
    private async Task CopySlotAsync(int slot, bool toOnline)
    {
        var copies = _copyService;
        if (copies is null || IsBusy || IsGameRunning)
        {
            return;
        }

        var from = new SaveSlotRef(toOnline ? SaveRealm.Local : SaveRealm.Online, slot);
        var to = new SaveSlotRef(toOnline ? SaveRealm.Online : SaveRealm.Local, slot);

        SlotCopyPlan? plan = null;
        Exception? failure = null;

        BeginBusy("Copy save slot", "Working out what would change");
        try
        {
            plan = await Task.Run(() => copies.PlanCopy(from, to));
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
            Report("The copy could not be worked out.", failure);
            return;
        }

        if (!plan!.CanCopy)
        {
            ShowMessage(
                "This copy cannot be made.\n\n" + FormatList(plan.Problems),
                "Copy save slot",
                MessageBoxImage.Warning);
            return;
        }

        var dialog = new CopySlotDialog(plan);
        if (ShowDialog(dialog) != true)
        {
            return;
        }

        var progress = new Progress<string>(message => BusyMessage = message);
        SlotCopyResult? result = null;

        BeginBusy("Copying save slot", "Taking a safety snapshot");
        try
        {
            result = await Task.Run(() =>
                copies.CopySlot(from, to, progress, CancellationToken.None));
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
            Report("The copy failed.", failure);
            return;
        }

        ReportCopyResult(result!);
    }

    /// <summary>
    /// Reports a finished copy. The headline is built by Core, so a copy that wrote to the save
    /// folder can never be reported with the same wording as one that refused to start.
    /// </summary>
    private void ReportCopyResult(SlotCopyResult result)
    {
        var text = new StringBuilder();
        text.Append(result.Headline()).Append("\n\n");

        if (result.Success)
        {
            if (result.SafetySnapshot is { } safety)
            {
                text.Append("Safety snapshot of your previous saves: ").Append(safety.Id).Append("\n\n");
            }

            text.Append(SteamGuidance);

            if (result.Warnings.Count > 0)
            {
                text.Append("\n\nNotes:\n").Append(FormatList(result.Warnings));
            }

            ShowMessage(text.ToString(), "Copy save slot", MessageBoxImage.Information);
            return;
        }

        text.Append(FormatList(result.Errors));

        if (result.Warnings.Count > 0)
        {
            text.Append("\n\nNotes:\n").Append(FormatList(result.Warnings));
        }

        ShowMessage(text.ToString(), "Copy save slot", MessageBoxImage.Error);
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

        // DefaultBackupRootPath is a Path.Combine and touches no disk, so it is safe here.
        // CreateDefault is not: it probes the save folder.
        if (string.IsNullOrWhiteSpace(_settings.BackupRootPath))
        {
            _settings.BackupRootPath = AppSettings.DefaultBackupRootPath;
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
        // The install path only feeds the portraits, so it is never validated and never blocks
        // anything here. Probing it still touches disk, so it goes on the worker with the rest.
        var installPath = _settings.GameInstallPath;
        await Task.Run(() => _icons.UseInstall(installPath));

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

        // The copy service is the backup service plus the game check, and it goes away with it, so
        // a folder the app cannot back up is also a folder it will not copy a slot inside.
        _copyService = _backupService?.SlotCopies;

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
        var keepLive = IsLiveSelected;

        if (service is null)
        {
            _liveSlotData = Array.Empty<SlotMetadata>();
            _liveSizeBytes = 0;
            _liveFileCount = 0;
            _liveMeadow = null;
            _backupMeadow = new Dictionary<string, MeadowProfile>(StringComparer.OrdinalIgnoreCase);
            LiveSlots.Clear();
            Backups.Clear();
            SelectedBackup = null;
            IsLiveSelected = false;
            Detail = null;
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
            // Reading the saves, listing the snapshots, measuring the live files and decoding the
            // portraits all happen here, on the worker. What comes back is enough to build every
            // view model on the dispatcher without touching disk again.
            var data = await Task.Run(() =>
            {
                var slots = service.ReadLiveSlots();
                var snapshots = service.ListBackups();
                var measured = MeasureLiveFiles(service.SaveRoot, slots);
                _icons.Preload(CollectSlugcatIds(slots, snapshots));

                // One small json per folder, read here with the rest of the disk work so that
                // selecting a row still costs nothing. ListBackups has already read a manifest out
                // of each of these folders, so this is the same order of cost again.
                var liveMeadow = ReadMeadow(service.SaveRoot);
                var backupMeadow = new Dictionary<string, MeadowProfile>(StringComparer.OrdinalIgnoreCase);
                foreach (var snapshot in snapshots)
                {
                    if (ReadMeadow(snapshot.DirectoryPath) is { } profile)
                    {
                        backupMeadow[snapshot.Id] = profile;
                    }
                }

                return (Slots: slots, Snapshots: snapshots, measured.Size, measured.Count, LiveMeadow: liveMeadow, BackupMeadow: backupMeadow);
            });

            _liveSlotData = data.Slots;
            _liveSizeBytes = data.Size;
            _liveFileCount = data.Count;
            _liveMeadow = data.LiveMeadow;
            _backupMeadow = data.BackupMeadow;

            // The card lists the local slots. The Rain Meadow online saves share these slot
            // numbers, so listing both here would show slot 2 twice with no way to tell which is
            // which. They are paired with their local halves in the detail panel instead, and the
            // line under the card says how many there are.
            LiveSlots.Clear();
            foreach (var slot in data.Slots)
            {
                if (slot.Realm == SaveRealm.Online)
                {
                    continue;
                }

                LiveSlots.Add(new SlotViewModel(slot, _icons));
            }

            Backups.Clear();
            foreach (var snapshot in data.Snapshots)
            {
                Backups.Add(new BackupItemViewModel(snapshot, _icons));
            }

            RestoreSelection(keepId, keepLive);
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

    /// <summary>
    /// Puts the selection back where it was after a refresh. The backup that was selected wins if
    /// it is still there; otherwise the newest one; and with no backups at all the live save card
    /// takes the selection so the panel is never blank.
    /// </summary>
    private void RestoreSelection(string? keepId, bool keepLive)
    {
        var restored = keepLive ? null : FindById(keepId) ?? Backups.FirstOrDefault();

        _movingSelection = true;
        try
        {
            IsLiveSelected = restored is null;
            SelectedBackup = restored;
        }
        finally
        {
            _movingSelection = false;
        }

        RebuildDetail();
    }

    /// <summary>
    /// Size and count of the save files behind the live slots. Runs on the worker with the rest
    /// of the disk work, and a file that cannot be measured is left out rather than reported.
    /// </summary>
    private static (long Size, int Count) MeasureLiveFiles(string saveRoot, IReadOnlyList<SlotMetadata> slots)
    {
        long size = 0;
        var count = 0;

        foreach (var slot in slots)
        {
            if (string.IsNullOrWhiteSpace(slot.FileName))
            {
                continue;
            }

            try
            {
                var file = new FileInfo(Path.Combine(saveRoot, slot.FileName));
                if (file.Exists)
                {
                    size += file.Length;
                    count++;
                }
            }
            catch (Exception)
            {
                // The slot still lists and still restores. Only the header's size line loses it.
            }
        }

        return (size, count);
    }

    /// <summary>
    /// meadow.json out of one folder, which is either the save folder or a snapshot, or null when
    /// there is no such file. Runs on the worker with the rest of the disk work.
    ///
    /// The absent case is deliberately separate from a read that failed. A save folder with no
    /// Rain Meadow in it has no meadow.json, and reporting that as an unreadable file would put a
    /// warning in front of every player who does not use the mod.
    /// </summary>
    private static MeadowProfile? ReadMeadow(string folder)
    {
        try
        {
            var path = Path.Combine(folder, MeadowProfile.FileName);
            return File.Exists(path) ? MeadowProfile.Read(path) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Every slugcat id on screen after this refresh, so the portraits are read and decoded on
    /// the worker instead of one file at a time while the list is being built.
    /// </summary>
    private static IEnumerable<string> CollectSlugcatIds(
        IReadOnlyList<SlotMetadata> liveSlots,
        IReadOnlyList<BackupSnapshot> snapshots)
    {
        foreach (var slot in liveSlots)
        {
            foreach (var campaign in slot.Campaigns)
            {
                yield return campaign.SlugcatId;
            }
        }

        foreach (var snapshot in snapshots)
        {
            var slots = snapshot.Manifest?.Slots;
            if (slots is null)
            {
                continue;
            }

            foreach (var slot in slots)
            {
                foreach (var campaign in slot.Campaigns)
                {
                    yield return campaign.SlugcatId;
                }
            }
        }
    }

    // async void, so nothing can observe a failure here. PollGameAsync is written not to throw.
    private async void OnGameTimerTick(object? sender, EventArgs e) => await PollGameAsync();

    /// <summary>
    /// Asks whether Rain World is running and puts the answer on the banner.
    ///
    /// The await does not capture the dispatcher. A poll that is inside the process enumeration
    /// when the user closes the window would otherwise resume by posting to a dispatcher that has
    /// already shut down, which throws on the thread pool from an async void timer tick, where
    /// App.DispatcherUnhandledException cannot reach it and the process ends in a crash report.
    /// The result is marshalled back explicitly instead, and dropped if the window has gone.
    /// </summary>
    private async Task PollGameAsync()
    {
        if (Interlocked.CompareExchange(ref _pollInFlight, 1, 0) != 0)
        {
            return;
        }

        var token = _shutdown.Token;

        try
        {
            var detector = _gameDetector;
            var running = await Task.Run(() => detector.IsGameRunning(out _), token).ConfigureAwait(false);

            if (token.IsCancellationRequested)
            {
                return;
            }

            ApplyGameState(
                running,
                running
                    ? "Rain World is running - close it before backing up or restoring"
                    : "Rain World is closed");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            ApplyGameState(false, "Could not check whether Rain World is running");
        }
        finally
        {
            Interlocked.Exchange(ref _pollInFlight, 0);
        }
    }

    /// <summary>
    /// Writes the poll result on the dispatcher, or drops it when there is no dispatcher left to
    /// write to. Called from a worker thread.
    /// </summary>
    private void ApplyGameState(bool running, string status)
    {
        void Apply()
        {
            IsGameRunning = running;
            GameStatusText = status;
        }

        if (_shutdown.IsCancellationRequested)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Apply();
            return;
        }

        try
        {
            if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
            {
                dispatcher.BeginInvoke(Apply);
            }
        }
        catch (InvalidOperationException)
        {
            // The dispatcher finished shutting down between the check and the post. There is
            // nothing left to update and nothing to report.
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
        OnPropertyChanged(nameof(LiveSummaryText));
        OnPropertyChanged(nameof(LiveOnlineText));
        OnPropertyChanged(nameof(LiveAccessibleName));
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
