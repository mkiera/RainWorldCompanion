using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using RainWorldSaveManager.Core.Settings;
using RainWorldSaveManager.Core.System;

namespace RainWorldSaveManager.ViewModels;

/// <summary>
/// Backing view model for the settings dialog. Validation runs on every keystroke.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _store;
    private readonly AppSettings _current;
    private int _saveRootCheckVersion;

    public SettingsViewModel(SettingsStore store, AppSettings current, string? reason = null)
    {
        _store = store;
        _current = current;
        gameSavePath = current.GameSavePath ?? "";
        backupRootPath = current.BackupRootPath ?? "";
        introMessage = reason ?? "";
        Revalidate();
    }

    /// <summary>Settings that were written to disk, null until Save succeeds.</summary>
    public AppSettings? Result { get; private set; }

    /// <summary>Raised with the dialog result the view should close with.</summary>
    public event Action<bool>? CloseRequested;

    /// <summary>Owner for the folder picker. Set by the dialog.</summary>
    public Window? Owner { get; set; }

    public string SettingsFilePath => _store.SettingsPath;

    [ObservableProperty]
    private string gameSavePath;

    [ObservableProperty]
    private string backupRootPath;

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
    private bool isBusy;

    public bool HasValidationMessage => ValidationMessage.Length > 0;

    public bool HasSaveRootWarning => SaveRootWarning.Length > 0;

    public bool HasIntroMessage => IntroMessage.Length > 0;

    partial void OnGameSavePathChanged(string value) => Revalidate();

    partial void OnBackupRootPathChanged(string value) => Revalidate();

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

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        var settings = new AppSettings
        {
            SchemaVersion = _current.SchemaVersion,
            GameSavePath = GameSavePath.Trim(),
            BackupRootPath = BackupRootPath.Trim(),
        };

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

    private void Revalidate()
    {
        var savePath = GameSavePath.Trim();
        var problem = SettingsValidation.Validate(savePath, BackupRootPath.Trim());
        ValidationMessage = problem ?? "";
        IsValid = problem is null;

        if (problem is not null)
        {
            SaveRootWarning = "";
            return;
        }

        _ = CheckSaveRootAsync(savePath);
    }

    /// <summary>
    /// Folder probing happens off the UI thread. A stale result is dropped when the path has moved on.
    /// </summary>
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

        // The probe runs off the dispatcher. A configured path on a share whose machine is off
        // makes Directory.Exists block for as long as SMB takes to give up, and the window would
        // sit there unpainted for all of it.
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
