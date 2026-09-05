using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.Core.Settings;
using RainWorldCompanion.Core.System;

namespace RainWorldCompanion.ViewModels;

/// <summary>
/// Both path boxes update their binding on every keystroke, so nothing here that touches disk runs
/// on the dispatcher. Every background check carries a version that a later keystroke moves past,
/// which drops the stale answer and stops the work producing it.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    /// <summary>A full check walks every mods\*\illustrations folder once per known slugcat.</summary>
    private const int InstallCheckDebounceMs = 350;

    private readonly SettingsStore _store;
    private readonly AppSettings _current;
    private int _saveRootCheckVersion;
    private int _installCheckVersion;
    private int _validationVersion;

    public SettingsViewModel(SettingsStore store, AppSettings current, string? reason = null)
    {
        _store = store;
        _current = current;
        gameSavePath = current.GameSavePath ?? "";
        backupRootPath = current.BackupRootPath ?? "";
        libraryRootPath = current.LibraryRootPath ?? "";
        gameInstallPath = current.GameInstallPath ?? "";
        isDarkTheme = AppThemes.Parse(current.Theme) == AppTheme.Dark;
        introMessage = reason ?? "";
        Revalidate();
        _ = CheckInstallAsync(gameInstallPath, debounce: false);
    }

    /// <summary>Settings that were written to disk, null until Save succeeds.</summary>
    public AppSettings? Result { get; private set; }

    public event Action<bool>? CloseRequested;

    /// <summary>Owner for the folder picker. Set by the dialog.</summary>
    public Window? Owner { get; set; }

    public string SettingsFilePath => _store.SettingsPath;

    [ObservableProperty]
    private string gameSavePath;

    [ObservableProperty]
    private string backupRootPath;

    [ObservableProperty]
    private string libraryRootPath;

    [ObservableProperty]
    private bool isDarkTheme;

    [ObservableProperty]
    private string gameInstallPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInstallStatus))]
    private string installStatus = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValidationMessage))]
    private string validationMessage = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSaveRootWarning))]
    private string saveRootWarning = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIntroMessage))]
    private string introMessage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool isValid;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(AutoDetectCommand))]
    [NotifyCanExecuteChangedFor(nameof(AutoDetectInstallCommand))]
    [NotifyCanExecuteChangedFor(nameof(BundleLogsCommand))]
    private bool isBusy;

    public bool HasValidationMessage => ValidationMessage.Length > 0;

    public bool HasSaveRootWarning => SaveRootWarning.Length > 0;

    public bool HasIntroMessage => IntroMessage.Length > 0;

    public bool HasInstallStatus => InstallStatus.Length > 0;

    partial void OnGameSavePathChanged(string value) => Revalidate();

    partial void OnBackupRootPathChanged(string value) => Revalidate();

    partial void OnLibraryRootPathChanged(string value) => Revalidate();

    partial void OnGameInstallPathChanged(string value) => _ = CheckInstallAsync(value, debounce: true);

    [RelayCommand]
    private async Task BrowseSavePathAsync()
    {
        var picked = await PickFolderAsync("Select the Rain World save folder", GameSavePath);
        if (picked is not null)
        {
            GameSavePath = picked;
        }
    }

    [RelayCommand]
    private async Task BrowseBackupRootAsync()
    {
        var picked = await PickFolderAsync("Select the folder that will hold backups", BackupRootPath);
        if (picked is not null)
        {
            BackupRootPath = picked;
        }
    }

    [RelayCommand]
    private async Task BrowseLibraryRootAsync()
    {
        var picked = await PickFolderAsync("Select the folder that will hold the save library", LibraryRootPath);
        if (picked is not null)
        {
            LibraryRootPath = picked;
        }
    }

    [RelayCommand]
    private void UseDefaultLibraryRoot() => LibraryRootPath = AppSettings.DefaultLibraryRootPath;

    [RelayCommand(CanExecute = nameof(CanAutoDetect))]
    private async Task AutoDetectAsync()
    {
        IsBusy = true;
        string? found;
        try
        {
            found = await Task.Run<string?>(() =>
            {
                try
                {
                    return SavePathResolver.FindSavePath();
                }
                catch (Exception)
                {
                    return null;
                }
            });
        }
        finally
        {
            IsBusy = false;
        }

        if (found is null)
        {
            Show(
                "No Rain World save folder was found at the usual location:\n\n" + SavePathResolver.DefaultSavePath +
                "\n\nPick the folder yourself with Browse.",
                "Auto-detect",
                MessageBoxImage.Information);
            return;
        }

        GameSavePath = found;
    }

    private bool CanAutoDetect() => !IsBusy;

    [RelayCommand]
    private async Task BrowseInstallPathAsync()
    {
        var picked = await PickFolderAsync("Select the Rain World install folder", GameInstallPath);
        if (picked is not null)
        {
            GameInstallPath = picked;
        }
    }

    [RelayCommand(CanExecute = nameof(CanAutoDetect))]
    private async Task AutoDetectInstallAsync()
    {
        IsBusy = true;
        string? found;
        try
        {
            found = await Task.Run<string?>(() =>
            {
                try
                {
                    return GameInstallLocator.FindInstallPath();
                }
                catch (Exception)
                {
                    return null;
                }
            });
        }
        finally
        {
            IsBusy = false;
        }

        if (found is null)
        {
            Show(
                "No Rain World install was found in the usual Steam locations.\n\n" +
                "Pick the folder yourself with Browse, or leave this blank. Without it the app " +
                "draws its own slugcat icons and nothing else changes.",
                "Auto-detect",
                MessageBoxImage.Information);
            return;
        }

        GameInstallPath = found;
    }

    [RelayCommand(CanExecute = nameof(CanBundleLogs))]
    private async Task BundleLogsAsync()
    {
        var installPath = GameInstallPath.Trim();
        if (installPath.Length == 0)
        {
            Show(
                "Set the Rain World install folder above, then try again.",
                "Rain World logs",
                MessageBoxImage.Information);
            return;
        }

        RainWorldLogBundleResult? result = null;
        Exception? failure = null;

        IsBusy = true;
        try
        {
            result = await Task.Run(() => RainWorldLogBundle.Create(
                installPath,
                DownloadsFolder.GetPath()));
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            IsBusy = false;
        }

        if (failure is not null)
        {
            Show(
                "The Rain World logs could not be bundled.\n\n" + failure.Message,
                "Rain World logs",
                MessageBoxImage.Error);
            return;
        }

        Show(
            "Bundled " + string.Join(" and ", result!.IncludedFileNames) + " into:\n\n"
            + result.ArchivePath,
            "Rain World logs",
            MessageBoxImage.Information);
    }

    private bool CanBundleLogs() => !IsBusy;

    /// <param name="debounce">
    /// True when the path came from typing, which stops one sweep per character being run through.
    /// </param>
    private async Task CheckInstallAsync(string path, bool debounce)
    {
        var version = ++_installCheckVersion;
        var trimmed = path.Trim();

        if (debounce)
        {
            await Task.Delay(InstallCheckDebounceMs);

            if (version != _installCheckVersion)
            {
                return;
            }
        }

        var status = await Task.Run(() => DescribeInstall(trimmed, version));

        if (status is null || version != _installCheckVersion)
        {
            return;
        }

        InstallStatus = status;
    }

    /// <summary>
    /// Null when a later keystroke has moved <c>_installCheckVersion</c> past
    /// <paramref name="version"/>, so a stale sweep stops between slugcats.
    /// </summary>
    private string? DescribeInstall(string trimmed, int version)
    {
        if (trimmed.Length == 0)
        {
            return "No install set, so the app draws its own slugcat icons.";
        }

        if (!GameInstallLocator.LooksLikeInstall(trimmed))
        {
            return "That folder does not look like a Rain World install. The app will draw its own icons.";
        }

        var found = 0;
        foreach (var slugcat in SlugcatCatalog.Known)
        {
            if (version != Volatile.Read(ref _installCheckVersion))
            {
                return null;
            }

            if (GameInstallLocator.FindPortraitFile(trimmed, slugcat.Id) is not null)
            {
                found++;
            }
        }

        return found == 0
            ? "That install holds no portrait art, so the app draws its own icons."
            : "Portraits found for " + found + " of " + SlugcatCatalog.Known.Count + " slugcats.";
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        var installPath = GameInstallPath.Trim();

        // Built from what was loaded, so only the fields this dialog owns are overwritten. A
        // fresh object would reset the update channel, the auto-check choice and the last-check
        // stamp to their initialisers every time.
        var settings = _current.Clone();
        settings.GameSavePath = GameSavePath.Trim();
        settings.BackupRootPath = BackupRootPath.Trim();
        settings.LibraryRootPath = LibraryRootPath.Trim();
        settings.GameInstallPath = installPath.Length == 0 ? null : installPath;
        settings.Theme = (IsDarkTheme ? AppTheme.Dark : AppTheme.Light).ToStorageString();

        IsBusy = true;
        try
        {
            await Task.Run(() => _store.Save(settings));
        }
        catch (Exception ex)
        {
            Show("The settings could not be saved.\n\n" + ex.Message, "Settings", MessageBoxImage.Error);
            return;
        }
        finally
        {
            IsBusy = false;
        }

        Result = settings;
        CloseRequested?.Invoke(true);
    }

    private bool CanSave() => IsValid && !IsBusy;

    /// <summary>
    /// Only the text-only checks. The rest of validation opens each path with CreateFileW, and a
    /// half-typed UNC path such as \\fileserver\rw blocks that for the full DNS and SMB timeout.
    /// Save stays disabled until the background half answers.
    /// </summary>
    private void Revalidate()
    {
        var savePath = GameSavePath.Trim();
        var backupRoot = BackupRootPath.Trim();
        var libraryRoot = LibraryRootPath.Trim();

        var problem = SettingsValidation.ValidateText(savePath, backupRoot, libraryRoot);
        if (problem is not null)
        {
            // Moves the version on, so any full check already running is discarded.
            _validationVersion++;
            ValidationMessage = problem;
            IsValid = false;
            SaveRootWarning = "";
            return;
        }

        ValidationMessage = "";
        IsValid = false;
        _ = ValidateFullAsync(savePath, backupRoot, libraryRoot);
    }

    /// <summary>Off the dispatcher. A stale result is dropped when any path has moved on.</summary>
    private async Task ValidateFullAsync(string savePath, string backupRoot, string libraryRoot)
    {
        var version = ++_validationVersion;

        var problem = await Task.Run(() =>
        {
            try
            {
                return SettingsValidation.Validate(savePath, backupRoot, libraryRoot);
            }
            catch (Exception ex)
            {
                return "Those folders could not be checked: " + ex.Message;
            }
        });

        if (version != _validationVersion)
        {
            return;
        }

        ValidationMessage = problem ?? "";
        IsValid = problem is null;

        if (problem is not null)
        {
            SaveRootWarning = "";
            return;
        }

        _ = CheckSaveRootAsync(savePath);
    }

    /// <summary>Off the UI thread. A stale result is dropped when the path has moved on.</summary>
    private async Task CheckSaveRootAsync(string path)
    {
        var version = ++_saveRootCheckVersion;

        var warning = await Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return "That folder does not exist yet. It will be created when it is first needed.";
                }

                if (!SavePathResolver.LooksLikeSaveRoot(path))
                {
                    return "No Rain World save files were found in that folder. Check the path before making a backup.";
                }

                return "";
            }
            catch (Exception ex)
            {
                return "That folder could not be read: " + ex.Message;
            }
        });

        if (version != _saveRootCheckVersion)
        {
            return;
        }

        SaveRootWarning = warning;
    }

    private async Task<string?> PickFolderAsync(string title, string startingPath)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false,
        };

        // Off the dispatcher: a configured path on a share whose machine is off makes
        // Directory.Exists block for as long as SMB takes to give up.
        var startingPathExists = await Task.Run(() =>
        {
            try
            {
                return !string.IsNullOrWhiteSpace(startingPath) && Directory.Exists(startingPath);
            }
            catch (Exception)
            {
                return false;
            }
        });

        if (startingPathExists)
        {
            dialog.InitialDirectory = startingPath;
        }

        var accepted = Owner is null ? dialog.ShowDialog() : dialog.ShowDialog(Owner);
        return accepted == true ? dialog.FolderName : null;
    }

    private void Show(string message, string title, MessageBoxImage icon)
    {
        if (Owner is not null && Owner.IsLoaded)
        {
            MessageBox.Show(Owner, message, title, MessageBoxButton.OK, icon);
        }
        else
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, icon);
        }
    }
}
