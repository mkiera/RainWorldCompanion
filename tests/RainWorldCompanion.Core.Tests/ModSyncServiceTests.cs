using RainWorldCompanion.Core.Mods;

namespace RainWorldCompanion.Tests;

public class ModSyncServiceTests
{
    private sealed class Machine : IDisposable
    {
        public Machine(TimeProvider? catalogTime = null)
        {
            Root = new TempDirectory("modsync");
            SaveRoot = Root.CreateSubdirectory("save");
            InstallPath = Root.CreateSubdirectory("install");
            Directory.CreateDirectory(StreamingAssets);
            Detector = FakeGameDetector.NotRunning();
            Service = new ModSyncService(
                SaveRoot,
                InstallPath,
                Detector,
                store: new ModStateStore(Root.Resolve("modstate")),
                catalog: new ModListCatalog(Root.Resolve("modstate"), catalogTime));
        }

        public TempDirectory Root { get; }

        public string SaveRoot { get; }

        public string InstallPath { get; }

        public FakeGameDetector Detector { get; }

        public ModSyncService Service { get; }

        public string StreamingAssets => Path.Combine(InstallPath, "RainWorld_Data", "StreamingAssets");

        public string EnabledModsPath => Path.Combine(StreamingAssets, "enabledMods.txt");

        public string OptionsPath => Path.Combine(SaveRoot, "options");

        public void WriteOptions(string payload) => File.WriteAllBytes(OptionsPath, OptionsFixture.Bytes(payload));

        public void WriteEnabledMods(params string[] lines) => File.WriteAllLines(EnabledModsPath, lines);

        public void InstallMod(string id)
        {
            string folder = Path.Combine(StreamingAssets, "mods", id);
            Directory.CreateDirectory(folder);
            File.WriteAllText(
                Path.Combine(folder, "modinfo.json"),
                "{\"id\": \"" + id + "\", \"name\": \"" + id + "\", \"version\": \"1.0\"}");
        }

        public void Dispose() => Root.Dispose();
    }

    private sealed class CallbackTimeProvider : TimeProvider
    {
        public Action? OnRead { get; set; }

        public override DateTimeOffset GetUtcNow()
        {
            Action? action = OnRead;
            OnRead = null;
            action?.Invoke();
            return new DateTimeOffset(2026, 9, 4, 14, 31, 0, TimeSpan.Zero);
        }
    }

    [Fact]
    public void WhyNotNow_names_the_running_game()
    {
        using var machine = new Machine();
        machine.Detector.RunningProcessName = "RainWorld";

        string? reason = machine.Service.WhyNotNow();

        Assert.NotNull(reason);
        Assert.Contains("RainWorld", reason);
        Assert.Contains("running", reason);
    }

    [Fact]
    public void WhyNotNow_names_a_missing_install_path()
    {
        using var root = new TempDirectory("modsync");
        var service = new ModSyncService(
            root.Path,
            null,
            FakeGameDetector.NotRunning(),
            store: new ModStateStore(root.Resolve("modstate")));

        string? reason = service.WhyNotNow();

        Assert.NotNull(reason);
        Assert.Contains("game folder is not set", reason);
    }

    [Fact]
    public void WhyNotNow_names_an_install_that_does_not_look_like_one()
    {
        using var root = new TempDirectory("modsync");
        string notAnInstall = root.CreateSubdirectory("not-rain-world");
        var service = new ModSyncService(
            root.Path,
            notAnInstall,
            FakeGameDetector.NotRunning(),
            store: new ModStateStore(root.Resolve("modstate")));

        string? reason = service.WhyNotNow();

        Assert.NotNull(reason);
        Assert.Contains("does not look like a Rain World install", reason);
    }

    [Fact]
    public void WhyNotNow_names_a_missing_enabled_mods_file()
    {
        using var machine = new Machine();

        string? reason = machine.Service.WhyNotNow();

        Assert.NotNull(reason);
        Assert.Contains("enabledMods.txt", reason);
    }

