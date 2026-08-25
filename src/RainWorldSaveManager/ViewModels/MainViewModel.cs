using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using RainWorldSaveManager.Core.Backups;
using RainWorldSaveManager.Core.Library;
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

    /// <summary>Built beside the backup service too, for the same reason: a load takes a snapshot.</summary>
    private SaveLibrary? _library;

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

    // Whether Rain Meadow is on this machine. The whole online block hangs on it, so a player
    // without the mod never sees a section about it. Re-checked whenever the paths change.
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

    /// <summary>The named saves, newest first. The other half of the list column.</summary>
    public ObservableCollection<LibraryEntryViewModel> LibraryEntries { get; } = new();

    // The banner is not enough on its own. Without these, Restore stays enabled while the game
    // is open and the user is walked all the way through the destructive confirmation before
    // Core refuses the job.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NewBackupCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopySlotCommand))]
    [NotifyCanExecuteChangedFor(nameof(StoreSlotCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadSaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpdateEntryCommand))]
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

    /// <summary>The library row that is selected, or null. The third of the three selections.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpdateEntryCommand))]
    [NotifyCanExecuteChangedFor(nameof(UndoUpdateCommand))]
    [NotifyCanExecuteChangedFor(nameof(RenameEntryCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportSaveCommand))]
    private LibraryEntryViewModel? selectedLibraryEntry;

    /// <summary>
    /// Which of the two lists the column is showing. Only a view state: switching tabs moves no
    /// selection, so a backup stays selected while the library is on screen and the detail panel
    /// keeps showing it.
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
        // window that has gone. The verify sweep is linked to the same token.
        _shutdown.Cancel();

        _verifySweep?.Cancel();
        _verifySweep?.Dispose();
        _verifySweep = null;
    }

    /// <summary>Shows the save folder as it is on disk, so a backup can be read against it.</summary>
    [RelayCommand]
    private void SelectLive() => IsLiveSelected = true;

    // The three selections are one selection wearing three hats: the live card, a backup row and a
    // library row. Picking any of them clears the other two, and _movingSelection is what keeps the
    // clearing from rebuilding the detail panel once per property on the way through.
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
        // Stamped before the assignment, not after. MeadowInstalled raises no change notification,
        // and ShowMeadowSection is computed from it, so a binding that read it when Detail changed
        // would see the default and never look again, which left the whole block hidden.
        SnapshotDetailViewModel? built = BuildDetail();
        if (built is not null)
        {
            built.MeadowInstalled = _meadow.Present;
            built.MeadowVersionText = MeadowVersionText;
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
                _icons);
        }

        if (SelectedLibraryEntry is { } entry)
        {
            return SnapshotDetailViewModel.ForLibraryEntry(entry, _icons);
        }

        return SelectedBackup is { } item
            ? SnapshotDetailViewModel.ForBackup(item, FindMeadow(item.Id), _icons)
            : null;
    }

    /// <summary>
    /// "Rain Meadow 0.1.15.1" when the version was read, otherwise just the name. Shown on the
    /// section band so it is obvious which mod the block belongs to.
    /// </summary>
    private string MeadowVersionText =>
        string.IsNullOrWhiteSpace(_meadow.Version) ? "" : "v" + _meadow.Version;

    private MeadowProfile? FindMeadow(string id) =>
        _backupMeadow.TryGetValue(id, out var profile) ? profile : null;

    /// <summary>
    /// Copies one whole slot file onto another. Both ends are picked in the dialog, so this is the
    /// only entry point and the slot rows in the panel carry no buttons.
    ///
    /// Core does the work: this asks it for a plan of the pair the dialog opens on, shows that plan,
    /// and runs the copy on a worker with the busy overlay up, the same shape as Restore.
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

        // A plan that cannot run still opens the dialog. The pair here is only where the pickers
        // start, and refusing to open would leave the user no way to reach a pair that does work.
        var dialog = new CopySlotDialog(plan!, copies.PlanCopy, _meadow.Present);
        if (ShowDialog(dialog) != true)
        {
            return;
        }

        // The pickers let the user change either side, so the pair that runs is the one the dialog
        // closed on rather than the one it opened with.
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
    /// Where the pickers start. Slot 1 to its online half is the copy Rain Meadow players come for,
    /// and without the mod there is no online half to offer, so it starts on the two local slots the
    /// game itself shows first.
    /// </summary>
    private (SaveSlotRef From, SaveSlotRef To) DefaultCopyPair() =>
        _meadow.Present
            ? (new SaveSlotRef(SaveRealm.Local, 1), new SaveSlotRef(SaveRealm.Online, 1))
            : (new SaveSlotRef(SaveRealm.Local, 1), new SaveSlotRef(SaveRealm.Local, 2));

    /// <summary>
    /// Keeps a copy of one live slot in the library under a name. Nothing in the save folder is
    /// written, so there is no safety snapshot and no confirmation beyond the dialog itself.
    /// </summary>
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

    /// <summary>
    /// Writes a library save over a live slot. Both ends are picked in the dialog, and the load runs
    /// the same ladder a slot copy runs, so the slot it replaces is in a safety snapshot first.
    /// </summary>
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
            plan = await Task.Run(() => library.PlanLoad(entry, target));
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
        var dialog = new LoadSaveDialog(entries, plan!, library.PlanLoad, _meadow.Present);
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
            result = await Task.Run(() => library.LoadEntry(chosen, chosenTarget, progress, CancellationToken.None));
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

    /// <summary>
    /// Writes what is in a slot now back over the library save it came from, which is how an hour of
    /// play gets back into the entry. The bytes being replaced are kept, so this can be undone.
    /// </summary>
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

        // The slot this save was last loaded into is the one the user played, so that is what an
        // update means. Without a load on record there is nothing to guess from and the whole
        // dialog is a better place to ask.
        var source = item.Entry.Manifest?.LastLoadedSlotRef;
        if (source is null)
        {
            ShowMessage(
                "This save has not been loaded into a slot yet, so there is nothing to update it from.\n\n" +
                "Load it into a slot first, or use Store Slot to keep a slot as a new library save.",
                "Update a library save",
                MessageBoxImage.Warning);
            return;
        }

        var side = await Task.Run(() => copies.ReadSide(source));

        if (!side.Exists)
        {
            ShowMessage(
                source.FileName + " is not in the save folder, so there is nothing to update from.",
                "Update a library save",
                MessageBoxImage.Warning);
            return;
        }

        var confirm = AskYesNo(
            "Replace \"" + item.Name + "\" with what is in " + source.FileName + " now?\n\n" +
            source.FileName + ": " + side.Describe() + "\n" +
            "Stored now: " + item.CampaignCountText + ", " + item.SizeText + "\n\n" +
            "The save being replaced is kept, so this can be undone.",
            "Update a library save");
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

    /// <summary>Puts back the save the last update replaced.</summary>
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

    /// <summary>Changes the name and the note. The stored save is not touched.</summary>
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

    /// <summary>Writes the selected save out as a single .rwsave file.</summary>
    [RelayCommand(CanExecute = nameof(CanExportSave))]
    private async Task ExportSaveAsync()
    {
        var library = _library;
        var item = SelectedLibraryEntry;
        if (library is null || item is null || IsBusy)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export a library save",
            Filter = "Rain World save bundle (*.rwsave)|*.rwsave|All files (*.*)|*.*",
            DefaultExt = ".rwsave",
            AddExtension = true,
            FileName = SafeFileName(item.Name) + ".rwsave",
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
            "\n\nThe file holds the save and the name, the note and the campaigns beside it.",
            "Export a library save",
            MessageBoxImage.Information);
    }

    private bool CanExportSave() =>
        !IsBusy && _library is not null && SelectedLibraryEntry is { IsComplete: true };

    /// <summary>
    /// Reads a .rwsave bundle, or a bare save file, into a new library save. An import never writes
    /// into the save folder, so a file from somewhere else reaches a live slot only by being loaded.
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
            Filter = "Rain World saves (*.rwsave, sav, online_sav)|*.rwsave;sav;sav2;sav3;online_sav;online_sav2;online_sav3"
                + "|Rain World save bundle (*.rwsave)|*.rwsave"
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
    /// Reports a finished load. The headline is built by Core, so a load that wrote to the save
    /// folder can never be reported with the same wording as one that refused to start.
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

    /// <summary>Shows the library tab with one save selected, after storing or importing it.</summary>
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

    /// <summary>
    /// The slots the store dialog offers. Online slots only when Rain Meadow is on the machine,
    /// because without it those files are written by nothing.
    /// </summary>
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
    /// Turns a user's name into something a file dialog can start on. This is the one place a name
    /// touches a path, and the user still picks the final one in the dialog.
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

    /// <summary>
    /// Re-hashes every listed snapshot and library save against its own manifest, in the background,
    /// so the state column is answered without the user having to ask. It runs one at a time off the
    /// UI thread and updates each row as it finishes, so a long list fills in rather than blocking.
    ///
    /// A restore and a load both check for themselves immediately beforehand. This is about telling
    /// the user which of their saves is sound before they need one, not about gating anything.
    /// </summary>
    private async Task VerifyAllAsync(CancellationToken token)
    {
        var service = _backupService;
        if (service is null)
        {
            return;
        }

        // Copied first: the collections are rebuilt on the UI thread by a refresh, and iterating a
        // live one across an await would throw when that happens.
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
                // A snapshot that cannot be read is already reported as incomplete by the listing.
                // Failing to verify it is not worth a dialog the user did not ask for.
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

    /// <summary>
    /// Deletes whichever of the two lists has the selection. The wording says which kind of thing is
    /// going, because a backup and a library save are worth very different amounts to a user.
    /// </summary>
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
        // The install path only feeds the portraits, so it is never validated and never blocks
        // anything here. Probing it still touches disk, so it goes on the worker with the rest.
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

        // The library borrows the backup service's safety snapshot for its loads, so it goes the
        // same way: no backup service, no library.
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

            // Reported without taking the backup service down with it. A library path that will not
            // work costs the library tab, and backing up and restoring still matter more.
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
            // Reading the saves, listing the snapshots, measuring the live files and decoding the
            // portraits all happen here, on the worker. What comes back is enough to build every
            // view model on the dispatcher without touching disk again.
            var data = await Task.Run(() =>
            {
                var slots = service.ReadLiveSlots();
                var snapshots = service.ListBackups();
                var entries = library?.ListEntries() ?? Array.Empty<LibraryEntry>();
                var measured = MeasureLiveFiles(service.SaveRoot, slots);
                _icons.Preload(CollectSlugcatIds(slots, snapshots, entries));

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

                // Reads the game's enabled mod list and probes the save folder, so it belongs on
                // the worker with the rest of the disk work rather than on the dispatcher.
                var meadow = RainMeadowDetector.Detect(service.SaveRoot, _settings.GameInstallPath);

                return (Slots: slots, Snapshots: snapshots, Entries: entries, measured.Size, measured.Count, LiveMeadow: liveMeadow, BackupMeadow: backupMeadow, Meadow: meadow);
            });

            _meadow = data.Meadow;
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
    /// Starts re-hashing the listed snapshots in the background. Any sweep still running from an
    /// earlier refresh is cancelled first, because its rows have already been replaced.
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
    /// Puts the selection back where it was after a refresh. Whatever was selected wins if it is
    /// still there; otherwise the newest backup; and with nothing at all the live save card takes
    /// the selection so the panel is never blank.
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

        // The library list decides whether there is anything to load, and that answer is not a
        // property any attribute can watch.
        LoadSaveCommand.NotifyCanExecuteChanged();
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
