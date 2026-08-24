using RainWorldSaveManager.Core.Settings;

namespace RainWorldSaveManager.Tests;

/// <summary>
/// Validation is what stops the backup root from being carved out of the save folder, where a
/// restore would then walk over its own snapshots. The store has to survive a file that was
/// half-written by a previous crash.
/// </summary>
public class SettingsTests
{
    // ---- SettingsValidation ----

    [Theory]
    [InlineData(@"C:\Games\Rain World", @"C:\Games\Rain World")]
    [InlineData(@"C:\Games\Rain World", @"C:\Games\Rain World\")]
    [InlineData(@"C:\Games\Rain World\", @"C:\Games\Rain World")]
    [InlineData(@"c:\games\rain world", @"C:\GAMES\RAIN WORLD")]
    [InlineData(@"C:\Games\Rain World", @"C:\Games\Other\..\Rain World")]
    public void Validate_rejects_the_same_folder_for_both_paths(string savePath, string backupPath)
        => Assert.NotNull(SettingsValidation.Validate(savePath, backupPath));

    [Theory]
    [InlineData(@"C:\Games\Rain World", @"C:\Games\Rain World\backups")]
    [InlineData(@"C:\Games\Rain World", @"C:\Games\Rain World\a\b\c")]
    [InlineData(@"C:\Games\Rain World\", @"C:\Games\Rain World\backups\")]
    [InlineData(@"C:\Games\Rain World", @"c:\games\rain world\BACKUPS")]
    public void Validate_rejects_a_backup_root_inside_the_save_folder(string savePath, string backupPath)
        => Assert.NotNull(SettingsValidation.Validate(savePath, backupPath));

    [Theory]
    [InlineData(@"C:\Backups\live", @"C:\Backups")]
    [InlineData(@"C:\Backups\a\b\c", @"C:\Backups")]
    [InlineData(@"c:\backups\LIVE\", @"C:\Backups\")]
    public void Validate_rejects_a_save_folder_inside_the_backup_root(string savePath, string backupPath)
        => Assert.NotNull(SettingsValidation.Validate(savePath, backupPath));

    [Theory]
    [InlineData("", @"C:\Backups")]
    [InlineData(@"C:\Games\Rain World", "")]
    [InlineData("", "")]
    public void Validate_rejects_an_empty_path(string savePath, string backupPath)
        => Assert.NotNull(SettingsValidation.Validate(savePath, backupPath));

    [Theory]
    [InlineData(@"C:\Foo", @"C:\FooBar")]
    [InlineData(@"C:\FooBar", @"C:\Foo")]
    [InlineData(@"C:\Foo\bar", @"C:\Foo\barbaz")]
    public void Validate_accepts_sibling_folders_that_share_a_name_prefix(string savePath, string backupPath)
        => Assert.Null(SettingsValidation.Validate(savePath, backupPath));

    [Theory]
    [InlineData(@"C:\Users\Someone\AppData\LocalLow\Videocult\Rain World", @"C:\Users\Someone\AppData\Local\RainWorldSaveManager\backups")]
    [InlineData(@"C:\Games\Rain World", @"D:\Backups\Rain World")]
    public void Validate_accepts_two_unrelated_folders(string savePath, string backupPath)
        => Assert.Null(SettingsValidation.Validate(savePath, backupPath));

    [Theory]
    [InlineData("   ", @"C:\Backups")]
    [InlineData(@"C:\Games\Rain World", "   ")]
    [InlineData("not a rooted path", "also not rooted")]
    [InlineData(@"C:\Games\Rain|World", @"C:\Backups")]
    public void Validate_does_not_throw_on_input_a_user_could_type(string savePath, string backupPath)
    {
        var reason = SettingsValidation.Validate(savePath, backupPath);

        // Whether these count as valid is the implementation's call. Throwing is not.
        Assert.True(reason is null || reason.Length > 0);
    }

    /// <summary>
    /// Junctioning a folder out of the save folder and naming the junction as the backup root is
    /// how a user relocates backups onto another drive. The two paths share no textual prefix, so
    /// a string comparison accepts it, and every backup then sweeps up every earlier snapshot and
    /// the first restore deletes the whole backup store.
    /// </summary>
    [JunctionFact]
    public void Validate_rejects_a_backup_root_that_is_only_aliased_out_of_the_save_folder()
    {
        using var live = new TempDirectory("live");
        using var alias = new TempDirectory("alias-parent");
        var inside = live.CreateSubdirectory(@"dvrmentSaveStates\bk");
        var link = alias.Resolve("backups");
        Assert.True(Links.TryCreateDirectoryJunction(link, inside));

        Assert.NotNull(SettingsValidation.Validate(live.Path, link));
    }

    [JunctionFact]
    public void Validate_still_accepts_a_backup_root_aliased_somewhere_harmless()
    {
        using var live = new TempDirectory("live");
        using var elsewhere = new TempDirectory("elsewhere");
        using var alias = new TempDirectory("alias-parent");
        var link = alias.Resolve("backups");
        Assert.True(Links.TryCreateDirectoryJunction(link, elsewhere.Path));

        Assert.Null(SettingsValidation.Validate(live.Path, link));
    }

    [Fact]
    public void The_rejection_reason_is_something_a_settings_dialog_can_show()
    {
        var reason = SettingsValidation.Validate(@"C:\Games\Rain World", @"C:\Games\Rain World\backups");

        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    // ---- SettingsValidation.ValidateText ----

    // The settings dialog validates on every keystroke, and the full Validate resolves each path
    // through the filesystem, which blocks for the whole network timeout on a half-typed UNC
    // path. ValidateText is the part the dialog can afford to run inline, so it has to answer
    // without touching disk and give the same reasons for the cases it does cover.

    [Theory]
    [InlineData("", @"C:\Backups")]
    [InlineData(@"C:\Games\Rain World", "")]
    [InlineData("   ", @"C:\Backups")]
    [InlineData(@"C:\Games\Rain World", "   ")]
    [InlineData(@"saves", @"C:\Backups")]
    [InlineData(@"C:\Games\Rain World", @"backups\here")]
    public void ValidateText_gives_the_same_reason_as_the_full_check(string savePath, string backupPath)
    {
        var quick = SettingsValidation.ValidateText(savePath, backupPath);

        Assert.NotNull(quick);
        Assert.Equal(SettingsValidation.Validate(savePath, backupPath), quick);
    }

    [Theory]
    [InlineData(@"C:\Games\Rain World", @"C:\Backups")]
    // Both are fully qualified, so the text checks pass. Whether they may be used together is
    // for the full check to say, and these two pairs are ones it rejects.
    [InlineData(@"C:\Games\Rain World", @"C:\Games\Rain World")]
    [InlineData(@"C:\Games\Rain World", @"C:\Games\Rain World\backups")]
    public void ValidateText_passes_anything_whose_text_is_a_full_path(string savePath, string backupPath)
    {
        Assert.Null(SettingsValidation.ValidateText(savePath, backupPath));
    }

    [Fact]
    public void ValidateText_answers_for_a_path_on_a_host_that_does_not_exist()
    {
        // A UNC path is fully qualified from the second backslash on, and resolving one whose
        // host does not answer is what made typing into the dialog freeze the window.
        var quick = SettingsValidation.ValidateText(@"\\no-such-host-here\rw\saves", @"C:\Backups");

        Assert.Null(quick);
    }

    // ---- AppSettings ----

    [Fact]
    public void CreateDefault_does_not_go_looking_for_the_game_install()
    {
        // Finding the install reads the Steam registry value, parses every libraryfolders.vdf
        // and probes each library it names, and a library on a share whose machine is off makes
        // that probe block for the full SMB timeout. CreateDefault is called on the dispatcher
        // before the window is shown, so the lookup belongs to SettingsStore.Load, which every
        // caller runs on a worker.
        Assert.Null(AppSettings.CreateDefault().GameInstallPath);
    }

    [Fact]
    public void The_default_backup_root_sits_under_the_local_app_data_folder()
    {
        var settings = AppSettings.CreateDefault();

        Assert.Equal(1, settings.SchemaVersion);
        Assert.False(string.IsNullOrWhiteSpace(settings.BackupRootPath));
        Assert.Contains("RainWorldSaveManager", settings.BackupRootPath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("backups", settings.BackupRootPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateDefault_returns_a_fresh_instance_each_time()
    {
        var first = AppSettings.CreateDefault();
        var second = AppSettings.CreateDefault();

        first.GameSavePath = @"C:\Somewhere";

        Assert.NotSame(first, second);
        Assert.NotEqual(@"C:\Somewhere", second.GameSavePath);
    }

    // ---- SettingsStore ----

    [Fact]
    public void SettingsPath_is_the_path_the_store_was_built_with()
    {
        using var temp = new TempDirectory("settings");
        var path = temp.Resolve("settings.json");

        Assert.Equal(path, new SettingsStore(path).SettingsPath);
    }

    [Fact]
    public void Load_on_a_missing_file_returns_usable_defaults()
    {
        using var temp = new TempDirectory("settings");
        var store = new SettingsStore(temp.Resolve("settings.json"));

        var settings = store.Load();

        Assert.NotNull(settings);
        Assert.Equal(1, settings.SchemaVersion);
        Assert.False(string.IsNullOrWhiteSpace(settings.BackupRootPath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ this is not json ]")]
    [InlineData("<xml>wrong format entirely</xml>")]
    [InlineData("null")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{\"SchemaVersion\": \"not a number\"}")]
    public void Load_on_a_corrupt_file_returns_usable_defaults(string contents)
    {
        using var temp = new TempDirectory("settings");
        var path = temp.WriteText("settings.json", contents);
        var store = new SettingsStore(path);

        var settings = store.Load();

        Assert.NotNull(settings);
        Assert.Equal(1, settings.SchemaVersion);
        Assert.False(string.IsNullOrWhiteSpace(settings.BackupRootPath));
    }

    [Fact]
    public void Load_on_a_file_of_binary_garbage_returns_usable_defaults()
    {
        using var temp = new TempDirectory("settings");
        var path = temp.WriteBytes("settings.json", SyntheticSave.GarbageBytes());

        var settings = new SettingsStore(path).Load();

        Assert.NotNull(settings);
        Assert.False(string.IsNullOrWhiteSpace(settings.BackupRootPath));
    }

    [Fact]
    public void Save_then_load_round_trips_every_field()
    {
        using var temp = new TempDirectory("settings");
        var store = new SettingsStore(temp.Resolve("settings.json"));
        var written = new AppSettings
        {
            SchemaVersion = 1,
            GameSavePath = @"C:\Users\Someone\AppData\LocalLow\Videocult\Rain World",
            BackupRootPath = @"D:\Backups\Rain World",
        };

        store.Save(written);
        var read = store.Load();

        Assert.Equal(written.SchemaVersion, read.SchemaVersion);
        Assert.Equal(written.GameSavePath, read.GameSavePath);
        Assert.Equal(written.BackupRootPath, read.BackupRootPath);
    }

    [Fact]
    public void Save_round_trips_through_a_second_store_on_the_same_path()
    {
        using var temp = new TempDirectory("settings");
        var path = temp.Resolve("settings.json");
        new SettingsStore(path).Save(new AppSettings
        {
            GameSavePath = @"C:\Games\Rain World",
            BackupRootPath = @"D:\Backups",
        });

        var read = new SettingsStore(path).Load();

        Assert.Equal(@"C:\Games\Rain World", read.GameSavePath);
        Assert.Equal(@"D:\Backups", read.BackupRootPath);
    }

    [Fact]
    public void Save_leaves_no_temporary_file_behind()
    {
        using var temp = new TempDirectory("settings");
        var path = temp.Resolve("settings.json");
        var store = new SettingsStore(path);

        store.Save(new AppSettings { GameSavePath = @"C:\a", BackupRootPath = @"C:\b" });

        Assert.True(File.Exists(path));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Saving_twice_overwrites_rather_than_failing()
    {
        using var temp = new TempDirectory("settings");
        var store = new SettingsStore(temp.Resolve("settings.json"));

        store.Save(new AppSettings { GameSavePath = @"C:\first", BackupRootPath = @"C:\b" });
        store.Save(new AppSettings { GameSavePath = @"C:\second", BackupRootPath = @"C:\b" });

        Assert.Equal(@"C:\second", store.Load().GameSavePath);
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Save_creates_the_settings_folder_when_it_is_missing()
    {
        using var temp = new TempDirectory("settings");
        var store = new SettingsStore(temp.Resolve(@"nested\folder\settings.json"));

        store.Save(new AppSettings { GameSavePath = @"C:\a", BackupRootPath = @"C:\b" });

        Assert.True(File.Exists(temp.Resolve(@"nested\folder\settings.json")));
        Assert.Equal(@"C:\a", store.Load().GameSavePath);
    }

    [Fact]
    public void A_corrupt_file_is_still_replaceable_by_a_save()
    {
        using var temp = new TempDirectory("settings");
        var path = temp.WriteText("settings.json", "{ half written");
        var store = new SettingsStore(path);

        store.Save(new AppSettings { GameSavePath = @"C:\recovered", BackupRootPath = @"C:\b" });

        Assert.Equal(@"C:\recovered", store.Load().GameSavePath);
    }
}
