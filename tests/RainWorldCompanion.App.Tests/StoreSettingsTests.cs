using System.IO;
using System.Text.Json;
using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Settings;
using RainWorldCompanion.Core.System;
using RainWorldCompanion.Services;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.App.Tests;

/// <summary>
/// Saving to the library, exporting a campaign and saving a whole slot all read the mod settings
/// from the snapshot the bytes came out of, whether that is a backup or a Recent save.
/// </summary>
public class StoreSettingsTests
{
    [Fact]
    public void A_backup_supplies_its_own_folder_for_the_settings()
    {
        using var root = new TempDirectory("store-settings");
        MainViewModel view = NewView(root);
        BackupSnapshot snapshot = AddSnapshot(root, "backup");

        view.SelectedBackup = new BackupItemViewModel(snapshot, new FakeIcons());

        Assert.Equal(snapshot.DirectoryPath, view.ConfigsRootOfSelection());
    }

    [Fact]
    public void A_recent_save_supplies_its_own_folder_for_the_settings()
    {
        using var root = new TempDirectory("store-settings");
        MainViewModel view = NewView(root);
        BackupSnapshot snapshot = AddSnapshot(root, "recent");

        view.SelectedLiveHistory = new LiveHistoryItemViewModel(
            new LiveHistoryEntry(snapshot, DateTimeOffset.UtcNow, []), new FakeIcons());

        Assert.Equal(snapshot.DirectoryPath, view.ConfigsRootOfSelection());
    }

    [Fact]
    public void The_live_save_supplies_none()
    {
        using var root = new TempDirectory("store-settings");

        Assert.Null(NewView(root).ConfigsRootOfSelection());
    }

    private static MainViewModel NewView(TempDirectory root) => new(
        new SettingsStore(Path.Combine(root.Path, "settings.json")),
        new NullGameProcessDetector(),
        new SlugcatIconProvider(),
        "test");

    private static BackupSnapshot AddSnapshot(TempDirectory root, string id)
    {
        string directory = Directory.CreateDirectory(Path.Combine(root.Path, id)).FullName;
        var manifest = new BackupManifest { CreatedUtc = DateTime.UtcNow, MetadataVersion = int.MaxValue };
        File.WriteAllText(
            Path.Combine(directory, BackupSnapshot.ManifestFileName),
            JsonSerializer.Serialize(manifest, BackupJson.Options));
        return BackupSnapshot.Load(directory);
    }
}
