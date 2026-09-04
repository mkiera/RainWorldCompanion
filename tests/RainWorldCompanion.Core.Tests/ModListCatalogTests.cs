using RainWorldCompanion.Core.Mods;

namespace RainWorldCompanion.Tests;

public class ModListCatalogTests
{
    [Fact]
    public void Profiles_can_be_created_renamed_replaced_and_deleted()
    {
        using var files = new TempDirectory("mod-list-catalog");
        var clock = new Clock("2026-09-04T14:31:00Z");
        var catalog = new ModListCatalog(files.Path, clock);

        ModListCatalogResult saved = catalog.Execute(new SaveProfile(" Co-op ", Snapshot("slugbase")));

        Assert.True(saved.Succeeded);
        Guid id = Assert.IsType<Guid>(saved.EntryId);
        ModListProfile profile = Assert.Single(saved.View.Profiles);
        Assert.Equal("Co-op", profile.Name);
        Assert.Equal("slugbase", Assert.Single(profile.Snapshot.Mods).Id);
        Assert.Equal(DateTimeOffset.Parse("2026-09-04T14:31:00Z"), profile.CreatedAt);
        Assert.Equal(profile.CreatedAt, profile.UpdatedAt);

        clock.Advance(TimeSpan.FromMinutes(1));
        ModListCatalogResult renamed = catalog.Execute(new RenameProfile(id, "Friends"));
        ModListCatalogResult replaced = catalog.Execute(new ReplaceProfile(id, Snapshot("dressmyslugcat", "slugbase")));

        Assert.True(renamed.Succeeded);
        Assert.True(replaced.Succeeded);
        profile = Assert.Single(replaced.View.Profiles);
        Assert.Equal("Friends", profile.Name);
        Assert.Equal(DateTimeOffset.Parse("2026-09-04T14:31:00Z"), profile.CreatedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-09-04T14:32:00Z"), profile.UpdatedAt);
        Assert.Equal(new[] { "dressmyslugcat", "slugbase" }, profile.Snapshot.Mods.Select(mod => mod.Id));

        ModListCatalogResult deleted = catalog.Execute(new DeleteProfile(id));

        Assert.True(deleted.Succeeded);
        Assert.Empty(deleted.View.Profiles);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_profile_names_are_refused(string name)
    {
        using var files = new TempDirectory("mod-list-catalog");
        var catalog = new ModListCatalog(files.Path);

        ModListCatalogResult result = catalog.Execute(new SaveProfile(name, Snapshot("slugbase")));

        Assert.False(result.Succeeded);
        Assert.Contains("at least", result.Problem);
        Assert.Empty(result.View.Profiles);
    }

    [Fact]
    public void Oversized_and_duplicate_profile_names_are_refused_without_changing_profiles()
    {
        using var files = new TempDirectory("mod-list-catalog");
        var catalog = new ModListCatalog(files.Path);
        catalog.Execute(new SaveProfile("Friends", Snapshot("slugbase")));

        ModListCatalogResult oversized = catalog.Execute(new SaveProfile(new string('x', 81), Snapshot("slugbase")));
        ModListCatalogResult duplicate = catalog.Execute(new SaveProfile(" friends ", Snapshot("dressmyslugcat")));

        Assert.False(oversized.Succeeded);
        Assert.False(duplicate.Succeeded);
        ModListProfile profile = Assert.Single(catalog.Read().Profiles);
        Assert.Equal("Friends", profile.Name);
        Assert.Equal("slugbase", Assert.Single(profile.Snapshot.Mods).Id);
    }

    [Fact]
    public void Profiles_are_read_in_alphabetical_order()
    {
        using var files = new TempDirectory("mod-list-catalog");
        var catalog = new ModListCatalog(files.Path);

        catalog.Execute(new SaveProfile("Zulu", Snapshot("z")));
        catalog.Execute(new SaveProfile("alpha", Snapshot("a")));
        catalog.Execute(new SaveProfile("Middle", Snapshot("m")));

        Assert.Equal(new[] { "alpha", "Middle", "Zulu" }, catalog.Read().Profiles.Select(profile => profile.Name));
    }

    [Fact]
    public void Profiles_keep_portable_mod_metadata_and_can_be_exported()
    {
        using var files = new TempDirectory("mod-list-catalog");
        var catalog = new ModListCatalog(files.Path);
        var snapshot = new ModListSnapshot
        {
            GameVersion = "v1.11.8",
            ReadTheEnabledList = true,
            Mods =
            [
                new ModEntry
                {
                    Id = "slugbase",
                    Name = "SlugBase",
                    Version = "2.10",
                    WorkshopId = "2933196558",
                    LoadOrder = 4,
                    FolderName = "local-only",
                    Origin = ModEntry.WorkshopOrigin,
                },
            ],
        };

        ModListCatalogResult result = catalog.Execute(new SaveProfile("Friends", snapshot));
        ModListProfile profile = Assert.Single(result.View.Profiles);
        string exportPath = files.Resolve("friends.rwmods");
        ModListFile.Write(exportPath, profile.Snapshot);
        ModListSnapshot exported = ModListFile.Read(exportPath);

        ModEntry mod = Assert.Single(exported.Mods);
        Assert.Equal("v1.11.8", exported.GameVersion);
        Assert.Equal("slugbase", mod.Id);
        Assert.Equal("SlugBase", mod.Name);
        Assert.Equal("2.10", mod.Version);
        Assert.Equal("2933196558", mod.WorkshopId);
        Assert.Equal(4, mod.LoadOrder);
    }

    [Fact]
    public void History_stays_at_ten_when_pruned_without_touching_profiles()
    {
        using var files = new TempDirectory("mod-list-catalog");
        var clock = new Clock("2026-09-04T14:31:00Z");
        var catalog = new ModListCatalog(files.Path, clock);
        catalog.Execute(new SaveProfile("Friends", Snapshot("profile")));

        for (int number = 0; number < 11; number++)
        {
            catalog.Execute(new AppendHistory(Snapshot("mod" + number), "Before test " + number));
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.Equal(11, catalog.Read().History.Count);
        ModListCatalogResult pruned = catalog.Execute(new PruneModListHistory());

        Assert.True(pruned.Succeeded);
        Assert.Equal(10, pruned.View.History.Count);
        Assert.Equal("mod10", Assert.Single(pruned.View.History[0].Snapshot.Mods).Id);
        Assert.Equal("mod1", Assert.Single(pruned.View.History[^1].Snapshot.Mods).Id);
        Assert.Equal("profile", Assert.Single(pruned.View.Profiles[0].Snapshot.Mods).Id);
    }

    [Fact]
    public void Pruning_keeps_the_latest_capture_when_the_clock_stalls_or_moves_back()
    {
        using var files = new TempDirectory("mod-list-catalog");
        var clock = new Clock("2026-09-04T14:31:00Z");
        var catalog = new ModListCatalog(files.Path, clock);
        for (int number = 0; number < ModListCatalog.HistoryLimit; number++)
        {
            catalog.Execute(new AppendHistory(Snapshot("old" + number), "Older " + number));
        }

        clock.Advance(TimeSpan.FromDays(-1));
        catalog.Execute(new AppendHistory(Snapshot("newest"), "Newest"));

        ModListCatalogResult pruned = catalog.Execute(new PruneModListHistory());

        Assert.Equal(ModListCatalog.HistoryLimit, pruned.View.History.Count);
        Assert.Equal("newest", Assert.Single(pruned.View.History[0].Snapshot.Mods).Id);
        Assert.DoesNotContain(
            pruned.View.History,
            entry => entry.Snapshot.Mods.Any(mod => mod.Id == "old0"));
    }

    [Fact]
    public void Corrupt_entries_are_skipped_without_hiding_valid_entries()
    {
        using var files = new TempDirectory("mod-list-catalog");
        var catalog = new ModListCatalog(files.Path);
        catalog.Execute(new SaveProfile("Friends", Snapshot("slugbase")));
        catalog.Execute(new AppendHistory(Snapshot("dressmyslugcat"), "Before import"));
        files.WriteText("profiles/broken.json", "{");
        files.WriteText("history/broken.json", "{");
        files.WriteText("profiles/future.json", "{\"schemaVersion\":999}");
        files.WriteText("history/future.json", "{\"schemaVersion\":999}");

        ModListCatalogView view = catalog.Read();

        Assert.Single(view.Profiles);
        Assert.Single(view.History);
        Assert.Equal(2, view.UnreadableProfileCount);
        Assert.Equal(2, view.UnreadableHistoryCount);
    }

    [Fact]
    public void Previous_json_is_visible_then_migrates_once_on_the_first_catalog_write()
    {
        using var files = new TempDirectory("mod-list-catalog");
        var legacyStore = new ModStateStore(files.Path);
        var legacyPoint = new ModStateRestorePoint
        {
            TakenAt = DateTimeOffset.Parse("2026-09-04T14:31:00Z"),
            Because = "Before imported list",
            Mods = Snapshot("legacy"),
        };
        legacyStore.Write(legacyPoint);
        var catalog = new ModListCatalog(files.Path, new Clock("2026-09-04T15:00:00Z"));

        ModListHistoryEntry virtualEntry = Assert.Single(catalog.Read().History);
        ModListCatalogResult saved = catalog.Execute(new SaveProfile("Friends", Snapshot("slugbase")));
        ModListCatalogView migrated = catalog.Read();

        Assert.True(virtualEntry.IsLegacy);
        Assert.True(saved.Succeeded);
        Assert.False(File.Exists(legacyStore.FilePath));
        ModListHistoryEntry entry = Assert.Single(migrated.History);
        Assert.False(entry.IsLegacy);
        Assert.Equal(virtualEntry.Id, entry.Id);
        Assert.Equal("legacy", Assert.Single(entry.Snapshot.Mods).Id);

        legacyStore.Write(legacyPoint);
        Assert.Single(catalog.Read().History);
        catalog.Execute(new AppendHistory(Snapshot("new"), "Before next"));

        Assert.False(File.Exists(legacyStore.FilePath));
        Assert.Equal(2, catalog.Read().History.Count);
    }

    [Fact]
    public void Interrupted_temporary_files_are_ignored()
    {
        using var files = new TempDirectory("mod-list-catalog");
        files.WriteText("profiles/unfinished.tmp", "{");
        files.WriteText("history/unfinished.tmp", "{");
        var catalog = new ModListCatalog(files.Path);

        ModListCatalogResult result = catalog.Execute(new SaveProfile("Friends", Snapshot("slugbase")));

        Assert.True(result.Succeeded);
        Assert.Single(result.View.Profiles);
        Assert.Equal(0, result.View.UnreadableEntryCount);
    }

    private static ModListSnapshot Snapshot(params string[] ids)
    {
        return new ModListSnapshot
        {
            GameVersion = "v1.11.8",
            ReadTheEnabledList = true,
            Mods = ids.Select((id, index) => new ModEntry
            {
                Id = id,
                Name = id,
                LoadOrder = index,
            }).ToList(),
        };
    }

    private sealed class Clock : TimeProvider
    {
        public Clock(string now)
        {
            Now = DateTimeOffset.Parse(now);
        }

        public DateTimeOffset Now { get; private set; }

        public override DateTimeOffset GetUtcNow() => Now;

        public void Advance(TimeSpan elapsed) => Now += elapsed;
    }
}
