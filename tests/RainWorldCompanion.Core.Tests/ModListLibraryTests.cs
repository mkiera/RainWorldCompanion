using RainWorldCompanion.Core.Editing;
using RainWorldCompanion.Core.Library;
using RainWorldCompanion.Core.Mods;
using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Tests;

public class ModListLibraryTests
{
    private static readonly SaveSlotRef Slot1 = new(SaveRealm.Local, 1);

    private static CurrentMods Version(string version) => ModLists.Current(
        "v1.11.8", ModLists.Mod("devourment", version), ModLists.Mod("MapOptions", "2.3.3", workshopId: "2923374705"));

    [Fact]
    public void A_stored_slot_records_the_mods_that_were_on()
    {
        using var world = new LibraryWorld(modListSource: () => Version("1.0"));

        LibraryEntry entry = world.Library.StoreSlot(Slot1, "a save", null);

        ModListSnapshot mods = entry.Manifest!.Mods!;
        Assert.Equal(new[] { "devourment", "MapOptions" }, mods.Mods.Select(mod => mod.Id));
        Assert.Equal("v1.11.8", mods.GameVersion);
    }

    [Fact]
    public void A_stored_campaign_records_them_too()
    {
        using var world = new LibraryWorld(modListSource: () => Version("1.0"));

        LibraryEntry entry = world.Library.StoreCampaign(Slot1, "White", "a campaign", null);

        Assert.Equal(2, entry.Manifest!.Mods!.Mods.Count);
    }

    /// <summary>
    /// A campaign taken from a backup carries that backup's mod record. Stamping today's mods
    /// onto old bytes would describe a machine the save was never played on.
    /// </summary>
    [Fact]
    public void A_campaign_taken_from_elsewhere_carries_the_list_it_was_given()
    {
        using var world = new LibraryWorld(modListSource: () => Version("today"));
        ModListSnapshot back_then = ModLists.Snapshot("v1.9.15", ModLists.Mod("devourment", "back then"));

        CampaignSlice slice = CampaignFile.ReadFrom(world.Live.Resolve("sav"), "White")!;
        LibraryEntry entry = world.Library.StoreCampaignFrom(
            slice, "backup sav", SaveRealm.Local, 1, "from a backup", null, back_then);

        Assert.Equal("v1.9.15", entry.Manifest!.Mods!.GameVersion);
        Assert.Equal("back then", Assert.Single(entry.Manifest.Mods.Mods).Version);
    }

    [Fact]
    public void A_library_with_nowhere_to_read_mods_still_stores_saves()
    {
        using var world = new LibraryWorld();

        Assert.Null(world.Library.StoreSlot(Slot1, "a save", null).Manifest!.Mods);
    }

    /// <summary>
    /// An update replaces bytes and mod record together and keeps the old pair, so undo can put
    /// both back. A record that outlived the bytes it described would be worse than none.
    /// </summary>
    [Fact]
    public void An_update_keeps_the_earlier_list_and_an_undo_puts_it_back()
    {
        var version = "1.0";
        using var world = new LibraryWorld(modListSource: () => Version(version));

        LibraryEntry entry = world.Library.StoreSlot(Slot1, "a save", null);
        world.PlayASlot(Slot1, "CYCLENUM", "40");

        version = "2.0";
        LibraryEntry updated = world.Library.UpdateEntry(entry, Slot1);

        Assert.Equal("2.0", updated.Manifest!.Mods!.Mods[0].Version);
        Assert.Equal("1.0", updated.Manifest.PreviousMods!.Mods[0].Version);

        LibraryEntry undone = world.Library.UndoUpdate(updated);

        Assert.Equal("1.0", undone.Manifest!.Mods!.Mods[0].Version);
        Assert.Null(undone.Manifest.PreviousMods);
    }

    /// <summary>The bundle carries the whole manifest, so the mod record travels with no extra work from the bundle writer.</summary>
    [Fact]
    public void An_export_carries_the_recorded_list_and_an_import_keeps_it()
    {
        using var source = new LibraryWorld(modListSource: () => Version("1.0"));
        using var elsewhere = new LibraryWorld(modListSource: () => Version("9.9"));

        LibraryEntry stored = source.Library.StoreSlot(Slot1, "a save", null);
        string file = Path.Combine(source.LibraryRoot.Path, "carried.rwsave");
        source.Library.ExportEntry(stored, file);

        LibraryEntry imported = elsewhere.Library.ImportFile(file).Entry!;

        // The exporter's machine, not this one. That is what a load here wants to compare against.
        Assert.Equal("1.0", imported.Manifest!.Mods!.Mods[0].Version);
        Assert.Null(imported.Manifest.PreviousMods);
    }

