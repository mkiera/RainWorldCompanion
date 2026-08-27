using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Editing;
using RainWorldCompanion.Core.Library;
using RainWorldCompanion.Core.Mods;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.Core.Saves.Models;
using RainWorldCompanion.Core.Settings;
using RainWorldCompanion.Core.System;
using RainWorldCompanion.Core.Updates;
using RainWorldCompanion.Services;
using RainWorldCompanion.Views;

namespace RainWorldCompanion.ViewModels;

/// <summary>Every call that touches disk runs on a background thread.</summary>
public sealed partial class MainViewModel : ObservableObject, IBusyGuard
{
    private const string SteamGuidance =
        "Launch Rain World through Steam before you restart Steam.\n" +
        "If a Steam Cloud Conflict dialog appears, choose the option that keeps the local files (Upload to Steam Cloud).";

    private readonly SettingsStore _settingsStore;
    private readonly IGameProcessDetector _gameDetector;
    private readonly SlugcatIconProvider _icons;
    private readonly string _appVersion;
    private readonly DispatcherTimer _gameTimer;

    /// <summary>
    /// Here rather than on UpdateViewModel, which owns no dispatcher so the tests can build one on
    /// any thread. First tick a few seconds after launch, then hourly while the window is open.
    /// </summary>
    private DispatcherTimer? _updateTimer;

    /// <summary>Cancelled by <see cref="Shutdown"/>, so nothing started before the window closed
    /// tries to write to the view model afterwards.</summary>
    private readonly CancellationTokenSource _shutdown = new();

    private AppSettings _settings;
    private BackupService? _backupService;

    /// <summary>Built beside the backup service, because it borrows that service's safety snapshot.</summary>
    private SlotCopyService? _copyService;

    /// <summary>Built beside the backup service too, for the same reason: a load takes a snapshot.</summary>
    private SaveLibrary? _library;

    private ModSyncService? _modSync;

    // One editor at a time: each holds a session over a whole slot file, so two would work from
    // bytes the other had already changed.
    private CampaignViewModel? _openEditor;

    // 1 while a poll is running. Interlocked because the poll's own continuation clears it on a
    // worker thread while the timer tick sets it on the dispatcher.
    private int _pollInFlight;

    // Set while one selection is being moved out of the way for the other. The detail panel is
    // rebuilt once, by the outer set, instead of once per property that changes on the way.
    private bool _movingSelection;

    // Kept here rather than read off the panel, which a reload leaves null: the list box writes
    // null into SelectedBackup as it empties, and reading the realm off a null panel is how a
    // refresh used to drop the user back to the local saves.
    private bool _showOnline;

    private IReadOnlyList<SlotMetadata> _liveSlotData = Array.Empty<SlotMetadata>();
    private long _liveSizeBytes;
    private int _liveFileCount;

    // Null means the folder holds no meadow.json, which is what a save folder without Rain Meadow
    // looks like, so the panel leaves the section out rather than reporting a missing file.
    private MeadowProfile? _liveMeadow;
    private IReadOnlyDictionary<string, MeadowProfile> _backupMeadow =
        new Dictionary<string, MeadowProfile>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The mods on this machine. Null before the first refresh.</summary>
    private CurrentMods? _currentMods;

    private RainMeadowPresence _meadow = RainMeadowPresence.Absent;

    // Cancels the background verify sweep when the list is rebuilt or the window closes.
    private CancellationTokenSource? _verifySweep;

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

        // Empty on purpose. This runs on the dispatcher inside App.OnStartup, and every way of
        // guessing a path from here touches disk. InitializeAsync loads the real settings.
        _settings = new AppSettings();

