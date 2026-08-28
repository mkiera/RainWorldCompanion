using System.Reflection;
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

    [Fact]
    public void The_last_seen_changelog_version_round_trips_through_a_save_and_a_load()
    {
        using var dir = new TempDirectory();
        var store = new SettingsStore(Path.Combine(dir.Path, "settings.json"));

        store.Save(new AppSettings { LastSeenChangelogVersion = "1.1.0" });

        Assert.Equal("1.1.0", store.Load().LastSeenChangelogVersion);
    }

    [Fact]
    public void The_window_geometry_round_trips_through_a_save_and_a_load()
    {
        using var dir = new TempDirectory();
        var store = new SettingsStore(Path.Combine(dir.Path, "settings.json"));

        store.Save(new AppSettings
        {
            WindowWidth = 1400,
            WindowHeight = 900,
            WindowLeft = 120,
            WindowTop = 80,
            WindowMaximized = true,
        });

        var settings = store.Load();

        Assert.Equal(1400, settings.WindowWidth);
        Assert.Equal(900, settings.WindowHeight);
        Assert.Equal(120, settings.WindowLeft);
        Assert.Equal(80, settings.WindowTop);
        Assert.True(settings.WindowMaximized);
    }

    [Fact]
    public void A_file_with_no_window_geometry_leaves_it_null()
    {
        using var dir = new TempDirectory();
        var store = new SettingsStore(Path.Combine(dir.Path, "settings.json"));

        store.Save(new AppSettings());

        var settings = store.Load();

        Assert.Null(settings.WindowWidth);
        Assert.Null(settings.WindowHeight);
        Assert.Null(settings.WindowLeft);
        Assert.Null(settings.WindowTop);
        Assert.False(settings.WindowMaximized);
    }

    [Fact]
    public void ReadForStartup_reads_geometry_without_resolving_paths()
    {
        using var dir = new TempDirectory();
        var store = new SettingsStore(Path.Combine(dir.Path, "settings.json"));

        store.Save(new AppSettings { WindowWidth = 1234, WindowHeight = 567 });

        var settings = store.ReadForStartup();

        Assert.NotNull(settings);
        Assert.Equal(1234, settings!.WindowWidth);
        Assert.Equal(567, settings.WindowHeight);
    }

    [Fact]
    public void ReadForStartup_is_null_when_there_is_no_settings_file()
    {
        using var dir = new TempDirectory();
        var store = new SettingsStore(Path.Combine(dir.Path, "settings.json"));

        Assert.Null(store.ReadForStartup());
    }

    /// <summary>App.OnStartup paints the window from this, before the full load has run.</summary>
    [Fact]
    public void ReadForStartup_reads_the_theme()
    {
        using var dir = new TempDirectory();
        var store = new SettingsStore(Path.Combine(dir.Path, "settings.json"));

        store.Save(new AppSettings { Theme = "dark" });

        Assert.Equal(AppTheme.Dark, AppThemes.Parse(store.ReadForStartup()?.Theme));
    }

    [Theory]
    [InlineData("dark", AppTheme.Dark)]
    [InlineData("light", AppTheme.Light)]
    [InlineData("LIGHT", AppTheme.Light)]
    [InlineData("  light  ", AppTheme.Light)]
    [InlineData("chartreuse", AppTheme.Dark)]
    [InlineData("", AppTheme.Dark)]
    [InlineData(null, AppTheme.Dark)]
    public void An_unreadable_theme_reads_as_dark(string? stored, AppTheme expected)
        => Assert.Equal(expected, AppThemes.Parse(stored));

    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    public void A_saved_theme_survives_the_round_trip(AppTheme theme)
    {
        using var dir = new TempDirectory();
        var store = new SettingsStore(Path.Combine(dir.Path, "settings.json"));

        store.Save(new AppSettings { Theme = theme.ToStorageString() });

        Assert.Equal(theme, AppThemes.Parse(store.Load().Theme));
    }

    /// <summary>Which is every file written before the toggle existed.</summary>
    [Fact]
    public void A_file_with_no_theme_loads_as_dark()
    {
        using var dir = new TempDirectory();
        var store = StoreWith(dir, """
        {
          "schemaVersion": 1,
          "gameSavePath": "C:\saves",
          "backupRootPath": "C:\backups"
        }
        """);

        Assert.Equal(AppTheme.Dark, AppThemes.Parse(store.Load().Theme));
    }

    /// <summary>
    /// Clone's own doc comment says a field left out of it silently reverts to default on every
    /// save. LastSeenChangelogVersion once made the same mistake in FromJson; this catches Clone
    /// dropping a future field the same way, without having to name each property here by hand.
    /// </summary>
    [Fact]
    public void Clone_copies_every_settable_property()
    {
        var original = new AppSettings();
        var properties = typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .ToList();

        foreach (var property in properties)
        {
            property.SetValue(original, DistinctValueFor(property.PropertyType));
        }

        var clone = original.Clone();

        foreach (var property in properties)
        {
            Assert.Equal(property.GetValue(original), property.GetValue(clone));
        }
    }

    private static object DistinctValueFor(Type type) => type switch
    {
        _ when type == typeof(string) => "distinct-value",
        _ when type == typeof(int) => 7,
        _ when type == typeof(bool) => true,
        _ when type == typeof(double?) => 12.5,
        _ when type == typeof(DateTimeOffset?) => new DateTimeOffset(2026, 8, 25, 9, 30, 0, TimeSpan.Zero),
        _ => throw new NotSupportedException($"Add a distinct value for {type} in this test."),
    };
}
