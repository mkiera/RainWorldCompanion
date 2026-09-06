using System.IO;
using System.Reflection;
using System.Text.Json;
using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Library;
using RainWorldCompanion.Core.Settings;
using RainWorldCompanion.Core.System;
using RainWorldCompanion.Services;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.App.Tests;

public class VerificationRefreshTests
{
    [Fact]
    public async Task Reload_after_deletion_keeps_verification_for_remaining_rows()
    {
        using var files = new TestDirectory();
        string saves = files.Create("saves");
        string backups = files.Create("backups");
        string libraryRoot = files.Create("library");
        var detector = new NullGameProcessDetector();
        var service = new BackupService(saves, backups, detector, "test");
        var library = new SaveLibrary(service, libraryRoot, detector, "test");
        var icons = new SlugcatIconProvider();
        var viewModel = new MainViewModel(
            new SettingsStore(Path.Combine(files.Path, "settings.json")),
            detector,
            icons,
            "test");

        BackupSnapshot keptBackup = AddBackup(backups, "kept-backup");
        BackupSnapshot deletedBackup = AddBackup(backups, "deleted-backup");
        LibraryEntry keptEntry = AddLibraryEntry(libraryRoot, "kept-entry");
        LibraryEntry deletedEntry = AddLibraryEntry(libraryRoot, "deleted-entry");

        viewModel.Backups.Add(new BackupItemViewModel(keptBackup, icons) { VerifiedOk = true });
        viewModel.Backups.Add(new BackupItemViewModel(deletedBackup, icons) { VerifiedOk = false });
        viewModel.LibraryEntries.Add(new LibraryEntryViewModel(keptEntry, icons, saves) { VerifiedOk = true });
        viewModel.LibraryEntries.Add(new LibraryEntryViewModel(deletedEntry, icons, saves) { VerifiedOk = false });

        SetField(viewModel, "_backupService", service);
        SetField(viewModel, "_library", library);
        viewModel.Shutdown();
        Directory.Delete(deletedBackup.DirectoryPath, recursive: true);
        Directory.Delete(deletedEntry.DirectoryPath, recursive: true);

        await Reload(viewModel);

        Assert.True(Assert.Single(viewModel.Backups).VerifiedOk);
        Assert.True(Assert.Single(viewModel.LibraryEntries).VerifiedOk);
    }

    private static BackupSnapshot AddBackup(string root, string id)
    {
        string directory = Directory.CreateDirectory(Path.Combine(root, id)).FullName;
        var manifest = new BackupManifest
        {
            CreatedUtc = DateTime.UtcNow,
            MetadataVersion = int.MaxValue,
        };
        File.WriteAllText(
            Path.Combine(directory, BackupSnapshot.ManifestFileName),
            JsonSerializer.Serialize(manifest, BackupJson.Options));
        return BackupSnapshot.Load(directory);
    }

    private static LibraryEntry AddLibraryEntry(string root, string id)
    {
        string directory = Directory.CreateDirectory(Path.Combine(root, id)).FullName;
        File.WriteAllText(Path.Combine(directory, LibraryEntry.SaveFileName), "save");
        var manifest = new LibraryManifest
        {
            Name = id,
            CreatedUtc = DateTime.UtcNow,
            MetadataVersion = int.MaxValue,
        };
        File.WriteAllText(
            Path.Combine(directory, LibraryEntry.ManifestFileName),
            JsonSerializer.Serialize(manifest, BackupJson.Options));
        return LibraryEntry.Load(directory);
    }

    private static async Task Reload(MainViewModel viewModel)
    {
        MethodInfo method = typeof(MainViewModel).GetMethod(
            "ReloadAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)method.Invoke(viewModel, new object[] { true })!;
    }

    private static void SetField(MainViewModel viewModel, string name, object value) =>
        typeof(MainViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(viewModel, value);

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "RainWorldCompanionTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Create(string name) => Directory.CreateDirectory(
            System.IO.Path.Combine(Path, name)).FullName;

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