    [Fact]
    public void WhyNotNow_returns_null_when_everything_is_in_place()
    {
        using var machine = new Machine();
        machine.WriteEnabledMods();
        machine.WriteOptions(OptionsFixture.Payload(OptionsFixture.Enabled()));

        Assert.Null(machine.Service.WhyNotNow());
    }

    [Fact]
    public void Apply_writes_both_the_options_file_and_the_enabled_mods_file()
    {
        using var machine = new Machine();
        machine.InstallMod("mod.a");
        machine.InstallMod("mod.b");
        machine.WriteEnabledMods("mod.a");
        machine.WriteOptions(OptionsFixture.Payload(
            OptionsFixture.Enabled("mod.a"),
            OptionsFixture.LoadOrder(("mod.a", "1")),
            OptionsFixture.Record("ScreenResolution", "1")));

        ModListSnapshot recorded = ModLists.Snapshot(null, ModLists.Mod("mod.b"));
        ModSyncResult result = machine.Service.Apply(machine.Service.BuildPlan(recorded));

        Assert.True(result.Applied);

        OptionsRead optionsRead = OptionsFile.Read(machine.SaveRoot);
        Assert.Contains("mod.b", optionsRead.EnabledModIds);
        Assert.DoesNotContain("mod.a", optionsRead.EnabledModIds);

        IReadOnlyList<string>? enabledLines = EnabledModsFile.Read(machine.InstallPath);
        Assert.NotNull(enabledLines);
        Assert.Contains("mod.b", enabledLines);
        Assert.DoesNotContain("mod.a", enabledLines);
    }

    [Fact]
    public void Apply_restores_an_imported_load_order_when_the_enabled_mods_already_match()
    {
        using var machine = new Machine();
        machine.InstallMod("mod.a");
        machine.InstallMod("mod.b");
        machine.WriteEnabledMods("mod.a", "mod.b");
        machine.WriteOptions(OptionsFixture.Payload(
            OptionsFixture.Enabled("mod.a", "mod.b"),
            OptionsFixture.LoadOrder(("mod.a", "0"), ("mod.b", "1"))));
        ModEntry first = ModLists.Mod("mod.a");
        first.LoadOrder = 1;
        ModEntry second = ModLists.Mod("mod.b");
        second.LoadOrder = 0;
        ModSyncPlan plan = machine.Service.BuildPlan(ModLists.Snapshot(null, first, second));

        ModSyncResult result = machine.Service.Apply(plan, "matching an imported mod list");

        Assert.True(result.Applied);
        OptionsRead options = OptionsFile.Read(machine.SaveRoot);
        Assert.Equal(1, options.LoadOrder["mod.a"]);
        Assert.Equal(0, options.LoadOrder["mod.b"]);
    }

    [Fact]
    public void Apply_refuses_while_the_game_is_running_and_leaves_both_files_untouched()
    {
        using var machine = new Machine();
        machine.WriteEnabledMods("mod.a");
        machine.WriteOptions(OptionsFixture.Payload(OptionsFixture.Enabled("mod.a")));

        byte[] optionsBefore = File.ReadAllBytes(machine.OptionsPath);
        byte[] enabledModsBefore = File.ReadAllBytes(machine.EnabledModsPath);
        machine.Detector.RunningProcessName = "RainWorld";

        ModSyncResult result = machine.Service.Apply(machine.Service.BuildPlan(null));

        Assert.False(result.Applied);
        Assert.Contains("running", result.Problem);
        Assert.Equal(optionsBefore, File.ReadAllBytes(machine.OptionsPath));
        Assert.Equal(enabledModsBefore, File.ReadAllBytes(machine.EnabledModsPath));
    }

