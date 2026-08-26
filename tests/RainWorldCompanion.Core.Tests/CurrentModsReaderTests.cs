using System.Text;

using RainWorldCompanion.Core.Mods;

namespace RainWorldCompanion.Tests;

/// <summary>
/// Reading what is on this machine: which mods the game has turned on, what is installed, and
/// which game they are for.
/// </summary>
public class CurrentModsReaderTests
{
    /// <summary>
    /// A save folder plus a game install laid out the way Steam lays one out, so the workshop
    /// folder is found by walking up rather than by being told where it is.
    /// </summary>
    private sealed class Machine : IDisposable
    {
        public Machine()
        {
            Directory = new TempDirectory("mods");
            SaveRoot = Directory.CreateSubdirectory("save");
            InstallPath = Directory.CreateSubdirectory(@"lib/steamapps/common/Rain World");
            ModsPath = Directory.CreateSubdirectory(@"lib/steamapps/common/Rain World/RainWorld_Data/StreamingAssets/mods");
            WorkshopPath = Directory.CreateSubdirectory(@"lib/steamapps/workshop/content/312520");
        }

        public TempDirectory Directory { get; }

        public string SaveRoot { get; }

        public string InstallPath { get; }

        public string ModsPath { get; }

        public string WorkshopPath { get; }

        /// <summary>Writes an options file naming what is on, with a load order to match.</summary>
        public void TurnOn(params string[] ids)
        {
            OptionsFixture.WriteInto(
                Directory,
                OptionsFixture.Payload(
                    OptionsFixture.Enabled(ids),
                    OptionsFixture.LoadOrder(ids.Select((id, i) => (id, i.ToString())).ToArray()),
                    OptionsFixture.Record("LastGameVersion", "v1.11.8")),
                "save/options");
        }

        public void WriteOptions(string payload) => OptionsFixture.WriteInto(Directory, payload, "save/options");

        /// <summary>A mod in the game's own mods folder.</summary>
        public void InstallMod(string folderName, string? json)
            => WriteMod(Path.Combine(ModsPath, folderName), json);

        /// <summary>A mod in the workshop content folder, whose folder name is its item id.</summary>
        public void WorkshopMod(string workshopId, string? json)
            => WriteMod(Path.Combine(WorkshopPath, workshopId), json);

        public void WriteGameVersion(string text)
        {
            string path = Path.Combine(InstallPath, @"RainWorld_Data\StreamingAssets\GameVersion.txt");
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        public CurrentMods Read() => CurrentModsReader.Read(SaveRoot, InstallPath);

        private static void WriteMod(string folder, string? json)
        {
            System.IO.Directory.CreateDirectory(folder);
            if (json is not null)
            {
                File.WriteAllText(Path.Combine(folder, "modinfo.json"), json);
            }
        }

        public void Dispose() => Directory.Dispose();
    }

    private static string Info(string id, string? version = null, string? name = null)
    {
        var parts = new List<string> { $"\"id\": \"{id}\"" };
        if (name is not null)
        {
            parts.Add($"\"name\": \"{name}\"");
        }

        if (version is not null)
        {
            parts.Add($"\"version\": \"{version}\"");
        }

        return "{" + string.Join(", ", parts) + "}";
    }

    // ---- matching what is on to what is installed ----

    /// <summary>
    /// The folder a mod sits in and the id the game knows it by are different things: Devourment
    /// ships in a folder called Devourment-mod. Matching on the folder name would find nothing.
    /// </summary>
    [Fact]
    public void Matches_by_mod_id_rather_than_folder_name()
    {
        using var machine = new Machine();
        machine.InstallMod("Devourment-mod", Info("devourment", "0.1.11-ea", "Devourment"));
        machine.TurnOn("devourment");

        ModEntry mod = Assert.Single(machine.Read().Enabled.Mods);

        Assert.Equal("devourment", mod.Id);
        Assert.Equal("Devourment", mod.Name);
        Assert.Equal("0.1.11-ea", mod.Version);
        Assert.Equal(ModEntry.InstallOrigin, mod.Origin);
    }

    /// <summary>The workshop folder name is the item id, and the only place it is written down.</summary>
    [Fact]
    public void Records_the_workshop_item_id_from_the_folder_name()
    {
        using var machine = new Machine();
        machine.WorkshopMod("2923374705", Info("MapOptions", "2.3.3"));
        machine.TurnOn("MapOptions");

        ModEntry mod = Assert.Single(machine.Read().Enabled.Mods);

        Assert.Equal("2923374705", mod.WorkshopId);
        Assert.Equal(ModEntry.WorkshopOrigin, mod.Origin);
    }

    /// <summary>
    /// A mod the game has on but that is nowhere on disk is recorded as an id and nothing more,
    /// rather than dropped. The game named it, so it belongs in the list.
    /// </summary>
    [Fact]
    public void A_mod_that_is_on_but_not_installed_is_kept_as_an_id()
    {
        using var machine = new Machine();
        machine.TurnOn("gone.missing");

        ModEntry mod = Assert.Single(machine.Read().Enabled.Mods);

        Assert.Equal("gone.missing", mod.Id);
        Assert.Equal("gone.missing", mod.Name);
        Assert.Null(mod.Version);
        Assert.Equal("", mod.Origin);
    }

