using RainWorldCompanion.Core.Library;
using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Tests;

/// <summary>
/// Which slot holds which library save, and when the two stop matching.
///
/// The library is the only thing that knows this, so the rules live here rather than being worked
/// out from the files. One slot has at most one claimant, an update makes the entry and the slot
/// match again, and anything else writing to a slot takes the claim away.
/// </summary>
public class LibrarySlotLinkTests
{
    private static readonly SaveSlotRef Slot1 = new(SaveRealm.Local, 1);
    private static readonly SaveSlotRef Slot2 = new(SaveRealm.Local, 2);

    [Fact]
    public void Storing_a_slot_claims_nothing()
    {
        using var world = new LibraryWorld();

        var entry = world.Library.StoreSlot(Slot1, "a save", null);

        // Nothing was written to a slot, so no slot holds these bytes yet.
        Assert.Null(entry.Manifest!.LastLoadedSlotRef);
    }

    [Fact]
    public void Loading_claims_the_slot_it_wrote_to()
    {
        using var world = new LibraryWorld();
        var entry = world.Library.StoreSlot(Slot1, "a save", null);

        var result = world.Library.LoadEntry(entry, Slot2);

        Assert.True(result.Success);
        var manifest = world.Reload(entry).Manifest!;
        Assert.Equal(Slot2, manifest.LastLoadedSlotRef);
    }

    [Fact]
    public void Loading_a_second_save_into_a_slot_takes_the_claim_from_the_first()
    {
        // Without this the first row keeps saying it is in sav, and the badge reports a played slot
        // when the slot in fact holds a different save entirely.
        using var world = new LibraryWorld();
        var first = world.Library.StoreSlot(Slot1, "first", null);
        var second = world.Library.StoreSlot(Slot2, "second", null);

        Assert.True(world.Library.LoadEntry(first, Slot2).Success);
        Assert.Equal(Slot2, world.Reload(first).Manifest!.LastLoadedSlotRef);

        Assert.True(world.Library.LoadEntry(second, Slot2).Success);

        Assert.Null(world.Reload(first).Manifest!.LastLoadedSlotRef);
        Assert.Equal(Slot2, world.Reload(second).Manifest!.LastLoadedSlotRef);
    }

    [Fact]
    public void Loading_into_a_different_slot_moves_the_claim_rather_than_adding_one()
    {
        using var world = new LibraryWorld();
        var entry = world.Library.StoreSlot(Slot1, "a save", null);

        Assert.True(world.Library.LoadEntry(entry, Slot1).Success);
        Assert.True(world.Library.LoadEntry(entry, Slot2).Success);

        Assert.Equal(Slot2, world.Reload(entry).Manifest!.LastLoadedSlotRef);
    }

    [Fact]
    public void Updating_makes_the_entry_and_the_slot_match_again()
    {
        // The bug this pins: an update copies the slot into the entry, so the two are byte for byte
        // the same afterwards. Leaving the old stamp had the row say "played since" about the very
        // slot it had just been brought level with, and no amount of updating cleared it.
        using var world = new LibraryWorld();
        var entry = world.Library.StoreSlot(Slot1, "a save", null);
        Assert.True(world.Library.LoadEntry(entry, Slot1).Success);

        PlayInto(world, Slot1);
        Assert.True(SlotHasChangedSince(world, world.Reload(entry)));

        world.Library.UpdateEntry(world.Reload(entry), Slot1);

        Assert.False(SlotHasChangedSince(world, world.Reload(entry)));
    }

    [Fact]
    public void Updating_from_a_slot_claims_that_slot()
    {
        using var world = new LibraryWorld();
        var entry = world.Library.StoreSlot(Slot1, "a save", null);

        world.Library.UpdateEntry(entry, Slot2);

        Assert.Equal(Slot2, world.Reload(entry).Manifest!.LastLoadedSlotRef);
    }

    [Fact]
    public void Updating_takes_the_claim_from_whoever_held_that_slot()
    {
        using var world = new LibraryWorld();
        var holder = world.Library.StoreSlot(Slot1, "holder", null);
        var other = world.Library.StoreSlot(Slot2, "other", null);

        Assert.True(world.Library.LoadEntry(holder, Slot1).Success);
        world.Library.UpdateEntry(other, Slot1);

        Assert.Null(world.Reload(holder).Manifest!.LastLoadedSlotRef);
        Assert.Equal(Slot1, world.Reload(other).Manifest!.LastLoadedSlotRef);
    }

    [Fact]
    public void Undoing_an_update_gives_up_the_slot()
    {
        // The entry holds the older save again, which is not what the slot holds.
        using var world = new LibraryWorld();
        var entry = world.Library.StoreSlot(Slot1, "a save", null);
        Assert.True(world.Library.LoadEntry(entry, Slot1).Success);

        PlayInto(world, Slot1);
        world.Library.UpdateEntry(world.Reload(entry), Slot1);
        Assert.Equal(Slot1, world.Reload(entry).Manifest!.LastLoadedSlotRef);

        world.Library.UndoUpdate(world.Reload(entry));

        Assert.Null(world.Reload(entry).Manifest!.LastLoadedSlotRef);
    }