    [Fact]
    public void Apply_captures_the_current_list_with_the_reason_before_changing_files()
    {
        using var machine = new Machine();
        machine.InstallMod("mod.a");
        machine.InstallMod("mod.b");
        machine.WriteEnabledMods("mod.a", "mod.b");
        machine.WriteOptions(OptionsFixture.Payload(
            OptionsFixture.Enabled("mod.a", "mod.b"),
            OptionsFixture.LoadOrder(("mod.a", "1"), ("mod.b", "0"))));

        ModListSnapshot recorded = ModLists.Snapshot(null, ModLists.Mod("mod.a"));
        ModSyncResult applied = machine.Service.Apply(
            machine.Service.BuildPlan(recorded),
            "Before applying imported list \"friends\"");
        Assert.True(applied.Applied);

        OptionsRead afterTurnOff = OptionsFile.Read(machine.SaveRoot);
        Assert.DoesNotContain("mod.b", afterTurnOff.EnabledModIds);

        ModListHistoryEntry entry = Assert.Single(machine.Service.ReadCatalog().History);
        Assert.Equal("Before applying imported list \"friends\"", entry.Reason);
        Assert.Equal(new[] { "mod.a", "mod.b" }, entry.Snapshot.Mods.Select(mod => mod.Id).OrderBy(id => id));
        Assert.Equal(1, entry.Snapshot.Mods.Single(mod => mod.Id == "mod.a").LoadOrder);
        Assert.Equal(0, entry.Snapshot.Mods.Single(mod => mod.Id == "mod.b").LoadOrder);
    }

    [Fact]
    public void A_no_op_creates_no_history()
    {
        using var machine = new Machine();
        machine.InstallMod("mod.a");
        machine.WriteEnabledMods("mod.a");
        machine.WriteOptions(OptionsFixture.Payload(OptionsFixture.Enabled("mod.a")));

        ModSyncResult result = machine.Service.Apply(machine.Service.BuildPlan(null));

        Assert.False(result.Applied);
        Assert.Null(result.Problem);
        Assert.Empty(machine.Service.ReadCatalog().History);
    }

    [Fact]
    public void Apply_refuses_when_history_cannot_be_written_and_keeps_both_live_files()
    {
        using var machine = new Machine();
        machine.InstallMod("mod.a");
        machine.InstallMod("mod.b");
        machine.WriteEnabledMods("mod.a");
        machine.WriteOptions(OptionsFixture.Payload(OptionsFixture.Enabled("mod.a")));
        Directory.CreateDirectory(machine.Service.Catalog.Root);
        File.WriteAllText(machine.Service.Catalog.HistoryRoot, "blocked");
        byte[] optionsBefore = File.ReadAllBytes(machine.OptionsPath);
        byte[] enabledModsBefore = File.ReadAllBytes(machine.EnabledModsPath);

        ModSyncResult result = machine.Service.Apply(
            machine.Service.BuildPlan(ModLists.Snapshot(null, ModLists.Mod("mod.b"))));

        Assert.False(result.Applied);
        Assert.Contains("saved for recovery", result.Problem);
        Assert.Equal(optionsBefore, File.ReadAllBytes(machine.OptionsPath));
        Assert.Equal(enabledModsBefore, File.ReadAllBytes(machine.EnabledModsPath));
    }

    [Fact]
    public void Apply_refuses_a_preview_when_the_live_mod_list_changed_since_it_was_built()
    {
        using var machine = new Machine();
        machine.InstallMod("mod.a");
        machine.InstallMod("mod.b");
        machine.WriteEnabledMods("mod.a");
        machine.WriteOptions(OptionsFixture.Payload(OptionsFixture.Enabled("mod.a")));
        ModSyncPlan plan = machine.Service.BuildPlan(ModLists.Snapshot(null, ModLists.Mod("mod.b")));
        machine.WriteEnabledMods("mod.a", "mod.b");
        machine.WriteOptions(OptionsFixture.Payload(OptionsFixture.Enabled("mod.a", "mod.b")));
        byte[] optionsBefore = File.ReadAllBytes(machine.OptionsPath);
        byte[] enabledModsBefore = File.ReadAllBytes(machine.EnabledModsPath);

        ModSyncResult result = machine.Service.Apply(plan);

        Assert.False(result.Applied);
        Assert.Contains("after this preview", result.Problem);
        Assert.Equal(optionsBefore, File.ReadAllBytes(machine.OptionsPath));
        Assert.Equal(enabledModsBefore, File.ReadAllBytes(machine.EnabledModsPath));
        Assert.Empty(machine.Service.ReadCatalog().History);
    }