    /// <summary>An installed mod that is turned off belongs in Installed and nowhere else.</summary>
    [Fact]
    public void An_installed_mod_that_is_off_is_listed_as_installed_only()
    {
        using var machine = new Machine();
        machine.InstallMod("on", Info("on"));
        machine.InstallMod("off", Info("off"));
        machine.TurnOn("on");

        CurrentMods read = machine.Read();

        Assert.Equal(new[] { "on" }, read.Enabled.Mods.Select(mod => mod.Id));
        Assert.Equal(new[] { "off", "on" }, read.Installed.Select(mod => mod.Id));
    }

    // ---- reading a mod folder ----

    /// <summary>The folder holds dlcversions.json and is not a mod. The game skips it by name.</summary>
    [Fact]
    public void The_versioning_folder_is_not_a_mod()
    {
        using var machine = new Machine();
        machine.InstallMod("versioning", null);
        machine.TurnOn();

        Assert.Empty(machine.Read().Installed);
    }

    [Fact]
    public void A_mod_with_no_modinfo_falls_back_to_its_folder_name()
    {
        using var machine = new Machine();
        machine.InstallMod("barefolder", null);
        machine.TurnOn("barefolder");

        ModEntry mod = Assert.Single(machine.Read().Enabled.Mods);

        Assert.Equal("barefolder", mod.Id);
        Assert.Equal("barefolder", mod.Name);
    }

    [Fact]
    public void A_modinfo_that_will_not_parse_falls_back_to_its_folder_name()
    {
        using var machine = new Machine();
        machine.InstallMod("brokenjson", "{ this is not json");
        machine.TurnOn("brokenjson");

        Assert.Equal("brokenjson", Assert.Single(machine.Read().Enabled.Mods).Id);
    }

    /// <summary>devtools ships without a version, so a null here has to survive as a null.</summary>
    [Fact]
    public void A_mod_with_no_version_keeps_a_null_version()
    {
        using var machine = new Machine();
        machine.InstallMod("devtools", Info("devtools"));
        machine.TurnOn("devtools");

        Assert.Null(Assert.Single(machine.Read().Enabled.Mods).Version);
    }

