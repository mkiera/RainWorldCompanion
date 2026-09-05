using RainWorldCompanion.Core.Mods;

namespace RainWorldCompanion.Tests;

// The rule being reproduced is RainMeadowModManager.CheckMods: enable what the lobby requires,
// turn off what this machine calls high impact or the lobby bans unless the lobby requires it,
// and put the required mods at the front of the load order in the lobby's own order.
public class MeadowModMatchTests
{
    private const string Meadow = MeadowModPolicy.MeadowModId;

    private static MeadowModPolicy Policy(params string[] highImpact) => new()
    {
        HighImpact = highImpact,
        Banned = Array.Empty<string>(),
        Read = true,
    };

    private static CurrentMods Machine(string[] on, string[]? alsoInstalled = null)
    {
        var enabled = new List<ModEntry>();
        for (int index = 0; index < on.Length; index++)
        {
            ModEntry mod = ModLists.Mod(on[index]);
            mod.LoadOrder = index;
            enabled.Add(mod);
        }

        var installed = enabled.Select(mod => ModLists.Mod(mod.Id)).ToList();
        foreach (string id in alsoInstalled ?? Array.Empty<string>())
        {
            installed.Add(ModLists.Mod(id));
        }

        return new CurrentMods(ModLists.Snapshot("v1.11.8", enabled.ToArray()), installed);
    }

    [Fact]
    public void The_two_lobby_strings_split_on_newlines()
    {
        MeadowLobbyMods lobby = MeadowLobbyMods.Read($"{Meadow}\nrwremix\n\nmoreslugcats", "pom\ncrs");

        Assert.Equal(new[] { Meadow, "rwremix", "moreslugcats" }, lobby.Required);
        Assert.Equal(new[] { "pom", "crs" }, lobby.Banned);
    }

    [Fact]
    public void A_required_mod_that_is_off_is_turned_on()
    {
        MeadowModMatch match = MeadowModMatch.Build(
            MeadowLobbyMods.Read($"{Meadow}\nrwremix", ""),
            Policy(),
            Machine(on: new[] { Meadow }, alsoInstalled: new[] { "rwremix" }));

        Assert.Equal(new[] { "rwremix" }, match.Enable);
        Assert.Empty(match.Disable);
        Assert.Empty(match.Missing);
    }

    [Fact]
    public void A_required_mod_nobody_has_is_named_rather_than_enabled()
    {
        MeadowModMatch match = MeadowModMatch.Build(
            MeadowLobbyMods.Read($"{Meadow}\ndeszcworlde", ""),
            Policy(),
            Machine(on: new[] { Meadow }));

        Assert.Empty(match.Enable);
        Assert.Equal(new[] { "deszcworlde" }, match.Missing);
        Assert.False(match.CanJoinCleanly);
    }

    [Fact]
    public void A_mod_the_lobby_bans_is_turned_off()
    {
        MeadowModMatch match = MeadowModMatch.Build(
            MeadowLobbyMods.Read(Meadow, "pom"),
            Policy(),
            Machine(on: new[] { Meadow, "pom" }));

        Assert.Equal(new[] { "pom" }, match.Disable);
    }

    // The case a plain "match the save" diff gets wrong: a mod this machine calls high impact but
    // the lobby neither requires nor bans still has to come off, because the host is not running it.
    [Fact]
    public void One_of_my_own_high_impact_mods_the_lobby_does_not_require_is_turned_off()
    {
        MeadowModMatch match = MeadowModMatch.Build(
            MeadowLobbyMods.Read(Meadow, ""),
            Policy("deszcworlde"),
            Machine(on: new[] { Meadow, "deszcworlde" }));

        Assert.Equal(new[] { "deszcworlde" }, match.Disable);
    }

    [Fact]
    public void An_ordinary_mod_the_lobby_says_nothing_about_is_left_alone()
    {
        MeadowModMatch match = MeadowModMatch.Build(
            MeadowLobbyMods.Read(Meadow, ""),
            Policy("deszcworlde"),
            Machine(on: new[] { Meadow, "dressmyslugcat" }));

        Assert.Empty(match.Disable);
        Assert.True(match.NothingToDo);
    }

    [Fact]
    public void Turning_a_mod_off_takes_what_needed_it_off_too()
    {
        CurrentMods machine = Machine(on: new[] { Meadow, "fisobs", "someaddon" });
        machine.Enabled.Mods.Single(mod => mod.Id == "someaddon").Requirements.Add("fisobs");
        machine.Installed.Single(mod => mod.Id == "someaddon").Requirements.Add("fisobs");

        MeadowModMatch match = MeadowModMatch.Build(
            MeadowLobbyMods.Read(Meadow, "fisobs"),
            Policy(),
            machine);

        Assert.Contains("fisobs", match.Disable);
        Assert.Contains("someaddon", match.Disable);
    }