    [Fact]
    public void Apply_rechecks_live_files_after_the_recovery_entry_is_written()
    {
        var clock = new CallbackTimeProvider();
        using var machine = new Machine(clock);
        machine.InstallMod("mod.a");
        machine.InstallMod("mod.b");
        machine.WriteEnabledMods("mod.a");
        machine.WriteOptions(OptionsFixture.Payload(OptionsFixture.Enabled("mod.a")));
        ModSyncPlan plan = machine.Service.BuildPlan(ModLists.Snapshot(null, ModLists.Mod("mod.b")));
        clock.OnRead = () =>
        {
            machine.WriteEnabledMods("mod.a", "mod.b");
            machine.WriteOptions(OptionsFixture.Payload(OptionsFixture.Enabled("mod.a", "mod.b")));
        };

        ModSyncResult result = machine.Service.Apply(plan);

        Assert.False(result.Applied);
        Assert.Contains("recovery entry", result.Problem);
        ModListHistoryEntry captured = Assert.Single(machine.Service.ReadCatalog().History);
        Assert.Equal("mod.a", Assert.Single(captured.Snapshot.Mods).Id);
        Assert.Contains("mod.b", OptionsFile.Read(machine.SaveRoot).EnabledModIds);
    }

    [Fact]
    public void Applying_a_history_preview_captures_the_list_it_replaces()
    {
        using var machine = new Machine();
        machine.InstallMod("mod.a");
        machine.InstallMod("mod.b");
        machine.WriteEnabledMods("mod.a");
        machine.WriteOptions(OptionsFixture.Payload(OptionsFixture.Enabled("mod.a")));
        machine.Service.Catalog.Execute(new AppendHistory(
            ModLists.Snapshot(null, ModLists.Mod("mod.b")),
            "Older list"));
        ModListHistoryEntry older = Assert.Single(machine.Service.ReadCatalog().History);

        ModSyncResult result = machine.Service.Apply(
            machine.Service.BuildPlan(older.Snapshot),
            "Before loading mod history from 4 Sep 14:31");

        Assert.True(result.Applied);
        ModListHistoryEntry newest = machine.Service.ReadCatalog().History.First();
        Assert.Equal("Before loading mod history from 4 Sep 14:31", newest.Reason);
        Assert.Equal("mod.a", Assert.Single(newest.Snapshot.Mods).Id);
    }

    [Fact]
    public void A_live_write_failure_keeps_the_new_history_entry_and_does_not_prune()
    {
        using var machine = new Machine();
        machine.InstallMod("mod.a");
        machine.InstallMod("mod.b");
        machine.WriteEnabledMods("mod.a");
        machine.WriteOptions(OptionsFixture.Payload(OptionsFixture.Enabled("mod.a")));
        for (int number = 0; number < ModListCatalog.HistoryLimit; number++)
        {
            machine.Service.Catalog.Execute(new AppendHistory(
                ModLists.Snapshot(null, ModLists.Mod("old." + number)),
                "Older " + number));
        }

        byte[] optionsBefore = File.ReadAllBytes(machine.OptionsPath);
        byte[] enabledModsBefore = File.ReadAllBytes(machine.EnabledModsPath);
        File.SetAttributes(machine.EnabledModsPath, File.GetAttributes(machine.EnabledModsPath) | FileAttributes.ReadOnly);
        ModSyncResult result;
        try
        {
            result = machine.Service.Apply(
                machine.Service.BuildPlan(ModLists.Snapshot(null, ModLists.Mod("mod.b"))));
        }
        finally
        {
            File.SetAttributes(machine.EnabledModsPath, FileAttributes.Normal);
        }

        Assert.False(result.Applied);
        Assert.Equal(optionsBefore, File.ReadAllBytes(machine.OptionsPath));
        Assert.Equal(enabledModsBefore, File.ReadAllBytes(machine.EnabledModsPath));
        Assert.Equal(11, machine.Service.ReadCatalog().History.Count);

        ModSyncResult retried = machine.Service.Apply(
            machine.Service.BuildPlan(ModLists.Snapshot(null, ModLists.Mod("mod.b"))));

        Assert.True(retried.Applied);
        Assert.Equal(ModListCatalog.HistoryLimit, machine.Service.ReadCatalog().History.Count);
    }

