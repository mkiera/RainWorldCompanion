using System.IO;
using RainWorldCompanion.Core.Mods;
using RainWorldCompanion.ViewModels;

// Aliased rather than imported: RainWorldCompanion.Core.System would shadow System.IO here, and
// every Path and Directory below would stop resolving.
using IGameProcessDetector = RainWorldCompanion.Core.System.IGameProcessDetector;

namespace RainWorldCompanion.App.Tests;

/// <summary>
/// Ticking a mod on in the Mods window turns on what it needs, the way the game's own Remix menu
/// does. Turning one off is left alone: that direction would take mods away from a player who did
/// not ask for it.
/// </summary>
public class ModDependencyTests
{
    private sealed class NotRunning : IGameProcessDetector
    {
        public bool IsGameRunning(out string? processName)
        {
            processName = null;
            return false;
        }
    }

    private sealed class Machine : IDisposable
    {
        public Machine()
        {
            Root = new TempDirectory("moddeps");
            SaveRoot = Directory.CreateDirectory(Path.Combine(Root.Path, "save")).FullName;
            InstallPath = Directory.CreateDirectory(Path.Combine(Root.Path, "install")).FullName;
            Directory.CreateDirectory(StreamingAssets);

            Service = new ModSyncService(
                SaveRoot,
                InstallPath,
                new NotRunning(),
                store: new ModStateStore(Path.Combine(Root.Path, "modstate")));
        }

        public TempDirectory Root { get; }

        public string SaveRoot { get; }

        public string InstallPath { get; }

        public ModSyncService Service { get; }

        public string StreamingAssets => Path.Combine(InstallPath, "RainWorld_Data", "StreamingAssets");

        /// <param name="requires">The ids this mod's modinfo.json names in its requirements array.</param>
        public void InstallMod(string id, params string[] requires)
        {
            string folder = Directory.CreateDirectory(Path.Combine(StreamingAssets, "mods", id)).FullName;
            string list = string.Join(", ", requires.Select(required => "\"" + required + "\""));

            File.WriteAllText(
                Path.Combine(folder, "modinfo.json"),
                $$"""{"id": "{{id}}", "name": "{{id}}", "version": "1.0", "requirements": [{{list}}]}""");
        }

        public ModSyncViewModel Window() => new(Service);

        public void Dispose() => Root.Dispose();
    }

    private static ModSyncRowViewModel Row(ModSyncViewModel window, string id)
        => window.Mods.Single(row => string.Equals(row.Id, id, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Turning_a_mod_on_turns_on_what_it_needs()
    {
        using var machine = new Machine();
        machine.InstallMod("pearlcat", "slugbase");
        machine.InstallMod("slugbase");

        var window = machine.Window();
        Row(window, "pearlcat").Wanted = true;

        Assert.True(Row(window, "slugbase").Wanted);
    }

    [Fact]
    public void A_requirement_of_a_requirement_comes_on_too()
    {
        using var machine = new Machine();
        machine.InstallMod("shelters", "regionkit");
        machine.InstallMod("regionkit", "pom");
        machine.InstallMod("pom");

        var window = machine.Window();
        Row(window, "shelters").Wanted = true;

        Assert.True(Row(window, "regionkit").Wanted);
        Assert.True(Row(window, "pom").Wanted);
    }

    [Fact]
    public void A_mod_nothing_needed_is_left_alone()
    {
        using var machine = new Machine();
        machine.InstallMod("pearlcat", "slugbase");
        machine.InstallMod("slugbase");
        machine.InstallMod("unrelated");

        var window = machine.Window();
        Row(window, "pearlcat").Wanted = true;

        Assert.False(Row(window, "unrelated").Wanted);
    }

    /// <summary>
    /// The direction deliberately not taken. Turning a requirement off would otherwise take the
    /// mods that need it off with it, which is not what the tick said.
    /// </summary>
    [Fact]
    public void Turning_a_mod_off_takes_nothing_with_it()
    {
        using var machine = new Machine();
        machine.InstallMod("pearlcat", "slugbase");
        machine.InstallMod("slugbase");

        var window = machine.Window();
        Row(window, "pearlcat").Wanted = true;

        Row(window, "slugbase").Wanted = false;

        Assert.True(Row(window, "pearlcat").Wanted);
    }

    [Fact]
    public void Two_mods_naming_each_other_do_not_hang_the_window()
    {
        using var machine = new Machine();
        machine.InstallMod("a", "b");
        machine.InstallMod("b", "a");

        var window = machine.Window();
        Row(window, "a").Wanted = true;

        Assert.True(Row(window, "b").Wanted);
    }

    [Fact]
    public void What_was_turned_on_alongside_is_said()
    {
        using var machine = new Machine();
        machine.InstallMod("pearlcat", "slugbase");
        machine.InstallMod("slugbase");

        var window = machine.Window();
        Row(window, "pearlcat").Wanted = true;

        Assert.Contains("slugbase", window.ResultText);
        Assert.Contains("pearlcat", window.ResultText);
    }

    /// <summary>
    /// A requirement nothing on this machine provides cannot be turned on, and saying nothing
    /// would leave the reason the mod may still not work unsaid.
    /// </summary>
    [Fact]
    public void A_requirement_that_is_not_installed_is_named_rather_than_ignored()
    {
        using var machine = new Machine();
        machine.InstallMod("pearlcat", "slugbase");

        var window = machine.Window();
        Row(window, "pearlcat").Wanted = true;

        Assert.Contains("slugbase", window.ResultText);
        Assert.Contains("not installed", window.ResultText);
    }

    [Fact]
    public void A_mod_needing_nothing_says_nothing()
    {
        using var machine = new Machine();
        machine.InstallMod("healthbars");

        var window = machine.Window();
        Row(window, "healthbars").Wanted = true;

        Assert.Equal("", window.ResultText);
    }

    [Fact]
    public void A_requirement_already_on_is_not_reported_as_turned_on()
    {
        using var machine = new Machine();
        machine.InstallMod("pearlcat", "slugbase");
        machine.InstallMod("slugbase");

        var window = machine.Window();

        // Turned on by hand first, and slugbase needs nothing, so nothing is said about it.
        Row(window, "slugbase").Wanted = true;
        Assert.Equal("", window.ResultText);

        Row(window, "pearlcat").Wanted = true;

        Assert.True(Row(window, "slugbase").Wanted);
        Assert.Equal("", window.ResultText);
    }
}