    [Fact]
    public void Releasing_a_slot_clears_only_that_slot()
    {
        using var world = new LibraryWorld();
        var one = world.Library.StoreSlot(Slot1, "one", null);
        var two = world.Library.StoreSlot(Slot2, "two", null);

        Assert.True(world.Library.LoadEntry(one, Slot1).Success);
        Assert.True(world.Library.LoadEntry(two, Slot2).Success);

        world.Library.ReleaseSlot(Slot1);

        Assert.Null(world.Reload(one).Manifest!.LastLoadedSlotRef);
        Assert.Equal(Slot2, world.Reload(two).Manifest!.LastLoadedSlotRef);
    }

    [Fact]
    public void Releasing_every_slot_clears_them_all()
    {
        using var world = new LibraryWorld();
        var one = world.Library.StoreSlot(Slot1, "one", null);
        var two = world.Library.StoreSlot(Slot2, "two", null);

        Assert.True(world.Library.LoadEntry(one, Slot1).Success);
        Assert.True(world.Library.LoadEntry(two, Slot2).Success);

        world.Library.ReleaseAllSlots();

        Assert.Null(world.Reload(one).Manifest!.LastLoadedSlotRef);
        Assert.Null(world.Reload(two).Manifest!.LastLoadedSlotRef);
    }

    // ---- when the bytes were last written ----

    [Fact]
    public void A_new_save_reports_the_time_it_was_stored()
    {
        using var world = new LibraryWorld();

        var entry = world.Library.StoreSlot(Slot1, "a save", null);

        Assert.False(entry.WasUpdated);
        Assert.Equal(entry.CreatedUtc, entry.ModifiedUtc);
    }

    [Fact]
    public void An_updated_save_reports_the_time_of_the_update()
    {
        // The row showed the day the save was first stored, so a save brought level with an hour of
        // play still read as days old.
        using var world = new LibraryWorld();
        var entry = world.Library.StoreSlot(Slot1, "a save", null);
        var stored = entry.CreatedUtc;

        world.Library.UpdateEntry(entry, Slot2);

        var updated = world.Reload(entry);
        Assert.True(updated.WasUpdated);
        Assert.Equal(stored, updated.CreatedUtc);
        Assert.True(updated.ModifiedUtc >= stored);
        Assert.NotEqual(stored, updated.ModifiedUtc);
    }

    [Fact]
    public void The_list_puts_the_most_recently_written_save_first()
    {
        using var world = new LibraryWorld();
        var older = world.Library.StoreSlot(Slot1, "older", null);
        world.Library.StoreSlot(Slot2, "newer", null);

        Assert.Equal("newer", world.Library.ListEntries()[0].Name);

        world.Library.UpdateEntry(older, Slot2);

        Assert.Equal("older", world.Library.ListEntries()[0].Name);
    }

    // ---- which slot an update offers ----

    [Fact]
    public void A_save_that_was_never_loaded_still_knows_the_slot_it_came_from()
    {
        using var world = new LibraryWorld();

        var entry = world.Library.StoreSlot(Slot2, "a save", null);

        Assert.Null(entry.Manifest!.LastLoadedSlotRef);
        Assert.Equal(Slot2, entry.Manifest.SourceSlotRef);
    }

    [Fact]
    public void An_import_that_named_no_slot_offers_none()
    {
        using var world = new LibraryWorld();
        var loose = Path.Combine(world.LibraryRoot.Path, "handed to me.sav");
        File.Copy(Path.Combine(world.Live.Path, "sav"), loose);

        var imported = world.Library.ImportFile(loose);

        Assert.NotNull(imported.Entry);
        Assert.Null(imported.Entry!.Manifest!.SourceSlotRef);
    }

    /// <summary>Writes to a slot the way the game does, so its size and write time both move.</summary>
    private static void PlayInto(LibraryWorld world, SaveSlotRef slot)
    {
        var path = Path.Combine(world.Live.Path, slot.FileName);
        var bytes = File.ReadAllBytes(path).ToList();
        bytes.AddRange(new byte[] { 0, 0, 0, 0 });
        File.WriteAllBytes(path, bytes.ToArray());
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
    }

    /// <summary>
    /// The same comparison the row badge makes: the stamp taken when the two last matched, against
    /// the slot file as it stands now.
    /// </summary>
    private static bool SlotHasChangedSince(LibraryWorld world, LibraryEntry entry)
    {
        var manifest = entry.Manifest!;
        var slot = manifest.LastLoadedSlotRef;
        Assert.NotNull(slot);

        var info = new FileInfo(Path.Combine(world.Live.Path, slot!.FileName));
        return !info.Exists
            || info.Length != manifest.LastLoadedSizeBytes
            || info.LastWriteTimeUtc != manifest.LastLoadedWriteUtc;
    }
}