    [Fact]
    public void A_retention_failure_is_a_warning_after_the_live_change()
    {
        using var machine = new Machine();
        machine.InstallMod("mod.a");
        machine.InstallMod("mod.b");
        machine.WriteEnabledMods("mod.a");
        machine.WriteOptions(OptionsFixture.Payload(OptionsFixture.Enabled("mod.a")));
        for (int number = 0; number < ModListCatalog.HistoryLimit; number++)
        {
            machine.Service.Catalog.Execute(new AppendHistory(
                ModLists.Snapshot(null, ModLists.Mod("old." + number)),
                "Older " + number));
        }

        string oldest = Directory.EnumerateFiles(machine.Service.Catalog.HistoryRoot, "*.json")
            .OrderBy(path => path, StringComparer.Ordinal)
            .First();
        using var held = new FileStream(oldest, FileMode.Open, FileAccess.Read, FileShare.Read);

        ModSyncResult result = machine.Service.Apply(
            machine.Service.BuildPlan(ModLists.Snapshot(null, ModLists.Mod("mod.b"))));

        Assert.True(result.Applied);
        Assert.Contains("could not be cleaned up", result.Warning);
        Assert.Contains("mod.b", OptionsFile.Read(machine.SaveRoot).EnabledModIds);
        Assert.Equal(11, machine.Service.ReadCatalog().History.Count);
    }

    [Fact]
    public void A_corrupt_options_file_refuses_the_write_and_leaves_both_files_untouched()
    {
        using var machine = new Machine();
        machine.WriteEnabledMods();
        File.WriteAllBytes(machine.OptionsPath, new byte[] { 1, 2, 3, 4, 5 });

        byte[] optionsBefore = File.ReadAllBytes(machine.OptionsPath);
        byte[] enabledModsBefore = File.ReadAllBytes(machine.EnabledModsPath);

        ModSyncResult result = machine.Service.Apply(machine.Service.BuildPlan(null));

        Assert.False(result.Applied);
        Assert.Equal(optionsBefore, File.ReadAllBytes(machine.OptionsPath));
        Assert.Equal(enabledModsBefore, File.ReadAllBytes(machine.EnabledModsPath));
    }

    [Fact]
    public void No_staging_file_is_left_behind_after_a_success_or_a_refusal()
    {
        using var succeeding = new Machine();
        succeeding.InstallMod("mod.a");
        succeeding.InstallMod("mod.b");
        succeeding.WriteEnabledMods("mod.a");
        succeeding.WriteOptions(OptionsFixture.Payload(OptionsFixture.Enabled("mod.a")));

        ModSyncResult applied = succeeding.Service.Apply(
            succeeding.Service.BuildPlan(ModLists.Snapshot(null, ModLists.Mod("mod.b"))));

        Assert.True(applied.Applied);
        Assert.Empty(Directory.EnumerateFiles(succeeding.SaveRoot, "*.rwc-tmp"));
        Assert.Empty(Directory.EnumerateFiles(succeeding.StreamingAssets, "*.rwc-tmp"));

        using var refusing = new Machine();
        refusing.WriteEnabledMods("mod.a");
        refusing.WriteOptions(OptionsFixture.Payload(OptionsFixture.Enabled("mod.a")));
        refusing.Detector.RunningProcessName = "RainWorld";

        ModSyncResult refused = refusing.Service.Apply(refusing.Service.BuildPlan(null));

        Assert.False(refused.Applied);
        Assert.Empty(Directory.EnumerateFiles(refusing.SaveRoot, "*.rwc-tmp"));
        Assert.Empty(Directory.EnumerateFiles(refusing.StreamingAssets, "*.rwc-tmp"));
    }
}