    [Fact]
    public void A_bare_save_file_imports_with_nothing_recorded()
    {
        using var world = new LibraryWorld(modListSource: () => Version("1.0"));
        string file = Path.Combine(world.LibraryRoot.Path, "loose_sav");
        File.Copy(world.Live.Resolve("sav"), file);

        Assert.Null(world.Library.ImportFile(file).Entry!.Manifest!.Mods);
    }

    [Fact]
    public void A_load_plan_says_how_the_machine_has_moved()
    {
        var version = "1.0";
        using var world = new LibraryWorld(modListSource: () => Version(version));
        LibraryEntry entry = world.Library.StoreSlot(Slot1, "a save", null);

        version = "2.0";
        ModListDiff mods = world.Library.PlanAnyLoad(entry, Slot1).Mods!;

        ModVersionChange change = Assert.Single(mods.Changed);
        Assert.Equal("1.0", change.Recorded);
        Assert.Equal("2.0", change.Now);
    }

    [Fact]
    public void A_campaign_load_plan_compares_the_same_way()
    {
        var version = "1.0";
        using var world = new LibraryWorld(modListSource: () => Version(version));
        LibraryEntry entry = world.Library.StoreCampaign(Slot1, "White", "a campaign", null);

        version = "2.0";
        LibraryLoadPlan plan = world.Library.PlanAnyLoad(entry, Slot1);

        Assert.Single(plan.Mods!.Changed);
    }

    /// <summary>A mod difference informs but never blocks. A load whose mods have all gone is still allowed.</summary>
    [Fact]
    public void A_mod_difference_never_stops_a_load()
    {
        CurrentMods machine = Version("1.0");
        using var world = new LibraryWorld(modListSource: () => machine);
        LibraryEntry entry = world.Library.StoreSlot(Slot1, "a save", null);

        // Every mod the save was played with is gone, which is the worst this comparison can say.
        machine = ModLists.Current("v1.11.8");

        LibraryLoadPlan plan = world.Library.PlanAnyLoad(entry, Slot1);

        Assert.Equal(2, plan.Mods!.Missing.Count);
        Assert.True(plan.CanLoad);
        Assert.Empty(plan.Problems);
    }

    [Fact]
    public void An_entry_with_no_recorded_list_plans_a_load_that_says_so()
    {
        using var world = new LibraryWorld(modListSource: () => Version("1.0"));
        LibraryEntry entry = world.Library.StoreSlot(Slot1, "a save", null);
        entry.Manifest!.Mods = null;

        ModListDiff mods = world.Library.PlanAnyLoad(entry, Slot1).Mods!;

        Assert.True(mods.NothingWasRecorded);
        Assert.False(mods.Compared);
    }

    /// <summary>
    /// Turning mods on to match a save happens between the dialog opening and the write, so a safety
    /// copy that read the machine at copy time would record the list it exists to undo. It records
    /// the one it was handed instead.
    /// </summary>
    [Fact]
    public void A_safety_copy_records_the_mods_from_before_the_operation_not_the_ones_on_now()
    {
        var version = "before";
        using var world = new LibraryWorld(modListSource: () => Version(version));
        LibraryEntry entry = world.Library.StoreSlot(Slot1, "a save", null);

        ModListSnapshot wasOn = world.Backups.ModListSource!().Enabled;

        // Stands in for the Mods window having been through while the dialog was open.
        version = "after";

        LibraryLoadResult result = world.Library.LoadAny(
            entry, new SaveSlotRef(SaveRealm.Local, 3), Array.Empty<string>(), null, default, wasOn);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal("before", result.SafetySnapshot!.Manifest!.Mods!.Mods[0].Version);
    }

    /// <summary>Nothing handed in still means the machine as it stands, which is what a backup
    /// taken on its own wants.</summary>
    [Fact]
    public void A_safety_copy_told_nothing_still_records_the_machine_as_it_stands()
    {
        using var world = new LibraryWorld(modListSource: () => Version("now"));
        LibraryEntry entry = world.Library.StoreSlot(Slot1, "a save", null);

        LibraryLoadResult result = world.Library.LoadAny(entry, new SaveSlotRef(SaveRealm.Local, 3));

        Assert.Equal("now", result.SafetySnapshot!.Manifest!.Mods!.Mods[0].Version);
    }

    [Fact]
    public void A_library_with_nowhere_to_read_mods_plans_loads_with_no_comparison()
    {
        using var world = new LibraryWorld();
        LibraryEntry entry = world.Library.StoreSlot(Slot1, "a save", null);

        LibraryLoadPlan plan = world.Library.PlanAnyLoad(entry, Slot1);

        Assert.Null(plan.Mods);
        Assert.True(plan.CanLoad);
    }
}
