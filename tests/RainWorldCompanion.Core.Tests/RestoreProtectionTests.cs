using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Library;
using RainWorldCompanion.Core.Settings;
using RainWorldCompanion.Core.System;

namespace RainWorldCompanion.Tests;

public class RestoreProtectionTests
{
    [Fact]
    public void Restoring_the_oldest_automatic_backup_keeps_its_source_while_lists_are_refreshed()
    {
        using var live = new TempDirectory("live");
        using var backups = new TempDirectory("backups");
        var service = new BackupService(live.Path, backups.Path, FakeGameDetector.NotRunning(), "test");
        live.WriteText("sav", "original");
        var oldest = service.CreateBackup(null, null, BackupKind.PreRestoreSafety);
        for (var index = 1; index < BackupService.RetainedAutomaticBackups; index++)
        {
            live.WriteText("sav", "later " + index);
            service.CreateBackup(null, null, BackupKind.PreRestoreSafety);
        }

        var sourcePresent = true;
        var hook = ProgressHook.On("Restoring ", _ =>
        {
            service.ListBackups();
            sourcePresent &= File.Exists(Path.Combine(oldest.DirectoryPath, "sav"));
        });
        var result = service.RestoreBackup(oldest, hook);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.True(sourcePresent);
        Assert.Equal("original", File.ReadAllText(live.Resolve("sav")));
        Assert.Equal(BackupService.RetainedAutomaticBackups, service.ListBackups().Count);
    }

