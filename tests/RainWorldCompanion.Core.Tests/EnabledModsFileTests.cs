using RainWorldCompanion.Core.Mods;

namespace RainWorldCompanion.Tests;

public class EnabledModsFileTests
{
    private static ModEntry LocalMod(string id, string folderName)
        => new() { Id = id, Name = id, FolderName = folderName, Origin = ModEntry.InstallOrigin };

    private static ModEntry WorkshopMod(string id, string workshopId)
        => new() { Id = id, Name = id, WorkshopId = workshopId, Origin = ModEntry.WorkshopOrigin };

    [Fact]
    public void A_workshop_line_is_built_from_this_machines_workshop_folder_and_the_mods_id()
    {
        ModEntry mod = WorkshopMod("devourment", "3388224007");
        string workshopContentPath = @"C:\Program Files (x86)\Steam\steamapps\workshop\content\312520";

        string? line = EnabledModsFile.LineFor(mod, workshopContentPath);

        Assert.Equal(EnabledModsFile.WorkshopPrefix + Path.Combine(workshopContentPath, "3388224007"), line);
    }

    [Theory]
    [InlineData("moreslugcats", "moreslugcats")]
    [InlineData("devtools", "devtools")]
    [InlineData("SomeOtherFolder", "watcher")]
    public void A_builtin_mod_gets_no_line(string folderName, string id)
    {
        ModEntry mod = LocalMod(id, folderName);

        Assert.Null(EnabledModsFile.LineFor(mod, null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_workshop_mod_gets_no_line_when_the_workshop_path_is_unknown(string? workshopContentPath)
    {
        ModEntry mod = WorkshopMod("some.mod", "3388224007");

        Assert.Null(EnabledModsFile.LineFor(mod, workshopContentPath));
    }

    [Fact]
    public void A_mod_with_no_folder_name_gets_no_line()
    {
        ModEntry mod = ModLists.Mod("orphan");

        Assert.Null(EnabledModsFile.LineFor(mod, null));
    }

    [Fact]
    public void A_local_third_party_mod_still_gets_its_folder_as_a_line()
    {
        ModEntry mod = LocalMod("devourment", "Devourment-mod");

        Assert.Equal("Devourment-mod", EnabledModsFile.LineFor(mod, null));
    }

    [Theory]
    [InlineData(@"[WORKSHOP]C:\Program Files (x86)\Steam\steamapps\workshop\content\312520\3388224007")]
    [InlineData("[WORKSHOP]D:/SteamLibrary/steamapps/workshop/content/312520/3388224007")]
    [InlineData(@"[WORKSHOP]C:\Games\workshop\content\312520\3388224007\")]
    [InlineData(@"[workshop]C:\Games\workshop\content\312520\3388224007")]
    public void Names_matches_a_workshop_line_by_its_trailing_id_regardless_of_drive_separator_case_or_trailing_separator(string line)
    {
        ModEntry mod = WorkshopMod("devourment", "3388224007");

        Assert.True(EnabledModsFile.Names(line, mod));
    }

    [Fact]
    public void A_workshop_line_does_not_match_a_local_mod()
    {
        ModEntry mod = LocalMod("devourment", "Devourment-mod");
        string line = @"[WORKSHOP]C:\steam\workshop\content\312520\3388224007";

        Assert.False(EnabledModsFile.Names(line, mod));
    }

    [Fact]
    public void A_local_line_does_not_match_a_workshop_mod_even_with_the_same_name()
    {
        ModEntry mod = WorkshopMod("same", "99999");
        mod.FolderName = "Devourment-mod";
        string line = "Devourment-mod";

        Assert.False(EnabledModsFile.Names(line, mod));
    }

    [Fact]
    public void Rewrite_drops_the_turned_off_lines_and_leaves_the_rest_in_order()
    {
        var existingLines = new[]
        {
            @"[WORKSHOP]C:\ws\1111",
            "LocalModA",
            @"[WORKSHOP]C:\ws\2222",
            "LocalModB",
        };
        ModEntry[] turnOff = { WorkshopMod("off.workshop", "2222"), LocalMod("off.local", "LocalModA") };

        IReadOnlyList<string> result = EnabledModsFile.Rewrite(existingLines, Array.Empty<ModEntry>(), turnOff, null);

        Assert.Equal(new[] { @"[WORKSHOP]C:\ws\1111", "LocalModB" }, result);
    }

    [Fact]
    public void Rewrite_appends_a_new_line_but_does_not_duplicate_one_already_present()
    {
        var existingLines = new[] { "AlreadyThere" };
        ModEntry[] turnOn = { LocalMod("already", "AlreadyThere"), LocalMod("new.mod", "BrandNew") };

        IReadOnlyList<string> result = EnabledModsFile.Rewrite(existingLines, turnOn, Array.Empty<ModEntry>(), null);

        Assert.Equal(new[] { "AlreadyThere", "BrandNew" }, result);
    }

    [Fact]
    public void Rewrite_adds_no_line_for_a_builtin_being_turned_on()
    {
        ModEntry[] turnOn = { LocalMod("watcher", "watcher") };

        IReadOnlyList<string> result = EnabledModsFile.Rewrite(Array.Empty<string>(), turnOn, Array.Empty<ModEntry>(), null);

        Assert.Empty(result);
    }

    [Fact]
    public void A_line_with_no_matching_mod_is_left_alone()
    {
        var existingLines = new[] { "UnknownFolder", "LocalModA" };
        ModEntry[] turnOff = { LocalMod("off.local", "LocalModA") };

        IReadOnlyList<string> result = EnabledModsFile.Rewrite(existingLines, Array.Empty<ModEntry>(), turnOff, null);

        Assert.Equal(new[] { "UnknownFolder" }, result);
    }

    [Fact]
    public void Read_returns_null_when_the_file_is_missing()
    {
        using var directory = new TempDirectory();

        Assert.Null(EnabledModsFile.Read(directory.Path));
    }

    [Fact]
    public void Read_skips_blank_lines()
    {
        using var directory = new TempDirectory();
        directory.WriteText(EnabledModsFile.RelativePath, "LineOne\n\n   \nLineTwo\n");

        IReadOnlyList<string>? lines = EnabledModsFile.Read(directory.Path);

        Assert.Equal(new[] { "LineOne", "LineTwo" }, lines);
    }

    [Fact]
    public void A_mod_in_both_folders_takes_the_local_line_the_game_actually_loads()
    {
        var both = new ModEntry
        {
            Id = "devourment",
            Name = "Devourment",
            FolderName = "Devourment-mod",
            WorkshopId = "2929613038",
            Origin = ModEntry.InstallOrigin,
        };

        Assert.Equal("Devourment-mod", EnabledModsFile.LineFor(both, @"C:\steam\workshopÊ520"));
    }

    [Fact]
    public void A_mod_in_both_folders_still_matches_the_folder_line_it_already_has()
    {
        var both = new ModEntry
        {
            Id = "devourment",
            Name = "Devourment",
            FolderName = "Devourment-mod",
            WorkshopId = "2929613038",
            Origin = ModEntry.InstallOrigin,
        };

        Assert.True(EnabledModsFile.Names("Devourment-mod", both));

        IReadOnlyList<string> left = EnabledModsFile.Rewrite(
            new[] { "Devourment-mod", "other" },
            Array.Empty<ModEntry>(),
            new[] { both },
            @"C:\steam\workshopÊ520");

        Assert.Equal(new[] { "other" }, left);
    }

    [Fact]
    public void A_mod_in_both_folders_is_not_added_twice_when_it_is_turned_on()
    {
        var both = new ModEntry
        {
            Id = "devourment",
            Name = "Devourment",
            FolderName = "Devourment-mod",
            WorkshopId = "2929613038",
            Origin = ModEntry.InstallOrigin,
        };

        IReadOnlyList<string> lines = EnabledModsFile.Rewrite(
            new[] { "Devourment-mod" },
            new[] { both },
            Array.Empty<ModEntry>(),
            @"C:\steam\workshopÊ520");

        Assert.Equal(new[] { "Devourment-mod" }, lines);
    }
}
