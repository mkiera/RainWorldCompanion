using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.App.Tests;

/// <summary>
/// Which slots offer to be emptied.
///
/// Emptying rewrites the file, so it belongs to the live save folder alone: a backup and a library
/// save are copies taken at a moment. A slot with nothing in it is left off rather than offered and
/// refused, because a button that can only ever report "there is nothing to empty" is one to not
/// draw at all.
/// </summary>
public class EmptySlotTests
{
    [Fact]
    public void A_live_slot_holding_a_campaign_offers_to_be_emptied()
    {
        SlotViewModel slot = Panels.Live(Panels.Slot(2)).Slots[0];

        Assert.True(slot.CanEmpty);
        Assert.Equal(new SaveSlotRef(SaveRealm.Local, 2), slot.EditableSlot);
    }

    [Fact]
    public void A_live_slot_holding_nothing_does_not()
    {
        SlotViewModel slot = Panels.Live(Panels.Slot(2, campaigns: 0)).Slots[0];

        Assert.False(slot.CanEmpty);
        Assert.NotNull(slot.EditableSlot);
    }

    [Fact]
    public void A_backup_never_offers_to_be_emptied()
    {
        using var root = new TempDirectory("panel");

        SlotViewModel slot = Panels.Backup(root, Panels.Slot(2)).Slots[0];

        Assert.False(slot.CanEmpty);
        Assert.Null(slot.EditableSlot);
    }

    [Fact]
    public void A_library_save_never_offers_to_be_emptied()
    {
        using var root = new TempDirectory("panel");

        SlotViewModel slot = Panels.Entry(root, Panels.Slot(2)).Slots[0];

        Assert.False(slot.CanEmpty);
        Assert.Null(slot.EditableSlot);
    }

    /// <summary>
    /// A slot number outside 1 to 3 has no file the writer targets, so it is offered no button even
    /// in the live folder.
    /// </summary>
    [Fact]
    public void A_save_outside_the_three_slots_does_not_offer_it_either()
    {
        SlotViewModel slot = Panels.Live(Panels.Slot(0, fileName: "sav - Copy")).Slots[0];

        Assert.False(slot.CanEmpty);
        Assert.Null(slot.EditableSlot);
    }

    /// <summary>
    /// The online half is a real slot the game reads, so it is emptied the same way the local half
    /// is rather than being a second kind of thing.
    /// </summary>
    [Fact]
    public void An_online_slot_offers_it_the_same_way()
    {
        SnapshotDetailViewModel panel = Panels.Live(Panels.Slot(2, SaveRealm.Online));
        panel.ShowOnline = true;

        SlotViewModel slot = panel.Slots[0];

        Assert.True(slot.CanEmpty);
        Assert.Equal(new SaveSlotRef(SaveRealm.Online, 2), slot.EditableSlot);
    }
}