    [Theory]
    [InlineData(false, "Writing manifest")]
    [InlineData(true, "Writing manifest")]
    [InlineData(false, "Removing files the backup does not have")]
    [InlineData(true, "Removing ModConfigs")]
    public void Restore_preserves_deletion_targets_that_arrive_or_change_after_safety_capture(bool existed, string when)
    {
        using var world = new BackupWorld();
        var snapshot = world.Service.CreateBackup(null, null);
        const string relative = @"ModConfigs\late.json";
        if (existed)
        {
            world.Live.WriteText(relative, "old cloud data");
        }
        var capturedTime = existed ? File.GetLastWriteTimeUtc(world.Live.Resolve(relative)) : DateTime.UtcNow;
        var hook = ProgressHook.On(when, _ =>
        {
            world.Live.WriteText(relative, "new cloud data");
            File.SetLastWriteTimeUtc(world.Live.Resolve(relative), capturedTime);
        }, limit: 1);

        var result = world.Service.RestoreBackup(snapshot, hook);

        Assert.Equal(1, hook.Fired);
        Assert.True(world.Live.FileExists(relative), "uncaptured cloud data was deleted");
        Assert.Equal("new cloud data", File.ReadAllText(world.Live.Resolve(relative)));
        Assert.False(result.Success);
        Assert.NotNull(result.SafetySnapshot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Restore_preserves_overwrite_targets_that_arrive_or_change_after_safety_capture(bool existed)
    {
        using var world = new BackupWorld();
        var snapshot = world.Service.CreateBackup(null, null);
        if (!existed)
        {
            File.Delete(world.Live.Resolve("sav3"));
        }
        var hook = ProgressHook.On("Restoring sav3 ", _ => world.Live.WriteText("sav3", "new cloud save"), limit: 1);

        var result = world.Service.RestoreBackup(snapshot, hook);

        Assert.Equal(1, hook.Fired);
        Assert.Equal("new cloud save", File.ReadAllText(world.Live.Resolve("sav3")));
        Assert.False(result.Success);
        Assert.NotNull(result.SafetySnapshot);
    }

    [Fact]
    public void Restore_preserves_a_file_that_arrives_immediately_after_the_safety_scan()
    {
        using var world = new BackupWorld();
        var snapshot = world.Service.CreateBackup(null, null);
        const string relative = @"ModConfigs\late.json";
        var scope = new ScopeWithSideEffect(world.Live.Path, 1, () => world.Live.WriteText(relative, "new cloud data"));
        var service = new BackupService(world.Live.Path, world.BackupRoot.Path, world.Detector, "test", scope);

        var result = service.RestoreBackup(snapshot);

        Assert.True(world.Live.FileExists(relative), "file absent from the safety enumeration was deleted");
        Assert.Equal("new cloud data", File.ReadAllText(world.Live.Resolve(relative)));
        Assert.False(result.Success);
    }
}

public class StoragePathProtectionTests
{
    [JunctionFact]
    public void Missing_children_beneath_a_junction_into_saves_are_rejected_for_both_storage_roots()
    {
        using var live = new TempDirectory("live");
        using var backups = new TempDirectory("backups");
        using var aliases = new TempDirectory("aliases");
        var configs = live.CreateSubdirectory("ModConfigs");
        var alias = aliases.Resolve("configs");
        Assert.True(Links.TryCreateDirectoryJunction(alias, configs));
        var child = Path.Combine(alias, "missing", "storage");

        Assert.Equal(Path.Combine(configs, "missing", "storage"), CanonicalPath.Resolve(child));
        Assert.NotNull(SettingsValidation.Validate(live.Path, child));
        Assert.NotNull(SettingsValidation.Validate(live.Path, backups.Path, child));
        Assert.Throws<ArgumentException>(() => new BackupService(live.Path, child, FakeGameDetector.NotRunning(), "test"));
        var service = new BackupService(live.Path, backups.Path, FakeGameDetector.NotRunning(), "test");
        Assert.Throws<ArgumentException>(() => new SaveLibrary(service, child, FakeGameDetector.NotRunning(), "test"));
        Assert.False(Directory.Exists(child));
    }

    [JunctionFact]
    public void A_storage_alias_retargeted_after_service_construction_is_rejected_before_writing()
    {
        using var world = new BackupWorld();
        using var elsewhere = new TempDirectory("elsewhere");
        using var aliases = new TempDirectory("aliases");
        var alias = aliases.Resolve("storage");
        Assert.True(Links.TryCreateDirectoryJunction(alias, elsewhere.Path));
        var backupRoot = Path.Combine(alias, "backups");
        var libraryRoot = Path.Combine(alias, "library");
        var service = new BackupService(world.Live.Path, backupRoot, world.Detector, "test");
        var library = new SaveLibrary(world.Service, libraryRoot, world.Detector, "test");
        Directory.Delete(alias);
        Assert.True(Links.TryCreateDirectoryJunction(alias, world.Live.CreateSubdirectory("ModConfigs")));

        Assert.Throws<IOException>(() => service.CreateBackup(null, null));
        Assert.Throws<IOException>(() => library.StoreSlot(
            new RainWorldCompanion.Core.Saves.SaveSlotRef(RainWorldCompanion.Core.Saves.SaveRealm.Local, 1),
            "stored", null));
        Assert.False(Directory.Exists(backupRoot));
        Assert.False(Directory.Exists(libraryRoot));
    }

    [JunctionFact]
    public void An_unresolvable_junction_is_rejected_instead_of_assumed_safe()
    {
        using var live = new TempDirectory("live");
        using var elsewhere = new TempDirectory("elsewhere");
        using var aliases = new TempDirectory("aliases");
        var target = elsewhere.CreateSubdirectory("target");
        var alias = aliases.Resolve("storage");
        Assert.True(Links.TryCreateDirectoryJunction(alias, target));
        Directory.Delete(target);
        var child = Path.Combine(alias, "missing", "backups");

        Assert.NotNull(SettingsValidation.Validate(live.Path, child));
        Assert.Throws<IOException>(() => CanonicalPath.Resolve(child));
    }

    [JunctionFact]
    public void Missing_children_beneath_an_unrelated_junction_remain_usable()
    {
        using var live = new TempDirectory("live");
        using var elsewhere = new TempDirectory("elsewhere");
        using var aliases = new TempDirectory("aliases");
        var alias = aliases.Resolve("storage");
        Assert.True(Links.TryCreateDirectoryJunction(alias, elsewhere.Path));
        var child = Path.Combine(alias, "missing", "backups");

        Assert.Equal(Path.Combine(elsewhere.Path, "missing", "backups"), CanonicalPath.Resolve(child));
        Assert.Null(SettingsValidation.Validate(live.Path, child));
        var service = new BackupService(live.Path, child, FakeGameDetector.NotRunning(), "test");
        Assert.True(service.CreateBackup(null, null).IsComplete);
    }
}
