using RainWorldCompanion.Core.Mods;

namespace RainWorldCompanion.Tests;

public class ModSyncServiceTests
{
    private sealed class Machine : IDisposable
    {
        public Machine()
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
                store: new ModStateStore(Root.Resolve("modstate")));
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
    public void Apply_writes_a_restore_point_and_RestorePrevious_puts_the_original_list_back()
    {
        using var machine = new Machine();
        machine.InstallMod("mod.a");
        machine.InstallMod("mod.b");
        machine.WriteEnabledMods("mod.a", "mod.b");
        machine.WriteOptions(OptionsFixture.Payload(OptionsFixture.Enabled("mod.a", "mod.b")));

        ModListSnapshot recorded = ModLists.Snapshot(null, ModLists.Mod("mod.a"));
        ModSyncResult applied = machine.Service.Apply(machine.Service.BuildPlan(recorded));
        Assert.True(applied.Applied);

        OptionsRead afterTurnOff = OptionsFile.Read(machine.SaveRoot);
        Assert.DoesNotContain("mod.b", afterTurnOff.EnabledModIds);

        ModStateRestorePoint? point = machine.Service.ReadRestorePoint();
        Assert.NotNull(point);
        Assert.True(point!.UsableForRestore);

        ModListSnapshot snapshot = point.Mods!;
        Assert.Equal(new[] { "mod.a", "mod.b" }, snapshot.Mods.Select(mod => mod.Id).OrderBy(id => id));

        ModSyncResult restored = machine.Service.RestorePrevious();

        Assert.True(restored.Applied);
        OptionsRead afterRestore = OptionsFile.Read(machine.SaveRoot);
        Assert.Contains("mod.b", afterRestore.EnabledModIds);

        IReadOnlyList<string>? linesAfterRestore = EnabledModsFile.Read(machine.InstallPath);
        Assert.NotNull(linesAfterRestore);
        Assert.Contains("mod.b", linesAfterRestore);
    }

    [Fact]
    public void RestorePrevious_refuses_when_there_is_no_restore_point()
    {
        using var machine = new Machine();
        machine.WriteEnabledMods();
        machine.WriteOptions(OptionsFixture.Payload(OptionsFixture.Enabled()));

        ModSyncResult result = machine.Service.RestorePrevious();

        Assert.False(result.Applied);
        Assert.Contains("no earlier mod list", result.Problem);
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
        succeeding.WriteEnabledMods("mod.a");
        succeeding.WriteOptions(OptionsFixture.Payload(OptionsFixture.Enabled("mod.a")));

        ModSyncResult applied = succeeding.Service.Apply(succeeding.Service.BuildPlan(null));

        Assert.True(applied.Applied);
        Assert.False(File.Exists(succeeding.OptionsPath + ".rwc-tmp"));
        Assert.False(File.Exists(succeeding.EnabledModsPath + ".rwc-tmp"));

        using var refusing = new Machine();
        refusing.WriteEnabledMods("mod.a");
        refusing.WriteOptions(OptionsFixture.Payload(OptionsFixture.Enabled("mod.a")));
        refusing.Detector.RunningProcessName = "RainWorld";

        ModSyncResult refused = refusing.Service.Apply(refusing.Service.BuildPlan(null));

        Assert.False(refused.Applied);
        Assert.False(File.Exists(refusing.OptionsPath + ".rwc-tmp"));
        Assert.False(File.Exists(refusing.EnabledModsPath + ".rwc-tmp"));
    }
}
