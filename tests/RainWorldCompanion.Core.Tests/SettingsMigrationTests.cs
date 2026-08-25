using RainWorldCompanion.Core.Settings;

namespace RainWorldCompanion.Tests;

/// <summary>
/// Carrying the settings folder over from the name the app used before it was renamed.
///
/// The folder holds backups and a save library that are the only copy of the saves in them, so the
/// rule throughout is that no outcome may lose any of it. A failed move leaves everything where it
/// was, an ambiguous case is left alone rather than guessed at, and the app starts either way.
/// </summary>
public class SettingsMigrationTests
{
    private static (string Previous, string Root) Roots(TempDirectory dir) =>
        (Path.Combine(dir.Path, "RainWorldSaveManager"), Path.Combine(dir.Path, "RainWorldCompanion"));

    private static void Seed(string root, string relative, string content = "x")
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void The_old_folder_is_renamed_with_everything_in_it()
    {
        using var dir = new TempDirectory();
        var (previous, root) = Roots(dir);
        Seed(previous, "settings.json", "{}");
        Seed(previous, Path.Combine("backups", "2026-08-24_19-31-07", "sav"), "save bytes");
        Seed(previous, Path.Combine("library", "a-run", "save.bin"), "library bytes");

        Assert.Equal(MigrationOutcome.Moved, SettingsMigration.MoveFolder(previous, root));

        Assert.False(Directory.Exists(previous));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(root, "settings.json")));
        Assert.Equal(
            "save bytes",
            File.ReadAllText(Path.Combine(root, "backups", "2026-08-24_19-31-07", "sav")));
        Assert.Equal("library bytes", File.ReadAllText(Path.Combine(root, "library", "a-run", "save.bin")));
    }

    [Fact]
    public void A_fresh_install_has_nothing_to_move()
    {
        using var dir = new TempDirectory();
        var (previous, root) = Roots(dir);

        Assert.Equal(MigrationOutcome.NothingToMove, SettingsMigration.MoveFolder(previous, root));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void Running_a_second_time_does_nothing()
    {
        // This runs at every launch, so all the launches after the one that migrated matter more
        // than the one that did. The second answer is AlreadyMigrated rather than NothingToMove
        // because the folder under the new name is what it found, which is also true of a fresh
        // install that never had an old folder to move.
        using var dir = new TempDirectory();
        var (previous, root) = Roots(dir);
        Seed(previous, "settings.json", "{}");

        Assert.Equal(MigrationOutcome.Moved, SettingsMigration.MoveFolder(previous, root));
        Assert.Equal(MigrationOutcome.AlreadyMigrated, SettingsMigration.MoveFolder(previous, root));
        Assert.Equal(MigrationOutcome.AlreadyMigrated, SettingsMigration.MoveFolder(previous, root));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(root, "settings.json")));
    }

    [Fact]
    public void Both_folders_existing_leaves_both_alone()
    {
        // Which copy of a given backup is the real one is not a question this can answer, and
        // guessing wrong destroys a save. The new folder is the live one and the old one stays on
        // disk for the user to look through.
        using var dir = new TempDirectory();
        var (previous, root) = Roots(dir);
        Seed(previous, "settings.json", "old");
        Seed(root, "settings.json", "new");

        Assert.Equal(MigrationOutcome.AlreadyMigrated, SettingsMigration.MoveFolder(previous, root));

        Assert.Equal("old", File.ReadAllText(Path.Combine(previous, "settings.json")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(root, "settings.json")));
    }

    [Fact]
    public void A_move_that_cannot_happen_reports_failure_and_keeps_the_data()
    {
        // A file inside the folder held open by something else. The app has to start anyway, and
        // it has to start with the data still reachable, so the old folder is left intact.
        using var dir = new TempDirectory();
        var (previous, root) = Roots(dir);
        Seed(previous, Path.Combine("backups", "held-open"), "bytes");

        using (var _ = File.Open(
                   Path.Combine(previous, "backups", "held-open"),
                   FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(MigrationOutcome.Failed, SettingsMigration.MoveFolder(previous, root));
        }

        Assert.True(Directory.Exists(previous));
        Assert.Equal("bytes", File.ReadAllText(Path.Combine(previous, "backups", "held-open")));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void A_stored_path_under_the_old_folder_follows_it()
    {
        using var dir = new TempDirectory();
        var (previous, root) = Roots(dir);
        Seed(root, Path.Combine("backups", "marker"));

        Assert.Equal(
            Path.Combine(root, "backups"),
            SettingsMigration.Repoint(Path.Combine(previous, "backups"), previous, root));
    }

    [Fact]
    public void The_folder_itself_is_repointed_as_well_as_what_is_under_it()
    {
        using var dir = new TempDirectory();
        var (previous, root) = Roots(dir);
        Directory.CreateDirectory(root);

        Assert.Equal(root, SettingsMigration.Repoint(previous, previous, root));
    }

    [Fact]
    public void A_folder_the_user_chose_somewhere_else_is_left_alone()
    {
        using var dir = new TempDirectory();
        var (previous, root) = Roots(dir);
        var elsewhere = Path.Combine(dir.Path, "D-drive-backups");
        Directory.CreateDirectory(elsewhere);
        Directory.CreateDirectory(root);

        Assert.Equal(elsewhere, SettingsMigration.Repoint(elsewhere, previous, root));
    }

    [Fact]
    public void A_path_that_still_exists_under_the_old_name_is_left_alone()
    {
        // Both folders are on disk, so the move did not happen and the stored path is still the
        // one holding the data. Re-pointing it would walk the user off their own backups.
        using var dir = new TempDirectory();
        var (previous, root) = Roots(dir);
        Seed(previous, Path.Combine("backups", "marker"));
        Seed(root, Path.Combine("backups", "marker"));

        var stored = Path.Combine(previous, "backups");
        Assert.Equal(stored, SettingsMigration.Repoint(stored, previous, root));
    }

    [Fact]
    public void A_path_that_is_missing_at_both_names_is_left_alone()
    {
        // Already broken before any of this ran. Pointing it at somewhere else that is also
        // missing changes nothing except which folder the error names.
        using var dir = new TempDirectory();
        var (previous, root) = Roots(dir);

        var stored = Path.Combine(previous, "backups");
        Assert.Equal(stored, SettingsMigration.Repoint(stored, previous, root));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_path_survives_being_repointed(string path)
    {
        using var dir = new TempDirectory();
        var (previous, root) = Roots(dir);

        Assert.Equal(path, SettingsMigration.Repoint(path, previous, root));
    }

    [Fact]
    public void A_folder_whose_name_merely_starts_the_same_is_not_inside_it()
    {
        // RainWorldSaveManager-old sits beside the folder, not under it, and a prefix compare
        // without a separator check would move it.
        using var dir = new TempDirectory();
        var (previous, root) = Roots(dir);
        var sibling = previous + "-old";
        Directory.CreateDirectory(sibling);
        Directory.CreateDirectory(root);

        Assert.Equal(sibling, SettingsMigration.Repoint(sibling, previous, root));
    }

    [Fact]
    public void The_rewrite_leaves_the_settings_file_naming_the_folder_that_now_exists()
    {
        using var dir = new TempDirectory();
        var (previous, root) = Roots(dir);
        Seed(previous, Path.Combine("backups", "marker"));
        Seed(previous, Path.Combine("library", "marker"));
        Seed(previous, "settings.json", $$"""
        {
          "schemaVersion": 1,
          "gameSavePath": "C:\\saves",
          "backupRootPath": {{Json(Path.Combine(previous, "backups"))}},
          "libraryRootPath": {{Json(Path.Combine(previous, "library"))}},
          "somethingALaterVersionAdded": { "keep": [1, 2] }
        }
        """);

        Assert.Equal(MigrationOutcome.Moved, SettingsMigration.MoveFolder(previous, root));
        var settingsPath = Path.Combine(root, "settings.json");
        Assert.True(SettingsMigration.RepointSettingsFile(settingsPath, previous, root));

        var written = File.ReadAllText(settingsPath);
        Assert.Contains(Json(Path.Combine(root, "backups")).Trim('"'), written);
        Assert.Contains(Json(Path.Combine(root, "library")).Trim('"'), written);
        Assert.DoesNotContain("RainWorldSaveManager", written);
        // A property this build has never heard of survives the edit, because a downgrade has to
        // be able to read back what a later version wrote.
        Assert.Contains("somethingALaterVersionAdded", written);
        // And the fields it was not asked to touch are still there.
        Assert.Contains("C:\\\\saves", written);
    }

    [Fact]
    public void The_rewrite_reports_no_change_when_there_is_nothing_to_change()
    {
        using var dir = new TempDirectory();
        var (previous, root) = Roots(dir);
        var elsewhere = Path.Combine(dir.Path, "D-drive-backups");
        Directory.CreateDirectory(elsewhere);
        Directory.CreateDirectory(root);
        Seed(root, "settings.json", $$"""
        {"backupRootPath": {{Json(elsewhere)}}}
        """);

        Assert.False(SettingsMigration.RepointSettingsFile(Path.Combine(root, "settings.json"), previous, root));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1, 2, 3]")]
    [InlineData("")]
    public void An_unreadable_settings_file_is_left_alone_rather_than_replaced(string content)
    {
        // Repoint still corrects the paths in memory on every load, so failing here costs the
        // tidiness of the file and nothing the user can see. Overwriting it would cost more.
        using var dir = new TempDirectory();
        var (previous, root) = Roots(dir);
        Seed(root, "settings.json", content);
        var settingsPath = Path.Combine(root, "settings.json");

        Assert.False(SettingsMigration.RepointSettingsFile(settingsPath, previous, root));
        Assert.Equal(content, File.ReadAllText(settingsPath));
    }

    [Fact]
    public void A_missing_settings_file_is_not_created_by_the_rewrite()
    {
        using var dir = new TempDirectory();
        var (previous, root) = Roots(dir);
        Directory.CreateDirectory(root);
        var settingsPath = Path.Combine(root, "settings.json");

        Assert.False(SettingsMigration.RepointSettingsFile(settingsPath, previous, root));
        Assert.False(File.Exists(settingsPath));
    }

    [Fact]
    public void A_settings_file_written_with_PascalCase_names_is_rewritten_too()
    {
        using var dir = new TempDirectory();
        var (previous, root) = Roots(dir);
        Seed(previous, Path.Combine("backups", "marker"));
        Seed(previous, "settings.json", $$"""
        {"BackupRootPath": {{Json(Path.Combine(previous, "backups"))}}}
        """);

        Assert.Equal(MigrationOutcome.Moved, SettingsMigration.MoveFolder(previous, root));
        Assert.True(SettingsMigration.RepointSettingsFile(Path.Combine(root, "settings.json"), previous, root));
        Assert.DoesNotContain("RainWorldSaveManager", File.ReadAllText(Path.Combine(root, "settings.json")));
    }

    private static string Json(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    [Fact]
    public void The_settings_file_comes_back_pointing_at_the_folder_that_now_holds_the_backups()
    {
        // The whole point, end to end: rename the folder, then load the settings written before it
        // and check the app knows where the backups went.
        using var dir = new TempDirectory();
        var (previous, root) = Roots(dir);
        Seed(previous, Path.Combine("backups", "2026-08-24_19-31-07", "manifest.json"), "{}");
        Seed(previous, Path.Combine("library", "a-run", "entry.json"), "{}");
        Seed(previous, "settings.json", $$"""
        {
          "schemaVersion": 1,
          "gameSavePath": "C:\\saves",
          "backupRootPath": {{System.Text.Json.JsonSerializer.Serialize(Path.Combine(previous, "backups"))}},
          "libraryRootPath": {{System.Text.Json.JsonSerializer.Serialize(Path.Combine(previous, "library"))}}
        }
        """);

        Assert.Equal(MigrationOutcome.Moved, SettingsMigration.MoveFolder(previous, root));

        var store = new SettingsStore(Path.Combine(root, "settings.json"));
        var settings = store.Load();

        Assert.Equal(
            Path.Combine(root, "backups"),
            SettingsMigration.Repoint(settings.BackupRootPath, previous, root));
        Assert.Equal(
            Path.Combine(root, "library"),
            SettingsMigration.Repoint(settings.LibraryRootPath, previous, root));
    }
}