    [Fact]
    public void A_modinfo_with_a_byte_order_mark_still_parses()
    {
        using var machine = new Machine();
        string folder = Path.Combine(machine.ModsPath, "withbom");
        Directory.CreateDirectory(folder);
        File.WriteAllText(
            Path.Combine(folder, "modinfo.json"),
            Info("withbom", "2.0"),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        machine.TurnOn("withbom");

        Assert.Equal("2.0", Assert.Single(machine.Read().Enabled.Mods).Version);
    }

    [Fact]
    public void A_modinfo_with_no_name_falls_back_to_its_id()
    {
        using var machine = new Machine();
        machine.InstallMod("folder", Info("the.id", "1.0"));
        machine.TurnOn("the.id");

        ModEntry mod = Assert.Single(machine.Read().Enabled.Mods);

        Assert.Equal("the.id", mod.Id);
        Assert.Equal("the.id", mod.Name);
    }

    /// <summary>
    /// The same mod in both places keeps the install copy, which is the one the game loads, and
    /// takes the workshop id off the other so a Steam link still exists.
    /// </summary>
    [Fact]
    public void A_mod_in_both_places_keeps_the_install_copy_and_the_workshop_id()
    {
        using var machine = new Machine();
        machine.InstallMod("local", Info("both", "1.0-local"));
        machine.WorkshopMod("55555", Info("both", "2.0-workshop"));
        machine.TurnOn("both");

        ModEntry mod = Assert.Single(machine.Read().Enabled.Mods);

        Assert.Equal("1.0-local", mod.Version);
        Assert.Equal(ModEntry.InstallOrigin, mod.Origin);
        Assert.Equal("55555", mod.WorkshopId);
    }

    // ---- order ----

    [Fact]
    public void Mods_are_listed_in_the_order_the_game_loads_them()
    {
        using var machine = new Machine();
        machine.WriteOptions(OptionsFixture.Payload(
            OptionsFixture.Enabled("third", "first", "second"),
            OptionsFixture.LoadOrder(("first", "1"), ("second", "5"), ("third", "9"))));

        Assert.Equal(
            new[] { "first", "second", "third" },
            machine.Read().Enabled.Mods.Select(mod => mod.Id));
    }

    /// <summary>
    /// A mod with no recorded position goes last. Sorting it as a zero would put it in front of
    /// everything, which is the opposite of what is known about it.
    /// </summary>
    [Fact]
    public void A_mod_with_no_recorded_position_is_listed_last()
    {
        using var machine = new Machine();
        machine.WriteOptions(OptionsFixture.Payload(
            OptionsFixture.Enabled("unplaced", "placed"),
            OptionsFixture.LoadOrder(("placed", "7"))));

        List<ModEntry> mods = machine.Read().Enabled.Mods;

        Assert.Equal(new[] { "placed", "unplaced" }, mods.Select(mod => mod.Id));
        Assert.Null(mods[1].LoadOrder);
    }

    /// <summary>The game leaves positions behind for mods it has since turned off.</summary>
    [Fact]
    public void Load_order_entries_for_mods_that_are_off_are_left_out()
    {
        using var machine = new Machine();
        machine.WriteOptions(OptionsFixture.Payload(
            OptionsFixture.Enabled("on"),
            OptionsFixture.LoadOrder(("on", "2"), ("off", "3"))));

        Assert.Equal(new[] { "on" }, machine.Read().Enabled.Mods.Select(mod => mod.Id));
    }

    // ---- what was and was not looked at ----

    [Fact]
    public void A_full_read_says_it_looked_everywhere()
    {
        using var machine = new Machine();
        machine.TurnOn();

        ModListSnapshot enabled = machine.Read().Enabled;

        Assert.True(enabled.ReadTheEnabledList);
        Assert.True(enabled.CheckedTheInstall);
        Assert.True(enabled.CheckedTheWorkshop);
        Assert.Null(enabled.Note);
    }

    /// <summary>
    /// Without the game folder the ids and the load order are still worth having. Only the names
    /// and versions are missing, and the flags say which.
    /// </summary>
    [Fact]
    public void No_game_folder_still_reads_the_ids_and_says_what_was_missed()
    {
        using var machine = new Machine();
        machine.TurnOn("some.mod");

        CurrentMods read = CurrentModsReader.Read(machine.SaveRoot, null);

        Assert.True(read.Enabled.ReadTheEnabledList);
        Assert.False(read.Enabled.CheckedTheInstall);
        Assert.False(read.Enabled.CheckedTheWorkshop);
        Assert.NotNull(read.Enabled.Note);
        Assert.Equal("some.mod", Assert.Single(read.Enabled.Mods).Id);
        Assert.Empty(read.Installed);
    }

    /// <summary>An unreadable options file must not read as "no mods were on".</summary>
    [Fact]
    public void No_options_file_still_scans_the_install()
    {
        using var machine = new Machine();
        machine.InstallMod("there", Info("there", "1.0"));

        CurrentMods read = machine.Read();

        Assert.False(read.Enabled.ReadTheEnabledList);
        Assert.NotNull(read.Enabled.Note);
        Assert.Empty(read.Enabled.Mods);
        Assert.Equal("there", Assert.Single(read.Installed).Id);
    }

    /// <summary>An install that is not under a steamapps folder has no workshop folder to find.</summary>
    [Fact]
    public void An_install_outside_a_steam_library_has_no_workshop_folder()
    {
        using var directory = new TempDirectory();
        string install = directory.CreateSubdirectory("Rain World");
        Directory.CreateDirectory(Path.Combine(install, @"RainWorld_Data\StreamingAssets\mods"));

        Assert.Null(CurrentModsReader.WorkshopContentPath(install));
        Assert.False(CurrentModsReader.Read(directory.Path, install).Enabled.CheckedTheWorkshop);
    }

    [Fact]
    public void The_workshop_folder_is_found_beside_the_library_the_game_is_in()
    {
        using var machine = new Machine();

        Assert.Equal(machine.WorkshopPath, CurrentModsReader.WorkshopContentPath(machine.InstallPath));
    }

    // ---- the game version ----

    /// <summary>
    /// The options file names the game it was last written under, which is the game the player
    /// actually runs. GameVersion.txt is the fallback for when that could not be read.
    /// </summary>
    [Fact]
    public void The_game_version_comes_from_the_options_file_first()
    {
        using var machine = new Machine();
        machine.WriteGameVersion("v1.9.15");
        machine.TurnOn();

        Assert.Equal("v1.11.8", machine.Read().Enabled.GameVersion);
    }

    [Fact]
    public void The_game_version_falls_back_to_the_install()
    {
        using var machine = new Machine();
        machine.WriteGameVersion("v1.9.15\n");
        machine.WriteOptions(OptionsFixture.Payload(OptionsFixture.Enabled("one")));

        Assert.Equal("v1.9.15", machine.Read().Enabled.GameVersion);
    }

    [Fact]
    public void No_game_version_anywhere_reads_as_null()
    {
        using var machine = new Machine();
        machine.WriteOptions(OptionsFixture.Payload(OptionsFixture.Enabled("one")));

        Assert.Null(machine.Read().Enabled.GameVersion);
    }

    // ---- never throws ----

    [Fact]
    public void Nothing_on_this_machine_reads_as_nothing_rather_than_throwing()
    {
        CurrentMods read = CurrentModsReader.Read(null, null);

        Assert.False(read.Enabled.ReadTheEnabledList);
        Assert.False(read.Enabled.CheckedTheInstall);
        Assert.Empty(read.Enabled.Mods);
        Assert.Empty(read.Installed);
    }

    [Fact]
    public void Paths_that_do_not_exist_read_as_nothing_rather_than_throwing()
    {
        CurrentMods read = CurrentModsReader.Read(@"Z:\no\save\folder", @"Z:\no\game\folder");

        Assert.False(read.Enabled.ReadTheEnabledList);
        Assert.Empty(read.Installed);
    }
}
