using RainWorldCompanion.Core.Mods;

namespace RainWorldCompanion.Tests;

public class MeadowReadinessTests
{
    private const string Meadow = MeadowModPolicy.MeadowModId;

    private static ModEntry Mod(string id, int order, string? version = "1.0") => new()
    {
        Id = id,
        Name = id,
        Version = version,
        LoadOrder = order,
        Origin = ModEntry.WorkshopOrigin,
        WorkshopId = id == Meadow ? MeadowReadiness.WorkshopId : "1",
    };

    private static CurrentMods Machine(
        IEnumerable<ModEntry> on,
        IEnumerable<ModEntry> installed,
        bool readTheList = true,
        bool checkedTheInstall = true)
        => new(
            new ModListSnapshot
            {
                GameVersion = "v1.11.8",
                ReadTheEnabledList = readTheList,
                CheckedTheInstall = checkedTheInstall,
                CheckedTheWorkshop = checkedTheInstall,
                Mods = on.ToList(),
            },
            installed.ToList());

    [Fact]
    public void The_mod_being_on_is_ready_and_carries_its_version()
    {
        MeadowReadiness readiness = MeadowReadiness.From(Machine(
            on: new[] { Mod("rwremix", 0), Mod(Meadow, 1, "0.1.15.2") },
            installed: new[] { Mod("rwremix", 0), Mod(Meadow, 0, "0.1.15.2") }));

        Assert.Equal(MeadowStep.Ready, readiness.Step);
        Assert.Equal("0.1.15.2", readiness.Version);
    }

    [Fact]
    public void The_mod_being_installed_but_off_is_turned_off()
    {
        MeadowReadiness readiness = MeadowReadiness.From(Machine(
            on: new[] { Mod("rwremix", 0) },
            installed: new[] { Mod("rwremix", 0), Mod(Meadow, 0, "0.1.15.2") }));

        Assert.Equal(MeadowStep.TurnedOff, readiness.Step);
        Assert.Equal("0.1.15.2", readiness.Version);
    }

    [Fact]
    public void The_mod_being_nowhere_after_a_full_look_is_not_installed()
    {
        MeadowReadiness readiness = MeadowReadiness.From(Machine(
            on: new[] { Mod("rwremix", 0) },
            installed: new[] { Mod("rwremix", 0) }));

        Assert.Equal(MeadowStep.NotInstalled, readiness.Step);
        Assert.Null(readiness.Version);
    }

    [Fact]
    public void Nothing_read_is_unknown_rather_than_not_installed()
    {
        Assert.Equal(MeadowStep.Unknown, MeadowReadiness.From(CurrentMods.NothingRead("no folder")).Step);

        // The enabled list alone says what is on, and says nothing about what is on disk.
        Assert.Equal(
            MeadowStep.Unknown,
            MeadowReadiness.From(Machine(
                on: new[] { Mod("rwremix", 0) },
                installed: Array.Empty<ModEntry>(),
                checkedTheInstall: false)).Step);
    }

    [Fact]
    public void An_enabled_entry_alone_still_counts_as_on()
    {
        MeadowReadiness readiness = MeadowReadiness.From(Machine(
            on: new[] { Mod(Meadow, 0, null) },
            installed: Array.Empty<ModEntry>(),
            checkedTheInstall: false));

        Assert.Equal(MeadowStep.Ready, readiness.Step);
        Assert.Null(readiness.Version);
    }

    [Fact]
    public void Turning_it_on_adds_the_mod_after_everything_already_on()
    {
        ModListSnapshot wanted = MeadowReadiness.TurnOn(Machine(
            on: new[] { Mod("rwremix", 0), Mod("sharpener", 1) },
            installed: new[] { Mod("rwremix", 0), Mod("sharpener", 0), Mod(Meadow, 0, "0.1.15.2") }));

        Assert.Equal(new[] { "rwremix", "sharpener", Meadow }, wanted.Mods.Select(mod => mod.Id));
        Assert.Equal(new int?[] { 0, 1, 2 }, wanted.Mods.Select(mod => mod.LoadOrder).ToArray());
        Assert.Equal("0.1.15.2", wanted.Mods[2].Version);
        Assert.Equal(MeadowReadiness.WorkshopId, wanted.Mods[2].WorkshopId);
        Assert.True(wanted.ReadTheEnabledList);
        Assert.Equal("v1.11.8", wanted.GameVersion);
    }

    [Fact]
    public void Turning_it_on_brings_what_it_requires_and_leaves_a_mod_already_on_alone()
    {
        ModEntry meadow = Mod(Meadow, 0);
        meadow.Requirements.Add("needed");

        ModListSnapshot wanted = MeadowReadiness.TurnOn(Machine(
            on: new[] { Mod("rwremix", 0) },
            installed: new[] { Mod("rwremix", 0), Mod("needed", 0), meadow }));

        Assert.Equal(new[] { "rwremix", Meadow, "needed" }, wanted.Mods.Select(mod => mod.Id));

        ModListSnapshot again = MeadowReadiness.TurnOn(Machine(
            on: wanted.Mods,
            installed: new[] { Mod("rwremix", 0), Mod("needed", 0), meadow }));

        Assert.Equal(wanted.Mods.Select(mod => mod.Id), again.Mods.Select(mod => mod.Id));
    }

    [Fact]
    public void Turning_it_on_without_it_installed_still_names_its_workshop_page()
    {
        ModListSnapshot wanted = MeadowReadiness.TurnOn(Machine(
            on: Array.Empty<ModEntry>(),
            installed: Array.Empty<ModEntry>()));

        ModEntry meadow = Assert.Single(wanted.Mods);
        Assert.Equal(Meadow, meadow.Id);
        Assert.Equal(MeadowReadiness.WorkshopId, meadow.WorkshopId);
    }
}