        _gameTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _gameTimer.Tick += OnGameTimerTick;
    }

    /// <summary>
    /// Null until <see cref="AttachUpdates"/> is called. Leaving it out is a supported state: the
    /// tests build this view model without it and the banner never appears.
    /// </summary>
    [ObservableProperty]
    private UpdateViewModel? updates;

    /// <summary>The first check waits a few seconds, so it never competes with the launch.</summary>
    public void AttachUpdates(UpdateViewModel updates)
    {
        Updates = updates;
        updates.Adopt(_settings);

        _updateTimer = new DispatcherTimer { Interval = UpdateCooldown.StartupDelay };
        _updateTimer.Tick += OnUpdateTimerTick;
        _updateTimer.Start();
    }

    private async void OnUpdateTimerTick(object? sender, EventArgs e)
    {
        // The first tick is the short startup delay. Every one after it is the hourly beat.
        if (_updateTimer is { } timer && timer.Interval != UpdateCooldown.Interval)
        {
            timer.Interval = UpdateCooldown.Interval;
        }

        if (Updates is not { } updates || !updates.IsAutomaticCheckDue())
        {
            return;
        }

        try
        {
            await updates.CheckAsync(userAsked: false, _shutdown.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Every path that writes a save file wraps itself in BeginBusy and EndBusy, so IsBusy covers
    /// all of them. An update ends the process, and a restore ended halfway through has already
    /// overwritten part of the live save folder.
    /// </summary>
    public string? WhyNotNow() => IsBusy
        ? "RainWorld Companion is in the middle of something that writes to your saves. "
          + "Let it finish, then update."
        : null;

    /// <summary>
    /// The updater is given this rather than the store, so there is one writer to settings.json.
    /// Written on a worker from a copy taken on the dispatcher, so the write cannot see a
    /// half-applied change.
    /// </summary>
    private void PersistUpdateSetting(Action<AppSettings> change)
    {
        change(_settings);
        var snapshot = _settings.Clone();

        _ = Task.Run(() =>
        {
            try
            {
                _settingsStore.Save(snapshot);
            }
            catch (Exception)
            {
            }
        });
    }

    /// <summary>
    /// Written synchronously, unlike <see cref="PersistUpdateSetting"/>: this runs from the
    /// window's Closed handler, moments before the process exits, so a background write could
    /// lose the race and never land.
    /// </summary>
    public void SaveWindowGeometry(double width, double height, double left, double top, bool maximized)
    {
        _settings.WindowWidth = width;
        _settings.WindowHeight = height;
        _settings.WindowLeft = left;
        _settings.WindowTop = top;
        _settings.WindowMaximized = maximized;

        try
        {
            _settingsStore.Save(_settings.Clone());
        }
        catch (Exception)
        {
        }
    }

    public UpdateViewModel CreateUpdates(
        BuildStamp build,
        IReleaseSource source,
        IInstallerDownloader downloader,
        IInstallerLauncher launcher,
        Action requestShutdown) =>
        new(build, source, downloader, launcher, this, PersistUpdateSetting, requestShutdown);

    public ObservableCollection<SlotViewModel> LiveSlots { get; } = new();

    public ObservableCollection<BackupItemViewModel> Backups { get; } = new();

    /// <summary>The named saves, newest first.</summary>
    public ObservableCollection<LibraryEntryViewModel> LibraryEntries { get; } = new();

    // Without these, Restore stays enabled while the game is open and the user is walked all the
    // way through the destructive confirmation before Core refuses the job.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NewBackupCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopySlotCommand))]
    [NotifyCanExecuteChangedFor(nameof(StoreSlotCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadSaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpdateEntryCommand))]
    [NotifyCanExecuteChangedFor(nameof(BeginEditCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveEditsCommand))]
    private bool isGameRunning;

    [ObservableProperty]
    private string gameStatusText = "Checking whether Rain World is running";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(NewBackupCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenSettingsCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopySlotCommand))]
    [NotifyCanExecuteChangedFor(nameof(StoreSlotCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadSaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpdateEntryCommand))]
    [NotifyCanExecuteChangedFor(nameof(UndoUpdateCommand))]
    [NotifyCanExecuteChangedFor(nameof(RenameEntryCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportSaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportSaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(BeginEditCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveEditsCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenModSyncCommand))]
    private bool isBusy;

    [ObservableProperty]
    private string busyTitle = "";

    [ObservableProperty]
    private string busyMessage = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    private BackupItemViewModel? selectedBackup;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpdateEntryCommand))]
    [NotifyCanExecuteChangedFor(nameof(UndoUpdateCommand))]
    [NotifyCanExecuteChangedFor(nameof(RenameEntryCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportSaveCommand))]
    private LibraryEntryViewModel? selectedLibraryEntry;

    /// <summary>
    /// Only a view state: switching tabs moves no selection, so a backup stays selected while the
    /// library is on screen.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBackupsTabSelected))]
    private bool isLibraryTabSelected;

    public bool IsBackupsTabSelected
    {
        get => !IsLibraryTabSelected;
        set => IsLibraryTabSelected = !value;
    }

    /// <summary>
    /// This and <see cref="SelectedBackup"/> are two halves of one selection: picking either one
    /// clears the other.
    /// </summary>
    [ObservableProperty]
    private bool isLiveSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    [NotifyPropertyChangedFor(nameof(HasNoDetail))]
    private SnapshotDetailViewModel? detail;

    [ObservableProperty]
    private string savePathText = "";

    [ObservableProperty]
    private string backupRootText = "";

    [ObservableProperty]
    private string libraryRootText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConfigProblem))]
    private string configProblem = "";

    public bool HasConfigProblem => ConfigProblem.Length > 0;

    public bool HasLiveSlots => LiveSlots.Count > 0;

    public bool HasNoLiveSlots => LiveSlots.Count == 0;

    public bool HasBackups => Backups.Count > 0;

    public bool HasNoBackups => Backups.Count == 0;

    public bool HasLibraryEntries => LibraryEntries.Count > 0;

    public bool HasNoLibraryEntries => LibraryEntries.Count == 0;

    public string LibraryCountText => LibraryEntries.Count == 1 ? "1 save" : LibraryEntries.Count + " saves";

    public bool HasDetail => Detail is not null;

    public bool HasNoDetail => Detail is null;

    public string BackupCountText => Backups.Count == 1 ? "1 backup" : Backups.Count + " backups";

    /// <summary>
    /// Counted through the same helper the detail header uses. Counting the rows on this card alone
    /// printed a different total, because the card lists the local slots and the header does not.
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
    /// The card lists the local slots only, so without this an online save would be invisible until
    /// the detail panel was opened.
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
    /// The card is a button wrapping a panel of text blocks, which on its own gives a screen reader
    /// no name at all.
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
            // Blank rather than CreateDefault, because this is the dispatcher.
            // FillInMissingPathsAsync fills both paths on a worker next.
            _settings = new AppSettings();
            ShowMessage("The settings file could not be read, so defaults are in use.\n\n" + ex.Message,
                "Settings", MessageBoxImage.Warning);
        }

        await FillInMissingPathsAsync();
        await ApplySettingsAsync();

        // Again, now that the real settings are here. AttachUpdates runs from App.OnStartup, where
        // _settings is still the blank object the constructor made.
        Updates?.Adopt(_settings);

        // Immediately after the Adopt above, because the version it compares against is one of the
        // settings that only just arrived. On the update timer it would race this method and a tick
        // that won would read a blank version and swallow the notes for good. Started rather than
        // awaited, so a banner never holds up the window.
        _ = Updates?.CheckForWhatsNewAsync(_shutdown.Token);

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

    public void Shutdown()
    {
        _gameTimer.Stop();
        _gameTimer.Tick -= OnGameTimerTick;

        if (_updateTimer is { } updateTimer)
        {
            updateTimer.Stop();
            updateTimer.Tick -= OnUpdateTimerTick;
            _updateTimer = null;
        }

        // Stopping the timer cannot cancel a poll already inside the process enumeration. This
        // tells it to drop its result rather than write it to a window that has gone.
        _shutdown.Cancel();

        _verifySweep?.Cancel();
        _verifySweep?.Dispose();
        _verifySweep = null;
    }

    [RelayCommand]
    private void SelectLive() => IsLiveSelected = true;

    // The live card, a backup row and a library row are one selection wearing three hats. Picking
    // any clears the other two, and _movingSelection keeps that from rebuilding the detail panel
    // once per property on the way through.
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
                SelectedLibraryEntry = null;
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
                SelectedLibraryEntry = null;
            }
        }
        finally
        {
            _movingSelection = false;
        }

        RebuildDetail();
    }

    partial void OnSelectedLibraryEntryChanged(LibraryEntryViewModel? value)
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
                SelectedBackup = null;
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
        // The panel is thrown away and built again, so an open editor is about to be detached from
        // anything on screen. Letting go of it here keeps a session from being held by a card the
        // user can no longer see or cancel.
        CloseOpenEditor();

        // Taken while there still is a panel: a rebuild driven by the list emptying arrives with
        // Detail already null.
        if (Detail is { } current)
        {
            _showOnline = current.ShowOnline;
        }

        // The realm cannot outlive the mod: the toggle that would put it back is drawn only while
        // the mod is on the machine.
        bool keepOnline = _showOnline && _meadow.Present;

        // Stamped before the assignment. MeadowInstalled raises no change notification, so a
        // binding that read ShowMeadowSection when Detail changed would see the default and never
        // look again.
        SnapshotDetailViewModel? built = BuildDetail();
        if (built is not null)
        {
            built.MeadowInstalled = _meadow.Present;
            built.MeadowVersionText = MeadowVersionText;
            built.ShowOnline = keepOnline;
        }

        Detail = built;
    }

    private SnapshotDetailViewModel? BuildDetail()
    {
        if (IsLiveSelected)
        {
            return SnapshotDetailViewModel.ForLive(
                _liveSlotData,
                SavePathText,
                _liveSizeBytes,
                _liveFileCount,
                _liveMeadow,
                _icons,
                _currentMods);
        }

        if (SelectedLibraryEntry is { } entry)
        {
            return SnapshotDetailViewModel.ForLibraryEntry(entry, _icons);
        }

        return SelectedBackup is { } item
            ? SnapshotDetailViewModel.ForBackup(item, FindMeadow(item.Id), _icons)
            : null;
    }

    private string MeadowVersionText =>
        string.IsNullOrWhiteSpace(_meadow.Version) ? "" : "v" + _meadow.Version;

    /// <summary>
    /// Forgets that any library save is in this slot, after something else wrote to it. Swallows
    /// its own failure: this keeps a hint on a row honest, and the write already happened.
    /// </summary>
    private async Task ReleaseSlotClaimAsync(SaveSlotRef slot)
    {
        var library = _library;
        if (library is null)
        {
            return;
        }

        try
        {
            await Task.Run(() => library.ReleaseSlot(slot));
        }
        catch (Exception)
        {
        }
    }

    private async Task ReleaseAllSlotClaimsAsync()
    {
        var library = _library;
        if (library is null)
        {
            return;
        }

        try
        {
            await Task.Run(library.ReleaseAllSlots);
        }
        catch (Exception)
        {
        }
    }

    private MeadowProfile? FindMeadow(string id) =>
        _backupMeadow.TryGetValue(id, out var profile) ? profile : null;

    /// <summary>
    /// Both ends are picked in the dialog, so this is the only entry point and the slot rows in the
    /// panel carry no buttons.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCopySlot))]
    private async Task CopySlotAsync()
    {
        var copies = _copyService;
        if (copies is null || IsBusy || IsGameRunning)
        {
            return;
        }

        (SaveSlotRef from, SaveSlotRef to) = DefaultCopyPair();

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

        // A plan that cannot run still opens the dialog: this pair is only where the pickers start,
        // and refusing to open would leave no way to reach a pair that does work.
        var dialog = new CopySlotDialog(plan!, copies.PlanCopy, _meadow.Present);
        if (ShowDialog(dialog) != true)
        {
            return;
        }

        // The pair that runs is the one the dialog closed on, not the one it opened with.
        from = dialog.ChosenSource;
        to = dialog.ChosenTarget;

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

        // Whatever library save was in that slot is not in it any more.
        if (result?.LiveFolderModified == true)
        {
            await ReleaseSlotClaimAsync(to);
        }

        await ReloadAsync();

        if (failure is not null)
        {
            Report("The copy failed.", failure);
            return;
        }

        ReportCopyResult(result!);
    }

    private bool CanCopySlot() => !IsBusy && !IsGameRunning && _copyService is not null;

    /// <summary>
    /// Nothing is written here. The session sits in memory until it is saved, and closing the
    /// editor drops it.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanBeginEdit))]
    private async Task BeginEditAsync(CampaignViewModel? campaign)
    {
        if (campaign?.EditableSlot is not { } slot || IsBusy || IsGameRunning)
        {
            return;
        }

        if (campaign.IsEditing)
        {
            return;
        }

        string root = _settings.GameSavePath ?? "";
        if (root.Length == 0)
        {
            Report("The save folder is not set, so there is nothing to edit.", null);
            return;
        }

        string path = Path.Combine(root, slot.FileName);

        SaveEditSession? session = null;
        Exception? failure = null;

        BeginBusy("Opening " + slot.FileName, "Reading the save");
        try
        {
            session = await Task.Run(() => SaveEditSession.Open(path));
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            EndBusy();
        }

        if (failure is not null || session is null)
        {
            Report(slot.FileName + " could not be opened for editing.", failure);
            return;
        }

        CampaignRecordRef? record = session.Campaigns
            .FirstOrDefault(c => string.Equals(c.SlugcatId, campaign.SlugcatId, StringComparison.Ordinal));

        if (record is null)
        {
            // The panel was drawn from an earlier reading, so the file changed underneath.
            Report(
                campaign.DisplayName + " is no longer in " + slot.FileName + ". Refresh and try again.",
                null);
            return;
        }

        CloseOpenEditor();

        campaign.Edit = new CampaignEditViewModel(
            session,
            record,
            campaign.Summary,
            ExpansionDetector.Detect(_settings.GameInstallPath));
        _openEditor = campaign;
    }

    private bool CanBeginEdit() => !IsBusy && !IsGameRunning;

    [RelayCommand]
    private void CancelEdit(CampaignViewModel? campaign)
    {
        if (campaign is null)
        {
            return;
        }

        campaign.Edit = null;

        if (ReferenceEquals(_openEditor, campaign))
        {
            _openEditor = null;
        }
    }

    private void CloseOpenEditor()
    {
        if (_openEditor is not null)
        {
            _openEditor.Edit = null;
            _openEditor = null;
        }
    }

    /// <summary>
    /// The plan is built and checked before anything is shown, so a refusal is reported instead of
    /// a confirmation the user agrees to and then watches fail.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSaveEdits))]
    private async Task SaveEditsAsync(CampaignViewModel? campaign)
    {
        if (campaign?.Edit is not { } editor
            || campaign.EditableSlot is not { } slot
            || _backupService is not { } backups
            || IsBusy
            || IsGameRunning)
        {
            return;
        }

        if (!editor.IsDirty)
        {
            Report("Nothing was changed, so there is nothing to save.", null);
            return;
        }

        SaveWritePlan? plan = null;
        Exception? failure = null;

        BeginBusy("Saving " + slot.FileName, "Checking the changes");
        try
        {
            plan = await Task.Run(editor.BuildWritePlan);
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            EndBusy();
        }

        if (failure is not null || plan is null)
        {
            Report("The changes could not be prepared.", failure);
            return;
        }

        if (!plan.CanWrite)
        {
            Report("These changes will not be saved.\n\n" + FormatList(plan.Problems), null);
            return;
        }

        var dialog = new SaveEditsDialog(plan, campaign.DisplayName, slot.FileName, editor.Warnings.ToArray());
        if (ShowDialog(dialog) != true)
        {
            return;
        }

        var progress = new Progress<string>(message => BusyMessage = message);
        SaveWriteResult? result = null;

        BeginBusy("Saving " + slot.FileName, "Taking a backup first");
        try
        {
            result = await Task.Run(() => backups.SlotWriter.Write(plan, slot, progress, CancellationToken.None));
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
            await ReloadAsync();
            Report("The changes could not be saved.", failure);
            return;
        }

        // Whatever library save claimed that slot no longer matches what is in it.
        if (result!.LiveFolderModified)
        {
            await ReleaseSlotClaimAsync(slot);
        }

        // Rebuilding from disk closes the editor, whose session describes bytes no longer there.
        await ReloadAsync();

        ReportSaveResult(result, slot.FileName);
    }

    private bool CanSaveEdits() => !IsBusy && !IsGameRunning;

    private void ReportSaveResult(SaveWriteResult result, string fileName)
    {
        var text = new StringBuilder();
        text.Append(result.Headline()).Append("\n\n");

        if (result.Success)
        {
            if (result.SafetySnapshot is { } safety)
            {
                text.Append("Backup of your previous saves: ").Append(safety.Id).Append("\n\n");
            }

            text.Append(SteamGuidance);

            if (result.Warnings.Count > 0)
            {
                text.Append("\n\nNotes:\n").Append(FormatList(result.Warnings));
            }

            ShowMessage(text.ToString(), "Save changes", MessageBoxImage.Information);
            return;
        }

        text.Append(FormatList(result.Errors));

        if (result.Warnings.Count > 0)
        {
            text.Append("\n\nNotes:\n").Append(FormatList(result.Warnings));
        }

        ShowMessage(text.ToString(), "Save changes", MessageBoxImage.Error);
    }

    /// <summary>
    /// Nothing in the save folder is written, so there is no safety snapshot and no confirmation
    /// beyond the dialog itself.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanActOnCampaign))]
    private async Task StoreCampaignAsync(CampaignViewModel? campaign)
    {
        SaveLibrary? library = _library;

        if (library is null || campaign?.Source is not { CanBeTaken: true } source || IsBusy || IsGameRunning)
        {
            return;
        }

        var dialog = new RenameEntryDialog(
            SuggestCampaignName(campaign),
            "",
            "Save " + campaign.DisplayName + " to the library",
            "Only this campaign is stored. " + WhereItIs(source) + " is not touched, and the other campaigns in it stay where they are.",
            "Save it");

        if (ShowDialog(dialog) != true)
        {
            return;
        }

        string name = dialog.EntryName;
        string? note = dialog.EntryNote;
        string slugcat = campaign.SlugcatId;

        // A campaign taken out of a backup carries that snapshot's mod list rather than what is on
        // right now. The bytes are from then, so the record has to be too.
        ModListSnapshot? recorded = RecordedModsOfSelection();

        LibraryEntry? stored = null;
        Exception? failure = null;

        BeginBusy("Storing " + campaign.DisplayName, name);
        try
        {
            // A live slot is read under the operation lock, because the game and Steam Cloud both
            // write to that folder. A backup and a library save are nobody else's to rewrite, so
            // they are read where they lie.
            stored = source.LiveSlot is { } slot
                ? await Task.Run(() => library.StoreCampaign(slot, slugcat, name, note))
                : await Task.Run(() =>
                {
                    CampaignSlice slice = ReadCampaignFrom(source, slugcat)
                        ?? throw new InvalidOperationException(NotThereAnyMore(campaign.DisplayName, source));

                    return library.StoreCampaignFrom(
                        slice, source.FileName, source.Realm, source.SlotNumber, name, note, recorded);
                });
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
            Report(campaign.DisplayName + " could not be stored.", failure);
            return;
        }

        ShowLibrarySave(stored!.Id);
    }

    /// <summary>
    /// A move is two writes and the order matters: the campaign lands in the slot it is going to
    /// before it leaves the one it came from, so a refused second write leaves it in both.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanActOnCampaign))]
    private async Task SendCampaignAsync(CampaignViewModel? campaign)
    {
        if (_backupService is not { } backups || campaign?.Source is not { CanBeTaken: true } source
            || IsBusy || IsGameRunning)
        {
            return;
        }

        SaveSlotWriter writer = backups.SlotWriter;
        string slugcat = campaign.SlugcatId;

        CampaignSlice? slice;
        Exception? failure = null;

        BeginBusy("Reading " + WhereItIs(source), campaign.DisplayName);
        try
        {
            slice = await Task.Run(() => ReadCampaignFrom(source, slugcat));
        }
        catch (Exception ex)
        {
            failure = ex;
            slice = null;
        }
        finally
        {
            EndBusy();
        }

        if (failure is not null)
        {
            Report(WhereItIs(source) + " could not be read.", failure);
            return;
        }

        if (slice is null)
        {
            Report(NotThereAnyMore(campaign.DisplayName, source), null);
            return;
        }

        // The dialog asks Core what each slot would do with the campaign, which means reading those
        // slots. A slot that will not read at all is a reason to say so rather than to open a window
        // that cannot describe anything.
        SendCampaignDialog dialog;
        try
        {
            dialog = new SendCampaignDialog(
                campaign.DisplayName,
                source.LiveSlot,
                WhereItIs(source),
                target => writer.PlanPutCampaign(target, slice),
                _meadow.Present,
                // Live to live is one machine against itself, so there is nothing to compare.
                source.LiveSlot is null ? DiffAgainstNow(RecordedModsOfSelection()) : null);
        }
        catch (Exception ex)
        {
            Report("The save folder could not be read.", ex);
            return;
        }

        if (ShowDialog(dialog) != true)
        {
            return;
        }

        SaveSlotRef target = dialog.ChosenTarget;
        bool takeItOut = dialog.ChosenToTakeItOut && source.LiveSlot is not null;
        var progress = new Progress<string>(message => BusyMessage = message);

        SaveWriteResult? arrival = null;
        SaveWriteResult? departure = null;

        BeginBusy("Sending " + campaign.DisplayName + " to " + target.FileName, "Taking a safety snapshot");
        try
        {
            arrival = await Task.Run(() =>
                writer.Write(writer.PlanPutCampaign(target, slice), progress, CancellationToken.None));

            if (arrival.Success && takeItOut && source.LiveSlot is { } from)
            {
                departure = await Task.Run(() => writer.Write(
                    writer.PlanTakeCampaign(from, slugcat, includeMaps: true),
                    progress,
                    CancellationToken.None));
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            EndBusy();
        }

        if (arrival?.LiveFolderModified == true)
        {
            await ReleaseSlotClaimAsync(target);
        }

        if (departure?.LiveFolderModified == true && source.LiveSlot is { } emptied)
        {
            await ReleaseSlotClaimAsync(emptied);
        }

        await ReloadAsync();

        if (failure is not null)
        {
            Report(campaign.DisplayName + " could not be sent to " + target.FileName + ".", failure);
            return;
        }

        ReportCampaignMove(campaign.DisplayName, arrival!, departure, WhereItIs(source), target);
    }

    /// <summary>The map discovery stays, which is what the game's own wipe does.</summary>
    [RelayCommand(CanExecute = nameof(CanActOnCampaign))]
    private async Task DeleteCampaignAsync(CampaignViewModel? campaign)
    {
        if (_backupService is not { } backups || campaign?.EditableSlot is not { } slot
            || IsBusy || IsGameRunning)
        {
            return;
        }

        bool confirmed = AskYesNo(
            "Take " + campaign.DisplayName + " out of " + slot.FileName + "?\n\n"
            + "The other campaigns in that slot are left alone, and the map this one explored stays, the "
            + "way it does when the game itself wipes a save.\n\n"
            + "The whole save folder is copied first, and restoring that backup puts this campaign back.",
            "Delete a campaign");

        if (!confirmed)
        {
            return;
        }

        SaveSlotWriter writer = backups.SlotWriter;
        string slugcat = campaign.SlugcatId;
        var progress = new Progress<string>(message => BusyMessage = message);

        SaveWriteResult? result = null;
        Exception? failure = null;

        BeginBusy("Taking " + campaign.DisplayName + " out of " + slot.FileName, "Taking a safety snapshot");
        try
        {
            result = await Task.Run(() => writer.Write(
                writer.PlanTakeCampaign(slot, slugcat, includeMaps: false),
                progress,
                CancellationToken.None));
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            EndBusy();
        }

        if (result?.LiveFolderModified == true)
        {
            await ReleaseSlotClaimAsync(slot);
        }

        await ReloadAsync();

        if (failure is not null)
        {
            Report(campaign.DisplayName + " could not be taken out of " + slot.FileName + ".", failure);
            return;
        }

        ReportSaveResult(result!, slot.FileName);
    }

    private bool CanActOnCampaign() =>
        !IsBusy && !IsGameRunning && _library is not null && _backupService is not null;

    /// <summary>
    /// The game deletes a slot by writing a reset MISCPROG over everything else. This takes only
    /// the campaigns, because rebuilding MISCPROG would drop every field this app does not model.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDeleteSlot))]
    private async Task DeleteSlotAsync(SlotViewModel? slot)
    {
        if (_backupService is not { } backups || slot?.EditableSlot is not { } target
            || IsBusy || IsGameRunning)
        {
            return;
        }

        SaveSlotWriter writer = backups.SlotWriter;
        Dictionary<SlotDeleteDepth, SlotDeletePlan> plans;

        BeginBusy("Reading " + target.FileName, "Working out what would go");
        try
        {
            // Every depth up front, so the window can grey out a row that would change nothing.
            plans = await Task.Run(() => Enum
                .GetValues<SlotDeleteDepth>()
                .ToDictionary(depth => depth, depth => writer.PlanDeleteSlot(target, depth)));
        }
        catch (Exception ex)
        {
            EndBusy();
            Report(target.FileName + " could not be read.", ex);
            return;
        }

        EndBusy();

        // Nothing any depth could do means the slot is already as empty as this app can make it.
        if (!plans.Values.Any(candidate => candidate.CanWrite))
        {
            SlotDeletePlan plan = plans[SlotDeleteDepth.Everything];

            Report(
                "Nothing in " + target.FileName + " was deleted.\n\n"
                + FormatList(plan.Problems.Count > 0 ? plan.Problems : plan.Write.Problems),
                null);
            return;
        }

        var dialog = new DeleteSlotDialog(plans);

        if (ShowDialog(dialog) != true)
        {
            return;
        }

        SlotDeleteDepth depth = dialog.ChosenDepth;
        var progress = new Progress<string>(message => BusyMessage = message);

        SaveWriteResult? result = null;
        Exception? failure = null;

        BeginBusy("Deleting " + target.FileName, "Taking a safety snapshot");
        try
        {
            result = await Task.Run(() => writer.Write(
                writer.PlanDeleteSlot(target, depth),
                progress,
                CancellationToken.None));
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            EndBusy();
        }

        // Whatever library save claimed that slot no longer matches what is in it.
        if (result?.LiveFolderModified == true)
        {
            await ReleaseSlotClaimAsync(target);
        }

        await ReloadAsync();

        if (failure is not null)
        {
            Report(target.FileName + " could not be deleted.", failure);
            return;
        }

        ReportSaveResult(result!, target.FileName);
    }

    private bool CanDeleteSlot() => !IsBusy && !IsGameRunning && _backupService is not null;

    /// <summary>
    /// Both halves of a send as one message. A move whose second half was refused says so rather
    /// than reading as a success.
    /// </summary>
    private void ReportCampaignMove(
        string campaignName,
        SaveWriteResult arrival,
        SaveWriteResult? departure,
        string sourceName,
        SaveSlotRef target)
    {
        var text = new StringBuilder();

        if (!arrival.Success)
        {
            text.Append(campaignName).Append(" was not written to ").Append(target.FileName).Append(".\n\n");
            text.Append(FormatList(arrival.Errors));
            ShowMessage(text.ToString(), "Send a campaign", MessageBoxImage.Error);
            return;
        }

        text.Append(campaignName).Append(" is now in ").Append(target.FileName).Append(".\n\n");

        if (departure is null)
        {
            text.Append("It is still in ").Append(sourceName).Append(" as well.\n\n");
        }
        else if (departure.Success)
        {
            text.Append("It has been taken out of ").Append(sourceName).Append(".\n\n");
        }
        else
        {
            text.Append("It could not be taken out of ").Append(sourceName)
                .Append(", so it is in both slots for now:\n")
                .Append(FormatList(departure.Errors))
                .Append("\n\n");
        }

        if (arrival.SafetySnapshot is { } safety)
        {
            text.Append("Backup of your previous saves: ").Append(safety.Id).Append("\n\n");
        }

        text.Append(SteamGuidance);

        ShowMessage(
            text.ToString(),
            "Send a campaign",
            departure is { Success: false } ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    /// <summary>Core tells a save container from a campaign file, so this does not have to.</summary>
    private static CampaignSlice? ReadCampaignFrom(CampaignSource source, string slugcatId)
        => CampaignFile.ReadFrom(source.FilePath, slugcatId);

    /// <summary>
    /// Null when the selection is the live save or carries no record. The selection built the panel
    /// the campaign card sits in, so it is the snapshot the campaign was read out of.
    /// </summary>
    private ModListSnapshot? RecordedModsOfSelection()
        => SelectedBackup?.Snapshot.Manifest?.Mods ?? SelectedLibraryEntry?.Entry.Manifest?.Mods;

    /// <summary>Null before the first refresh, which is the "no way to look" the plans mean.</summary>
    private ModListDiff? DiffAgainstNow(ModListSnapshot? recorded)
        => _currentMods is null ? null : ModListDiff.Compare(recorded, _currentMods);

    private static string WhereItIs(CampaignSource source)
        => source.Label.Length > 0 ? source.Label : source.FileName;

    private static string NotThereAnyMore(string campaignName, CampaignSource source)
        => campaignName + " is no longer in " + WhereItIs(source) + ". Refresh and try again.";

    private static string SuggestCampaignName(CampaignViewModel campaign)
        => campaign.Summary.DisplayCycleNum is { } cycle
            ? campaign.DisplayName + " cycle " + cycle.ToString(CultureInfo.InvariantCulture)
            : campaign.DisplayName;

    /// <summary>Where the pickers start. Without the mod there is no online half to offer.</summary>
    private (SaveSlotRef From, SaveSlotRef To) DefaultCopyPair() =>
        _meadow.Present
            ? (new SaveSlotRef(SaveRealm.Local, 1), new SaveSlotRef(SaveRealm.Online, 1))
            : (new SaveSlotRef(SaveRealm.Local, 1), new SaveSlotRef(SaveRealm.Local, 2));

    /// <summary>Nothing in the save folder is written, so there is no safety snapshot.</summary>
    [RelayCommand(CanExecute = nameof(CanStoreSlot))]
    private async Task StoreSlotAsync()
    {
        var library = _library;
        var copies = _copyService;
        if (library is null || copies is null || IsBusy || IsGameRunning)
        {
            return;
        }

        IReadOnlyList<SlotSide> sides;
        Exception? failure = null;

        BeginBusy("Store a slot", "Reading the save folder");
        try
        {
            sides = await Task.Run(() => ReadStorableSides(copies, _meadow.Present));
        }
        catch (Exception ex)
        {
            failure = ex;
            sides = Array.Empty<SlotSide>();
        }
        finally
        {
            EndBusy();
        }

        if (failure is not null)
        {
            Report("The save folder could not be read.", failure);
            return;
        }

        if (sides.Count == 0)
        {
            ShowMessage("There are no save slots to store.", "Store a slot", MessageBoxImage.Warning);
            return;
        }

        var dialog = new StoreSlotDialog(sides, FirstSlotWithASave(sides));
        if (ShowDialog(dialog) != true)
        {
            return;
        }

        var source = dialog.ChosenSource;
        var name = dialog.ChosenName;
        var note = dialog.ChosenNote;
        var progress = new Progress<string>(message => BusyMessage = message);

        LibraryEntry? stored = null;

        BeginBusy("Storing " + source.FileName, name);
        try
        {
            stored = await Task.Run(() => library.StoreSlot(source, name, note, progress, CancellationToken.None));
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
            Report("The slot could not be stored.", failure);
            return;
        }

        ShowLibrarySave(stored!.Id);
    }

    private bool CanStoreSlot() => !IsBusy && !IsGameRunning && _library is not null && _copyService is not null;

    /// <summary>The slot it replaces is in a safety snapshot first, as with a slot copy.</summary>
    [RelayCommand(CanExecute = nameof(CanLoadSave))]
    private async Task LoadSaveAsync()
    {
        var library = _library;
        if (library is null || IsBusy || IsGameRunning)
        {
            return;
        }

        var entries = LibraryEntries.Where(item => item.IsComplete).Select(item => item.Entry).ToList();
        if (entries.Count == 0)
        {
            ShowMessage("There are no library saves to load.", "Load a library save", MessageBoxImage.Warning);
            return;
        }

        var entry = entries.FirstOrDefault(
            item => string.Equals(item.Id, SelectedLibraryEntry?.Id, StringComparison.OrdinalIgnoreCase))
            ?? entries[0];

        var target = entry.Manifest?.LastLoadedSlotRef ?? new SaveSlotRef(SaveRealm.Local, 1);

        LibraryLoadPlan? plan = null;
        Exception? failure = null;

        BeginBusy("Load a library save", "Working out what would change");
        try
        {
            plan = await Task.Run(() => library.PlanAnyLoad(entry, target));
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
            Report("The load could not be worked out.", failure);
            return;
        }

        // A plan that cannot run still opens the dialog, for the reason Copy Slot does: the pair
        // here is only where the pickers start.
        var dialog = new LoadSaveDialog(entries, plan!, library.PlanAnyLoad, _meadow.Present);
        if (ShowDialog(dialog) != true)
        {
            return;
        }

        var chosen = dialog.ChosenEntry;
        var chosenTarget = dialog.ChosenTarget;
        var progress = new Progress<string>(message => BusyMessage = message);

        LibraryLoadResult? result = null;

        BeginBusy("Loading " + chosen.Name, "Taking a safety snapshot");
        try
        {
            result = await Task.Run(() => library.LoadAny(chosen, chosenTarget, progress, CancellationToken.None));
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
            Report("The load failed.", failure);
            return;
        }

        ReportLoadResult(result!);
    }

    private bool CanLoadSave() =>
        !IsBusy && !IsGameRunning && _library is not null && LibraryEntries.Any(item => item.IsComplete);

    /// <summary>The bytes being replaced are kept, so this can be undone.</summary>
    [RelayCommand(CanExecute = nameof(CanUpdateEntry))]
    private async Task UpdateEntryAsync()
    {
        var library = _library;
        var copies = _copyService;
        var item = SelectedLibraryEntry;
        if (library is null || copies is null || item is null || IsBusy || IsGameRunning)
        {
            return;
        }

        // The slot this save was put into is the one that was played. One that has never been put
        // anywhere still knows the slot it was taken from.
        var manifest = item.Entry.Manifest;
        var source = manifest?.LastLoadedSlotRef ?? manifest?.SourceSlotRef;
        if (source is null)
        {
            ShowMessage(
                "This save records no slot, so there is nothing to take it from.\n\n" +
                "Put it in a slot first, then take it back once you have played.",
                "Take from slot",
                MessageBoxImage.Warning);
            return;
        }

        var side = await Task.Run(() => copies.ReadSide(source));

        if (!side.Exists)
        {
            ShowMessage(
                source.FileName + " is not in the save folder, so there is nothing to take from it.",
                "Take from slot",
                MessageBoxImage.Warning);
            return;
        }

        var confirm = AskYesNo(
            "Replace \"" + item.Name + "\" with what is in " + source.FileName + " now?\n\n" +
            source.FileName + ": " + side.Describe() + "\n" +
            "Held now: " + item.CampaignCountText + ", " + item.SizeText + "\n\n" +
            "The save being replaced is kept, so this can be undone.",
            "Take from slot");
        if (!confirm)
        {
            return;
        }

        var progress = new Progress<string>(message => BusyMessage = message);
        Exception? failure = null;

        BeginBusy("Updating " + item.Name, "From " + source.FileName);
        try
        {
            await Task.Run(() => library.UpdateEntry(item.Entry, source, progress, CancellationToken.None));
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            EndBusy();
        }

        var id = item.Id;
        await ReloadAsync();

        if (failure is not null)
        {
            Report("The library save could not be updated.", failure);
            return;
        }

        ShowLibrarySave(id);
    }

    private bool CanUpdateEntry() =>
        !IsBusy && !IsGameRunning && _library is not null && SelectedLibraryEntry is { IsComplete: true };

    [RelayCommand(CanExecute = nameof(CanUndoUpdate))]
    private async Task UndoUpdateAsync()
    {
        var library = _library;
        var item = SelectedLibraryEntry;
        if (library is null || item is null || IsBusy)
        {
            return;
        }

        var confirm = AskYesNo(
            "Go back to the save \"" + item.Name + "\" held before it was last updated?\n\n" +
            "What is stored now is discarded.",
            "Undo an update");
        if (!confirm)
        {
            return;
        }

        Exception? failure = null;

        BeginBusy("Undoing the update", item.Name);
        try
        {
            await Task.Run(() => library.UndoUpdate(item.Entry));
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            EndBusy();
        }

        var id = item.Id;
        await ReloadAsync();

        if (failure is not null)
        {
            Report("The update could not be undone.", failure);
            return;
        }

        ShowLibrarySave(id);
    }

    private bool CanUndoUpdate() =>
        !IsBusy && _library is not null && SelectedLibraryEntry is { CanUndoUpdate: true };

    /// <summary>The stored save itself is not touched.</summary>
    [RelayCommand(CanExecute = nameof(CanRenameEntry))]
    private async Task RenameEntryAsync()
    {
        var library = _library;
        var item = SelectedLibraryEntry;
        if (library is null || item is null || IsBusy)
        {
            return;
        }

        var dialog = new RenameEntryDialog(item.Name, item.NoteText);
        if (ShowDialog(dialog) != true)
        {
            return;
        }

        var name = dialog.EntryName;
        var note = dialog.EntryNote;
        Exception? failure = null;

        try
        {
            await Task.Run(() => library.RenameEntry(item.Entry, name, note));
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        var id = item.Id;
        await ReloadAsync();

        if (failure is not null)
        {
            Report("The library save could not be renamed.", failure);
            return;
        }

        ShowLibrarySave(id);
    }

    private bool CanRenameEntry() =>
        !IsBusy && _library is not null && SelectedLibraryEntry is { IsComplete: true };

    [RelayCommand(CanExecute = nameof(CanExportSave))]
    private async Task ExportSaveAsync()
    {
        var library = _library;
        var item = SelectedLibraryEntry;
        if (library is null || item is null || IsBusy)
        {
            return;
        }

        // A campaign and a whole slot are the same format under different names, and the extension
        // is what tells somebody receiving one which of the two they were sent.
        var extension = SaveLibrary.ExportExtensionFor(item.Entry);
        var title = item.Entry.IsCampaign ? "Export a campaign" : "Export a library save";

        var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = item.Entry.IsCampaign
                ? "Rain World campaign (*.rwcampaign)|*.rwcampaign|All files (*.*)|*.*"
                : "Rain World save bundle (*.rwsave)|*.rwsave|All files (*.*)|*.*",
            DefaultExt = extension,
            AddExtension = true,
            FileName = SafeFileName(item.Name) + extension,
        };

        if (dialog.ShowDialog(OwnerWindow) != true)
        {
            return;
        }

        var destination = dialog.FileName;
        Exception? failure = null;

        BeginBusy("Exporting " + item.Name, destination);
        try
        {
            await Task.Run(() => library.ExportEntry(item.Entry, destination));
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
            Report("The save could not be exported.", failure);
            return;
        }

        ShowMessage(
            "Exported \"" + item.Name + "\" to:\n" + destination +
            (item.Entry.IsCampaign
                ? "\n\nThe file holds the campaign and the name, the note and its detail beside it."
                : "\n\nThe file holds the save and the name, the note and the campaigns beside it."),
            title,
            MessageBoxImage.Information);
    }

    private bool CanExportSave() =>
        !IsBusy && _library is not null && SelectedLibraryEntry is { IsComplete: true };

    /// <summary>
    /// An import never writes into the save folder, so a file from somewhere else reaches a live
    /// slot only by being loaded.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanImportSave))]
    private async Task ImportSaveAsync()
    {
        var library = _library;
        if (library is null || IsBusy)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Import a save into the library",
            Filter = "Rain World saves (*.rwsave, *.rwcampaign, sav, online_sav)"
                + "|*.rwsave;*.rwcampaign;sav;sav2;sav3;online_sav;online_sav2;online_sav3"
                + "|Rain World save bundle (*.rwsave)|*.rwsave"
                + "|Rain World campaign (*.rwcampaign)|*.rwcampaign"
                + "|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog(OwnerWindow) != true)
        {
            return;
        }

        var source = dialog.FileName;
        LibraryImportResult? result = null;
        Exception? failure = null;

        BeginBusy("Importing", source);
        try
        {
            result = await Task.Run(() => library.ImportFile(source));
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
            Report("The file could not be imported.", failure);
            return;
        }

        if (!result!.Success)
        {
            ShowMessage(
                "This file was not imported.\n\n" + FormatList(result.Errors),
                "Import a save",
                MessageBoxImage.Warning);
            return;
        }

        ShowLibrarySave(result.Entry!.Id);

        if (result.Warnings.Count > 0)
        {
            ShowMessage(
                "Imported \"" + result.Entry.Name + "\".\n\nNotes:\n" + FormatList(result.Warnings),
                "Import a save",
                MessageBoxImage.Information);
        }
    }

    private bool CanImportSave() => !IsBusy && _library is not null;

    /// <summary>
    /// The headline is built by Core, so a load that wrote to the save folder is never worded like
    /// one that refused to start.
    /// </summary>
    private void ReportLoadResult(LibraryLoadResult result)
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

            ShowMessage(text.ToString(), "Load a library save", MessageBoxImage.Information);
            return;
        }

        text.Append(FormatList(result.Errors));

        if (result.Warnings.Count > 0)
        {
            text.Append("\n\nNotes:\n").Append(FormatList(result.Warnings));
        }

        ShowMessage(text.ToString(), "Load a library save", MessageBoxImage.Error);
    }

    private void ShowLibrarySave(string id)
    {
        IsLibraryTabSelected = true;

        var match = LibraryEntries.FirstOrDefault(
            item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            SelectedLibraryEntry = match;
        }
    }

    /// <summary>Online slots only with Rain Meadow, because without it nothing writes those files.</summary>
    private static IReadOnlyList<SlotSide> ReadStorableSides(SlotCopyService copies, bool includeOnline)
    {
        var sides = new List<SlotSide>(SaveSlotRef.MaxSlot * 2);
        sides.AddRange(copies.ReadSlots(SaveRealm.Local));

        if (includeOnline)
        {
            sides.AddRange(copies.ReadSlots(SaveRealm.Online));
        }

        return sides;
    }

    private static SaveSlotRef FirstSlotWithASave(IReadOnlyList<SlotSide> sides)
    {
        foreach (var side in sides)
        {
            if (side.Exists)
            {
                return new SaveSlotRef(side.Realm, side.Slot);
            }
        }

        return new SaveSlotRef(sides[0].Realm, sides[0].Slot);
    }

    /// <summary>
    /// The one place a user's name touches a path, and the user still picks the final one in the
    /// dialog.
    /// </summary>
    private static string SafeFileName(string name)
    {
        var cleaned = new StringBuilder(name.Length);

        foreach (var character in name.Trim())
        {
            cleaned.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), character) >= 0 ? '_' : character);
        }

        var result = cleaned.ToString().Trim('.', ' ');
        return result.Length == 0 ? "library save" : result;
    }

    /// <summary>
    /// The headline is built by Core, so a copy that wrote to the save folder is never worded like
    /// one that refused to start.
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

        // A restore puts back every slot at once, so no library save is in the slot it was in.
        if (result?.LiveFolderModified == true)
        {
            await ReleaseAllSlotClaimsAsync();
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

    /// <summary>
    /// Re-hashes each row against its own manifest, one at a time off the UI thread, so a long list
    /// fills in rather than blocking. A restore and a load both check for themselves anyway.
    /// </summary>
    private async Task VerifyAllAsync(CancellationToken token)
    {
        var service = _backupService;
        if (service is null)
        {
            return;
        }

        // Copied first: a refresh rebuilds the collections on the UI thread, and iterating a live
        // one across an await would throw when that happens.
        var pending = Backups.Where(item => item.CanRestore && item.VerifiedOk is null).ToList();

        foreach (BackupItemViewModel item in pending)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            bool ok;
            try
            {
                ok = await Task.Run(() => service.Verify(item.Snapshot).Ok, token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                continue;
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            item.VerifiedOk = ok;
        }

        var library = _library;
        if (library is null)
        {
            return;
        }

        var pendingEntries = LibraryEntries.Where(item => item.IsComplete && item.VerifiedOk is null).ToList();

        foreach (LibraryEntryViewModel item in pendingEntries)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            bool ok;
            try
            {
                ok = await Task.Run(() => library.VerifyEntry(item.Entry).Ok, token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                continue;
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            item.VerifiedOk = ok;
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseSelection))]
    private void OpenFolder()
    {
        var folder = SelectedLibraryEntry?.Entry.DirectoryPath ?? SelectedBackup?.Snapshot.DirectoryPath;
        if (folder is null)
        {
            return;
        }

        try
        {
            var info = new ProcessStartInfo("explorer.exe", "\"" + folder + "\"")
            {
                UseShellExecute = true,
            };
            Process.Start(info);
        }
        catch (Exception ex)
        {
            Report("The folder could not be opened.", ex);
        }
    }

    /// <summary>Deletes whichever of the two lists has the selection.</summary>
    [RelayCommand(CanExecute = nameof(CanUseSelection))]
    private async Task DeleteAsync()
    {
        if (SelectedLibraryEntry is not null)
        {
            await DeleteLibraryEntryAsync();
            return;
        }

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

    private async Task DeleteLibraryEntryAsync()
    {
        var library = _library;
        var item = SelectedLibraryEntry;
        if (library is null || item is null)
        {
            return;
        }

        var confirm = AskYesNo(
            "Delete this library save for good?\n\n" + item.Name + "\n" + item.CampaignCountText + "   " + item.SizeText +
            "\nFolder: " + item.Id +
            "\n\nThe live save slots are not changed.",
            "Delete library save");
        if (!confirm)
        {
            return;
        }

        Exception? failure = null;

        BeginBusy("Deleting library save", item.Name);
        try
        {
            await Task.Run(() => library.DeleteEntry(item.Entry));
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
            Report("The library save could not be deleted.", failure);
        }
    }

    private bool CanUseSelection() =>
        !IsBusy && _backupService is not null && (SelectedBackup is not null || SelectedLibraryEntry is not null);

    [RelayCommand(CanExecute = nameof(CanOpenSettings))]
    private async Task OpenSettingsAsync() => await ShowSettingsAsync(null);

    private bool CanOpenSettings() => !IsBusy;

    /// <summary>
    /// Shown rather than shown as a dialog, so a download carries on while the user goes back to
    /// the main window. It carries the same UpdateViewModel the banner does.
    /// </summary>
    [RelayCommand]
    private void OpenUpdates()
    {
        if (Updates is not { } updates)
        {
            return;
        }

        if (_updatesWindow is { } already)
        {
            already.Activate();
            return;
        }

        var window = new UpdatesDialog(new UpdatesViewModel(updates));
        _updatesWindow = window;
        window.Closed += (_, _) => _updatesWindow = null;

        if (OwnerWindow is { } owner && !ReferenceEquals(owner, window))
        {
            window.Owner = owner;
        }

        window.Show();
    }

    /// <summary>
    /// Held so a second press brings the window forward. Two would each hold their own cached list
    /// and their own armed downgrade.
    /// </summary>
    private UpdatesDialog? _updatesWindow;

    // Shown rather than shown as a dialog: installing a missing mod means leaving for Steam and
    // coming back to press Refresh.
    [RelayCommand(CanExecute = nameof(CanOpenModSync))]
    private void OpenModSync()
    {
        if (_modSync is not { } service)
        {
            return;
        }

        if (_modSyncWindow is { } already)
        {
            already.Activate();
            MatchSelectedMods();
            return;
        }

        var window = new ModSyncDialog(new ModSyncViewModel(service));
        _modSyncWindow = window;
        window.Closed += (_, _) => _modSyncWindow = null;

        if (OwnerWindow is { } owner && !ReferenceEquals(owner, window))
        {
            window.Owner = owner;
        }

        window.Show();
        MatchSelectedMods();
    }

    private bool CanOpenModSync() => !IsBusy && _modSync is not null;

    private void MatchSelectedMods()
    {
        if (_modSyncWindow?.DataContext is not ModSyncViewModel view)
        {
            return;
        }

        if (IsLibraryTabSelected && SelectedLibraryEntry is { } entry)
        {
            view.Match(entry.Entry.Manifest?.Mods, entry.Name);
            return;
        }

        if (!IsLibraryTabSelected && SelectedBackup is { } backup)
        {
            view.Match(backup.Snapshot.Manifest?.Mods, backup.LabelText);
            return;
        }

        view.Match(null, null);
    }

    private ModSyncDialog? _modSyncWindow;

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

        if (string.IsNullOrWhiteSpace(_settings.LibraryRootPath))
        {
            _settings.LibraryRootPath = AppSettings.DefaultLibraryRootPath;
        }
    }

    /// <summary>
    /// A null service means the buttons stay disabled. The folder probe runs on a worker:
    /// Directory.Exists on a share whose machine is off blocks for as long as SMB takes to give up.
    /// </summary>
    private async Task ApplySettingsAsync()
    {
        var savePath = _settings.GameSavePath ?? "";
        var backupRoot = _settings.BackupRootPath ?? "";
        var libraryRoot = _settings.LibraryRootPath ?? "";
        SavePathText = savePath.Length == 0 ? "not set" : savePath;
        BackupRootText = backupRoot.Length == 0 ? "not set" : backupRoot;
        LibraryRootText = libraryRoot.Length == 0 ? "not set" : libraryRoot;

        var ownsBusy = !IsBusy;
        if (ownsBusy)
        {
            BeginBusy("Checking the folders", savePath.Length == 0 ? "Reading the settings" : savePath);
        }

        try
        {
            await ApplySettingsCoreAsync(savePath, backupRoot, libraryRoot);
        }
        finally
        {
            if (ownsBusy)
            {
                EndBusy();
            }
        }
    }

    private async Task ApplySettingsCoreAsync(string savePath, string backupRoot, string libraryRoot)
    {
        // Checked by ModSyncService before it writes into it. Probing touches disk, so it goes on
        // the worker with the rest.
        var installPath = _settings.GameInstallPath;
        await Task.Run(() => _icons.UseInstall(installPath));

        var problem = await Task.Run(() => SettingsValidation.Validate(savePath, backupRoot, libraryRoot));
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
                    // A closure rather than a value, because the mod list has to be current at the
                    // moment a snapshot is taken, not at the moment the service was built.
                    return (new BackupService(
                        savePath,
                        backupRoot,
                        _gameDetector,
                        _appVersion,
                        () => CurrentModsReader.Read(savePath, _settings.GameInstallPath)), null);
                }
                catch (Exception ex)
                {
                    return (null, ex.Message);
                }
            });

            _backupService = built.Service;
            ConfigProblem = built.Error is null ? "" : "The backup service could not be started: " + built.Error;
        }

        _copyService = _backupService?.SlotCopies;

        _modSync = null;
        if (_backupService is { } forMods)
        {
            _modSync = await Task.Run<ModSyncService?>(() =>
            {
                try
                {
                    return new ModSyncService(savePath, installPath, _gameDetector, backups: forMods);
                }
                catch (Exception)
                {
                    return null;
                }
            });
        }

        // The library borrows the backup service's safety snapshot for its loads: no backup
        // service, no library.
        _library = null;
        if (_backupService is { } backups)
        {
            var built = await Task.Run<(SaveLibrary? Library, string? Error)>(() =>
            {
                try
                {
                    return (new SaveLibrary(backups, libraryRoot, _gameDetector, _appVersion), null);
                }
                catch (Exception ex)
                {
                    return (null, ex.Message);
                }
            });

            _library = built.Library;

            // Reported without taking the backup service down with it: a library path that will not
            // work costs the library tab alone.
            if (built.Error is not null && ConfigProblem.Length == 0)
            {
                ConfigProblem = "The library folder could not be used: " + built.Error;
            }
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
        var library = _library;
        var keepId = SelectedBackup?.Id;
        var keepEntryId = SelectedLibraryEntry?.Id;
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
            LibraryEntries.Clear();
            SelectedBackup = null;
            SelectedLibraryEntry = null;
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
            // Everything that touches disk happens here. What comes back is enough to build every
            // view model on the dispatcher without reading again.
            var data = await Task.Run(() =>
            {
                var slots = service.ReadLiveSlots();
                var snapshots = service.ListBackups();
                var entries = library?.ListEntries() ?? Array.Empty<LibraryEntry>();
                var measured = MeasureLiveFiles(service.SaveRoot, slots);
                _icons.Preload(CollectSlugcatIds(slots, snapshots, entries));

                // One small json per folder, read here so that selecting a row costs nothing.
                var liveMeadow = ReadMeadow(service.SaveRoot);
                var backupMeadow = new Dictionary<string, MeadowProfile>(StringComparer.OrdinalIgnoreCase);
                foreach (var snapshot in snapshots)
                {
                    if (ReadMeadow(snapshot.DirectoryPath) is { } profile)
                    {
                        backupMeadow[snapshot.Id] = profile;
                    }
                }

                var meadow = RainMeadowDetector.Detect(service.SaveRoot, _settings.GameInstallPath);

                // The "now" side of every mod comparison the app makes.
                var mods = CurrentModsReader.Read(service.SaveRoot, _settings.GameInstallPath);

                return (Slots: slots, Snapshots: snapshots, Entries: entries, measured.Size, measured.Count, LiveMeadow: liveMeadow, BackupMeadow: backupMeadow, Meadow: meadow, Mods: mods);
            });

            _meadow = data.Meadow;
            _currentMods = data.Mods;
            _liveSlotData = data.Slots;
            _liveSizeBytes = data.Size;
            _liveFileCount = data.Count;
            _liveMeadow = data.LiveMeadow;
            _backupMeadow = data.BackupMeadow;

            // Local slots only. The online saves share these slot numbers, so listing both would
            // show slot 2 twice with no way to tell which is which.
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

            LibraryEntries.Clear();
            foreach (var entry in data.Entries)
            {
                LibraryEntries.Add(new LibraryEntryViewModel(entry, _icons, service.SaveRoot));
            }

            RestoreSelection(keepId, keepEntryId, keepLive);
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
            return;
        }

        StartVerifySweep();
    }

    /// <summary>
    /// Any sweep still running from an earlier refresh is cancelled first, because its rows have
    /// already been replaced.
    /// </summary>
    private void StartVerifySweep()
    {
        _verifySweep?.Cancel();
        _verifySweep?.Dispose();

        var sweep = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        _verifySweep = sweep;

        // Deliberately not awaited. The list is usable while this fills the state column in.
        _ = VerifyAllAsync(sweep.Token);
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
    /// Whatever was selected wins if it is still there, then the newest backup, then the live save
    /// card, so the panel is never blank.
    /// </summary>
    private void RestoreSelection(string? keepId, string? keepEntryId, bool keepLive)
    {
        LibraryEntryViewModel? entry = null;
        BackupItemViewModel? backup = null;

        if (!keepLive)
        {
            entry = FindEntryById(keepEntryId);
            if (entry is null)
            {
                backup = FindById(keepId) ?? (keepEntryId is null ? Backups.FirstOrDefault() : null);
            }
        }

        _movingSelection = true;
        try
        {
            IsLiveSelected = entry is null && backup is null;
            SelectedBackup = backup;
            SelectedLibraryEntry = entry;
        }
        finally
        {
            _movingSelection = false;
        }

        RebuildDetail();
    }

    private LibraryEntryViewModel? FindEntryById(string? id) =>
        id is null
            ? null
            : LibraryEntries.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>A file that cannot be measured is left out rather than reported.</summary>
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
            }
        }

        return (size, count);
    }

    /// <summary>Null when the folder holds no meadow.json, which is the ordinary case.</summary>
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
    /// Every slugcat id on screen after this refresh, so the portraits are decoded on the worker
    /// rather than one file at a time while the list is built.
    /// </summary>
    private static IEnumerable<string> CollectSlugcatIds(
        IReadOnlyList<SlotMetadata> liveSlots,
        IReadOnlyList<BackupSnapshot> snapshots,
        IReadOnlyList<LibraryEntry> entries)
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

        foreach (var entry in entries)
        {
            if (entry.Manifest?.Metadata is not { } metadata)
            {
                continue;
            }

            foreach (var campaign in metadata.Campaigns)
            {
                yield return campaign.SlugcatId;
            }
        }
    }

    // async void, so nothing can observe a failure here. PollGameAsync is written not to throw.
    private async void OnGameTimerTick(object? sender, EventArgs e) => await PollGameAsync();

    /// <summary>
    /// The await does not capture the dispatcher. A poll still inside the process enumeration when
    /// the window closes would otherwise resume by posting to a dispatcher that has shut down,
    /// which throws on the thread pool where App.DispatcherUnhandledException cannot reach it.
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

    /// <summary>Called from a worker. Drops the result when no dispatcher is left to write to.</summary>
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
            // The dispatcher finished shutting down between the check and the post.
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
        OpenFolderCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        OpenSettingsCommand.NotifyCanExecuteChanged();
        CopySlotCommand.NotifyCanExecuteChanged();
        StoreSlotCommand.NotifyCanExecuteChanged();
        LoadSaveCommand.NotifyCanExecuteChanged();
        UpdateEntryCommand.NotifyCanExecuteChanged();
        UndoUpdateCommand.NotifyCanExecuteChanged();
        RenameEntryCommand.NotifyCanExecuteChanged();
        ImportSaveCommand.NotifyCanExecuteChanged();
        ExportSaveCommand.NotifyCanExecuteChanged();
        BeginEditCommand.NotifyCanExecuteChanged();
        SaveEditsCommand.NotifyCanExecuteChanged();
        StoreCampaignCommand.NotifyCanExecuteChanged();
        SendCampaignCommand.NotifyCanExecuteChanged();
        DeleteCampaignCommand.NotifyCanExecuteChanged();
        DeleteSlotCommand.NotifyCanExecuteChanged();
        OpenModSyncCommand.NotifyCanExecuteChanged();
    }

    private void RaiseListStates()
    {
        OnPropertyChanged(nameof(HasLiveSlots));
        OnPropertyChanged(nameof(HasNoLiveSlots));
        OnPropertyChanged(nameof(HasBackups));
        OnPropertyChanged(nameof(HasNoBackups));
        OnPropertyChanged(nameof(BackupCountText));
        OnPropertyChanged(nameof(HasLibraryEntries));
        OnPropertyChanged(nameof(HasNoLibraryEntries));
        OnPropertyChanged(nameof(LibraryCountText));
        OnPropertyChanged(nameof(LiveSummaryText));
        OnPropertyChanged(nameof(LiveOnlineText));
        OnPropertyChanged(nameof(LiveAccessibleName));

        // Whether there is anything to load is not a property any attribute can watch.
        LoadSaveCommand.NotifyCanExecuteChanged();
    }

    private void ReportRestoreResult(BackupItemViewModel item, RestoreResult result)
    {
        var safetyName = result.SafetySnapshot?.Id ?? "none was recorded";
        var text = new StringBuilder();

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
    /// An exception out of RestoreBackup carries no result, and the restore may already have
    /// overwritten the save folder, so the safety snapshot is found in the refreshed list instead.
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
    /// Every command failure lands here, so an IOException reads as a message instead of ending
    /// the app. A refusal this app worked out itself passes a null exception.
    /// </summary>
    private void Report(string headline, Exception? ex)
    {
        if (ex is GameRunningException running)
        {
            ReportGameRunning(running);
            return;
        }

        string text = ex is null ? headline : headline + "\n\n" + ex.Message;

        ShowMessage(text, AppInfo.DisplayName, MessageBoxImage.Error);
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
