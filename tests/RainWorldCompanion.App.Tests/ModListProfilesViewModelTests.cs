using System.IO;

using RainWorldCompanion.Core.Mods;
using RainWorldCompanion.ViewModels;

using IGameProcessDetector = RainWorldCompanion.Core.System.IGameProcessDetector;

namespace RainWorldCompanion.App.Tests;

public class ModListProfilesViewModelTests
{
    private sealed class NotRunning : IGameProcessDetector
    {
        public bool IsGameRunning(out string? processName)
        {
            processName = null;
            return false;
        }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class Machine : IDisposable
    {
        public Machine()
        {
            Root = new TempDirectory("mod-list-profiles");
            SaveRoot = Directory.CreateDirectory(Path.Combine(Root.Path, "save")).FullName;
            InstallPath = Directory.CreateDirectory(Path.Combine(Root.Path, "install")).FullName;
            Directory.CreateDirectory(StreamingAssets);
            File.WriteAllText(Path.Combine(StreamingAssets, "enabledMods.txt"), "");
            Catalog = new ModListCatalog(Path.Combine(Root.Path, "modstate"), Clock);
            Service = new ModSyncService(SaveRoot, InstallPath, new NotRunning(), catalog: Catalog);
        }

        public TempDirectory Root { get; }

        public string SaveRoot { get; }

        public string InstallPath { get; }

        public Clock Clock { get; } = new(new DateTimeOffset(2026, 9, 4, 14, 31, 0, TimeSpan.Zero));

        public ModListCatalog Catalog { get; }

        public ModSyncService Service { get; }

        private string StreamingAssets => Path.Combine(InstallPath, "RainWorld_Data", "StreamingAssets");

        public void Install(string id)
        {
            string folder = Directory.CreateDirectory(Path.Combine(StreamingAssets, "mods", id)).FullName;
            File.WriteAllText(
                Path.Combine(folder, "modinfo.json"),
                $$"""{"id":"{{id}}","name":"{{id}}","version":"1.0"}""");
        }

        public void SetLive(params string[] ids)
        {
            string fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "options.bin");
            byte[] options = File.ReadAllBytes(fixture);
            IReadOnlyDictionary<string, int> order = ids
                .Select((id, index) => (id, index))
                .ToDictionary(item => item.id, item => item.index, StringComparer.OrdinalIgnoreCase);

            File.WriteAllBytes(Path.Combine(SaveRoot, "options"), OptionsWriter.Rewrite(options, ids, order));
            File.WriteAllLines(Path.Combine(StreamingAssets, "enabledMods.txt"), ids);
        }

        public ModSyncViewModel Window() => new(Service);

        public void Dispose() => Root.Dispose();
    }

    [Fact]
    public void Import_offers_a_profile_without_creating_one()
    {
        using var machine = new Machine();
        machine.Install("slugbase");
        machine.SetLive();
        string path = Path.Combine(machine.Root.Path, "friends.rwmods");
        ModListFile.Write(path, Snapshot("slugbase"));
        ModSyncViewModel window = machine.Window();

        Assert.True(window.IsCurrentListSelected);

        window.ImportList(path);

        Assert.True(window.CanSaveImportedAsProfile);
        Assert.Equal("friends", window.SuggestedProfileName);
        Assert.Empty(window.Profiles);
        Assert.Empty(machine.Catalog.Read().Profiles);
        Assert.Contains("Imported 1 mod", window.ResultText);
    }

    [Fact]
    public void Loading_a_profile_switches_to_current_list_and_keeps_missing_and_order_changes_visible()
    {
        using var machine = new Machine();
        machine.Install("first");
        machine.Install("second");
        machine.SetLive("first", "second");
        machine.Catalog.Execute(new SaveProfile("Reordered", Snapshot("second", "first", "not-installed")));
        ModSyncViewModel window = machine.Window();
        ModListProfileViewModel profile = Assert.Single(window.Profiles);
        window.SelectedTabIndex = 1;

        window.LoadProfile(profile);

        Assert.True(window.IsCurrentListSelected);
        Assert.False(window.IsSavedListsSelected);
        Assert.Contains("Previewing saved list", window.SourceText);
        Assert.Contains(window.Missing, row => row.Id == "not-installed");
        Assert.All(window.Mods, row => Assert.Equal("will move in load order", row.StateText));
        Assert.Single(machine.Catalog.Read().Profiles);
        Assert.Empty(machine.Catalog.Read().History);
    }

