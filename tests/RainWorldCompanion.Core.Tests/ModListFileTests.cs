using RainWorldCompanion.Core.Mods;

namespace RainWorldCompanion.Tests;

public class ModListFileTests
{
    [Fact]
    public void A_mod_list_round_trips_its_shareable_fields()
    {
        using var files = new TempDirectory("mod-list-file");
        string path = files.Resolve("friends.rwmods");
        var original = new ModListSnapshot
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
                    FolderName = @"C:\not\portable",
                    Origin = ModEntry.WorkshopOrigin,
                    LoadOrder = 4,
                    Requirements = ["pom"],
                },
            ],
        };

        ModListFile.Write(path, original);
        ModListSnapshot imported = ModListFile.Read(path);

        ModEntry mod = Assert.Single(imported.Mods);
        Assert.Equal("v1.11.8", imported.GameVersion);
        Assert.True(imported.ReadTheEnabledList);
        Assert.Equal("slugbase", mod.Id);
        Assert.Equal("SlugBase", mod.Name);
        Assert.Equal("2.10", mod.Version);
        Assert.Equal("2933196558", mod.WorkshopId);
        Assert.Equal(4, mod.LoadOrder);
        Assert.Null(mod.FolderName);
        Assert.Empty(mod.Requirements);
    }

    [Fact]
    public void An_imported_array_order_becomes_the_load_order_when_positions_are_absent()
    {
        using var files = new TempDirectory("mod-list-order");
        string path = files.WriteText(
            "ordered.rwmods",
            """{"schemaVersion":1,"mods":[{"id":"b"},{"id":"a"}]}""");

        ModListSnapshot imported = ModListFile.Read(path);

        Assert.Equal(new int?[] { 0, 1 }, imported.Mods.Select(mod => mod.LoadOrder).ToArray());
    }

    [Fact]
    public void A_duplicate_id_is_refused_without_case_sensitivity()
    {
        using var files = new TempDirectory("mod-list-duplicate");
        string path = files.WriteText(
            "duplicate.rwmods",
            """{"schemaVersion":1,"mods":[{"id":"SlugBase"},{"id":"slugbase"}]}""");

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => ModListFile.Read(path));

        Assert.Contains("appears more than once", error.Message);
    }

    [Fact]
    public void A_newer_format_is_refused()
    {
        using var files = new TempDirectory("mod-list-newer");
        string path = files.WriteText(
            "future.rwmods",
            """{"schemaVersion":999,"mods":[]}""");

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => ModListFile.Read(path));

        Assert.Contains("newer version", error.Message);
    }
}
