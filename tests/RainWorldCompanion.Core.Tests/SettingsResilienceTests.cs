using RainWorldCompanion.Core.Settings;
using RainWorldCompanion.Core.Updates;

namespace RainWorldCompanion.Tests;

/// <summary>
/// The updates window can install an older version, so an earlier build reads a file a later one
/// wrote. Losing the whole file to one bad value costs the user their backup and library
/// locations, which reads as "my backups are gone" even though the folders are still on disk.
/// </summary>
public class SettingsResilienceTests
{
    private static SettingsStore StoreWith(TempDirectory dir, string json)
    {
        var path = Path.Combine(dir.Path, "settings.json");
        File.WriteAllText(path, json);
        return new SettingsStore(path);
    }

    /// <summary>
    /// The serializer is configured case-insensitively for files written before the camelCase
    /// naming policy was set.
    /// </summary>
    [Fact]
    public void A_file_written_with_PascalCase_names_still_loads()
    {
        using var dir = new TempDirectory();
        var store = StoreWith(dir, """
        {
          "SchemaVersion": 1,
          "GameSavePath": "C:\\saves",
          "BackupRootPath": "C:\\backups",
          "LibraryRootPath": "C:\\library",
          "GameInstallPath": "C:\\game",
          "UpdateChannel": "prerelease",
          "AutoCheckUpdates": false
        }
        """);

        var settings = store.Load();

        Assert.Equal("C:\\saves", settings.GameSavePath);
        Assert.Equal("C:\\backups", settings.BackupRootPath);
        Assert.Equal("C:\\library", settings.LibraryRootPath);
        Assert.Equal("C:\\game", settings.GameInstallPath);
        Assert.Equal("prerelease", settings.UpdateChannel);
        Assert.False(settings.AutoCheckUpdates);
    }

    [Fact]
    public void A_file_written_before_the_update_settings_existed_gets_their_defaults()
    {
        using var dir = new TempDirectory();
        var store = StoreWith(dir, """
        {
          "schemaVersion": 1,
          "gameSavePath": "C:\\saves",
          "backupRootPath": "C:\\backups",
          "libraryRootPath": "C:\\library"
        }
        """);

        var settings = store.Load();

        Assert.Equal("stable", settings.UpdateChannel);
        Assert.True(settings.AutoCheckUpdates);
        Assert.Null(settings.LastUpdateCheckUtc);
        Assert.Equal(1, settings.SchemaVersion);
    }

    [Fact]
    public void An_unknown_property_from_a_later_version_is_ignored_rather_than_fatal()
    {
        using var dir = new TempDirectory();
        var store = StoreWith(dir, """
        {
          "backupRootPath": "C:\\backups",
          "libraryRootPath": "C:\\library",
          "somethingAddedLater": { "nested": [1, 2, 3] }
        }
        """);

        var settings = store.Load();

        Assert.Equal("C:\\backups", settings.BackupRootPath);
        Assert.Equal("C:\\library", settings.LibraryRootPath);
    }

    /// <summary>The case a downgrade produces: one unreadable field must not cost the paths beside it.</summary>
    [Theory]
    [InlineData(""""{"schemaVersion": "not a number", "backupRootPath": "C:\\backups", "libraryRootPath": "C:\\library"}"""")]
    [InlineData(""""{"autoCheckUpdates": "yes", "backupRootPath": "C:\\backups", "libraryRootPath": "C:\\library"}"""")]
    [InlineData(""""{"updateChannel": 7, "backupRootPath": "C:\\backups", "libraryRootPath": "C:\\library"}"""")]
    [InlineData(""""{"lastUpdateCheckUtc": "the other day", "backupRootPath": "C:\\backups", "libraryRootPath": "C:\\library"}"""")]
    [InlineData(""""{"gameInstallPath": [1, 2], "backupRootPath": "C:\\backups", "libraryRootPath": "C:\\library"}"""")]
    public void One_unreadable_field_does_not_cost_the_paths_beside_it(string json)
    {
        using var dir = new TempDirectory();
        var store = StoreWith(dir, json);

        var settings = store.Load();

        Assert.Equal("C:\\backups", settings.BackupRootPath);
        Assert.Equal("C:\\library", settings.LibraryRootPath);
    }

    [Fact]
    public void An_unreadable_field_falls_back_to_its_own_default()
    {
        using var dir = new TempDirectory();
        var store = StoreWith(dir, """{"updateChannel": 7, "autoCheckUpdates": "yes"}""");

        var settings = store.Load();

        Assert.Equal("stable", settings.UpdateChannel);
        Assert.True(settings.AutoCheckUpdates);
    }

    /// <summary>
    /// Not JSON at all falls back wholesale, since there is nothing here to salvage. This is a
    /// different case from one bad field, which keeps everything else.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{ unclosed")]
    [InlineData("[1, 2, 3]")]
    [InlineData("\"a string\"")]
    [InlineData("null")]
    public void A_document_that_is_not_a_settings_object_falls_back_to_defaults(string json)
    {
        using var dir = new TempDirectory();
        var store = StoreWith(dir, json);

        var settings = store.Load();

        Assert.Equal(AppSettings.DefaultBackupRootPath, settings.BackupRootPath);
        Assert.Equal(AppSettings.DefaultLibraryRootPath, settings.LibraryRootPath);
        Assert.Equal("stable", settings.UpdateChannel);
        Assert.True(settings.AutoCheckUpdates);
    }

    [Fact]
    public void The_update_settings_round_trip_through_a_save_and_a_load()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "settings.json");
        var store = new SettingsStore(path);
        var stamp = new DateTimeOffset(2026, 8, 25, 9, 30, 0, TimeSpan.Zero);

        store.Save(new AppSettings
        {
            SchemaVersion = 1,
            GameSavePath = "C:\\saves",
            BackupRootPath = "C:\\backups",
            LibraryRootPath = "C:\\library",
            UpdateChannel = "prerelease",
            AutoCheckUpdates = false,
            LastUpdateCheckUtc = stamp,
        });

        var settings = store.Load();

        Assert.Equal("prerelease", settings.UpdateChannel);
        Assert.Equal(UpdateChannel.Prerelease, UpdateChannels.Parse(settings.UpdateChannel));
        Assert.False(settings.AutoCheckUpdates);
        Assert.Equal(stamp, settings.LastUpdateCheckUtc);
    }
}
