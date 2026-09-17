using System.Globalization;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.Core.Saves.Models;

namespace RainWorldCompanion.ViewModels;

public sealed record DevourmentContentsTarget(
    SaveSlotRef Slot,
    string SlugcatId,
    string CampaignName,
    string CycleText)
{
    public string Label => "Slot " + Slot.Slot + ", " + CampaignName + ", " + CycleText;

    public static IReadOnlyList<DevourmentContentsTarget> Build(IEnumerable<SlotMetadata> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);

        return slots
            .Where(slot => slot.Realm == SaveRealm.Local
                           && slot.Slot is >= SaveSlotRef.MinSlot and <= SaveSlotRef.MaxSlot)
            .OrderBy(slot => slot.Slot)
            .SelectMany(slot => slot.Campaigns.Select(campaign => new DevourmentContentsTarget(
                new SaveSlotRef(SaveRealm.Local, slot.Slot),
                campaign.SlugcatId,
                SlugcatCatalog.ForId(campaign.SlugcatId).DisplayName,
                campaign.DisplayCycleNum is { } cycle
                    ? "Cycle " + cycle.ToString(CultureInfo.InvariantCulture)
                    : "Cycle -")))
            .ToArray();
    }
}