    [Fact]
    public void A_high_impact_mod_the_lobby_requires_stays_on()
    {
        MeadowModMatch match = MeadowModMatch.Build(
            MeadowLobbyMods.Read($"{Meadow}\ndeszcworlde", ""),
            Policy("deszcworlde"),
            Machine(on: new[] { Meadow, "deszcworlde" }));

        Assert.Empty(match.Disable);
        Assert.Empty(match.Enable);
    }

    [Fact]
    public void Required_mods_out_of_the_lobbys_order_count_as_a_change()
    {
        MeadowModMatch inOrder = MeadowModMatch.Build(
            MeadowLobbyMods.Read($"rwremix\nmoreslugcats\n{Meadow}", ""),
            Policy(),
            Machine(on: new[] { "rwremix", "moreslugcats", Meadow }));

        MeadowModMatch swapped = MeadowModMatch.Build(
            MeadowLobbyMods.Read($"rwremix\nmoreslugcats\n{Meadow}", ""),
            Policy(),
            Machine(on: new[] { "moreslugcats", "rwremix", Meadow }));

        Assert.False(inOrder.Reorders);
        Assert.True(swapped.Reorders);
    }

    [Fact]
    public void The_wanted_list_puts_the_lobbys_mods_first_in_its_own_order()
    {
        CurrentMods machine = Machine(on: new[] { "dressmyslugcat", "moreslugcats", Meadow });
        MeadowModMatch match = MeadowModMatch.Build(
            MeadowLobbyMods.Read($"{Meadow}\nmoreslugcats", ""),
            Policy(),
            machine);

        ModListSnapshot wanted = match.WantedList(machine);

        Assert.Equal(
            new[] { Meadow, "moreslugcats", "dressmyslugcat" },
            wanted.Mods.Select(mod => mod.Id));
        Assert.Equal(new int?[] { 0, 1, 2 }, wanted.Mods.Select(mod => mod.LoadOrder).ToArray());
    }

    [Fact]
    public void The_wanted_list_holds_what_should_end_up_on_and_nothing_else()
    {
        CurrentMods machine = Machine(
            on: new[] { Meadow, "pom", "deszcworlde" },
            alsoInstalled: new[] { "rwremix" });

        MeadowModMatch match = MeadowModMatch.Build(
            MeadowLobbyMods.Read($"{Meadow}\nrwremix", "pom"),
            Policy("deszcworlde"),
            machine);

        IEnumerable<string> ids = match.WantedList(machine).Mods.Select(mod => mod.Id);

        Assert.Equal(new[] { Meadow, "rwremix" }, ids);
    }

    // Fed back through the ordinary plan builder, the wanted list has to come out as exactly the
    // enable, disable and reorder the match described, because that is the path Apply takes.
    [Fact]
    public void The_wanted_list_drives_the_ordinary_mod_plan()
    {
        CurrentMods machine = Machine(
            on: new[] { Meadow, "pom", "deszcworlde" },
            alsoInstalled: new[] { "rwremix" });

        MeadowModMatch match = MeadowModMatch.Build(
            MeadowLobbyMods.Read($"{Meadow}\nrwremix", "pom"),
            Policy("deszcworlde"),
            machine);

        ModSyncPlan plan = ModSyncPlan.Build(match.WantedList(machine), machine);

        Assert.Equal(new[] { "rwremix" }, plan.Rows.Where(row => row.TurningOn).Select(row => row.Id));
        Assert.Equal(
            new[] { "deszcworlde", "pom" },
            plan.Rows.Where(row => row.TurningOff).Select(row => row.Id).Order());
    }

    [Fact]
    public void A_required_mod_nobody_has_still_reaches_the_plan_as_a_named_row()
    {
        CurrentMods machine = Machine(on: new[] { Meadow });
        MeadowModMatch match = MeadowModMatch.Build(
            MeadowLobbyMods.Read($"{Meadow}\ndeszcworlde", ""),
            Policy(),
            machine);

        ModSyncPlan plan = ModSyncPlan.Build(match.WantedList(machine), machine);
        ModSyncRow row = plan.Rows.Single(item => item.Id == "deszcworlde");

        Assert.False(row.Installed);
        Assert.True(row.Recorded);
    }
}