    [Fact]
    public void Loading_history_only_changes_the_preview()
    {
        using var machine = new Machine();
        machine.Install("live");
        machine.Install("saved");
        machine.SetLive("live");
        machine.Catalog.Execute(new AppendHistory(Snapshot("saved"), "Before importing friends"));
        ModSyncViewModel window = machine.Window();
        ModListHistoryViewModel history = Assert.Single(window.History);
        window.SelectedTabIndex = 1;

        window.LoadHistory(history);

        Assert.True(window.IsCurrentListSelected);
        Assert.Equal("saved", Assert.Single(window.Mods, row => row.Wanted).Id);
        Assert.Single(machine.Catalog.Read().History);
        Assert.Contains("Before importing friends", Assert.Single(machine.Catalog.Read().History).Reason);
    }

    [Fact]
    public void Latest_history_card_uses_the_newest_entry()
    {
        using var machine = new Machine();
        machine.SetLive();
        machine.Catalog.Execute(new AppendHistory(Snapshot("first"), "First"));
        machine.Clock.Advance(TimeSpan.FromMinutes(1));
        machine.Catalog.Execute(new AppendHistory(Snapshot("second", "third"), "Newest"));
        ModSyncViewModel window = machine.Window();

        Assert.True(window.HasLatestHistory);
        Assert.StartsWith("Previous list: 2 mods", window.LatestHistoryText);
        Assert.Equal("Newest", Assert.Single(window.History, entry => entry.Snapshot.Mods.Count == 2).Reason);
    }

    [Fact]
    public void An_imported_list_can_be_applied_then_undone_from_history()
    {
        using var machine = new Machine();
        machine.Install("before");
        machine.Install("imported");
        machine.SetLive("before");
        string path = Path.Combine(machine.Root.Path, "friends.rwmods");
        ModListFile.Write(path, Snapshot("imported"));
        ModSyncViewModel window = machine.Window();

        window.ImportList(path);
        window.ApplyCommand.Execute(null);

        Assert.Contains("imported", OptionsFile.Read(machine.SaveRoot).EnabledModIds);
        ModListHistoryViewModel earlier = Assert.Single(window.History);
        Assert.Equal("before", Assert.Single(earlier.Snapshot.Mods).Id);
        Assert.Equal("Before applying imported list \"friends\"", earlier.Reason);

        window.LoadHistory(earlier);
        Assert.Contains("imported", OptionsFile.Read(machine.SaveRoot).EnabledModIds);
        window.ApplyCommand.Execute(null);

        Assert.Contains("before", OptionsFile.Read(machine.SaveRoot).EnabledModIds);
        ModListHistoryEntry recovery = machine.Catalog.Read().History.First();
        Assert.Equal("imported", Assert.Single(recovery.Snapshot.Mods).Id);
    }

    [Fact]
    public void Invalid_profile_names_stay_inline_and_leave_existing_profiles_unchanged()
    {
        using var machine = new Machine();
        machine.Install("slugbase");
        machine.SetLive("slugbase");
        ModSyncViewModel window = machine.Window();
        window.SaveCurrentProfile("Friends");

        window.SaveCurrentProfile("   ");
        Assert.Contains("at least", window.CatalogMessageText);
        Assert.Equal("Friends", Assert.Single(window.Profiles).Name);

        window.SaveCurrentProfile(new string('x', 81));
        Assert.Contains("at most", window.CatalogMessageText);
        Assert.Equal("Friends", Assert.Single(window.Profiles).Name);

        window.SaveCurrentProfile(" friends ");
        Assert.Contains("already uses", window.CatalogMessageText);
        Assert.Equal("Friends", Assert.Single(machine.Catalog.Read().Profiles).Name);
    }

    private static ModListSnapshot Snapshot(params string[] ids) => new()
    {
        ReadTheEnabledList = true,
        Mods = ids.Select((id, index) => new ModEntry
        {
            Id = id,
            Name = id,
            Version = "1.0",
            LoadOrder = index,
        }).ToList(),
    };
}
