using System.Globalization;

using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.Services;

namespace RainWorldCompanion.ViewModels;

public sealed class LiveHistoryItemViewModel
{
    public LiveHistoryItemViewModel(LiveHistoryEntry entry, ISlugcatIconProvider icons)
    {
        Entry = entry;
        Backup = new BackupItemViewModel(entry.Snapshot, icons);
        Portraits = entry.Campaigns
            .Select(campaign => SlugcatCatalog.ForId(campaign.SlugcatId))
            .DistinctBy(info => info.Id, StringComparer.OrdinalIgnoreCase)
            .Select(info => new PortraitViewModel(info, icons.GetIcon(info.Id)))
            .ToArray();
    }

    public LiveHistoryEntry Entry { get; }

    public BackupItemViewModel Backup { get; }

    public BackupSnapshot Snapshot => Entry.Snapshot;

    public string Id => Snapshot.Id;

    public string CapturedText =>
        Entry.CapturedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public string LabelText
    {
        get
        {
            var labels = Entry.Campaigns.Select(campaign =>
            {
                string name = SlugcatCatalog.ForId(campaign.SlugcatId).DisplayName;
                return campaign.Cycle is { } cycle
                    ? name + ", cycle " + cycle.ToString(CultureInfo.InvariantCulture)
                    : name;
            });
            return string.Join("; ", labels);
        }
    }

    public string CampaignCountText => Entry.Campaigns.Count == 1
        ? "1 changed campaign"
        : Entry.Campaigns.Count.ToString(CultureInfo.InvariantCulture) + " changed campaigns";

    public IReadOnlyList<PortraitViewModel> Portraits { get; }

    public bool HasPortraits => Portraits.Count > 0;

    public string SizeText => Backup.SizeText;

    public string DisplayName => CapturedText + "  " + LabelText;

    public string AccessibleName =>
        "Recent live save, " + CapturedText + ", " + LabelText + ", " + SizeText;

    public string TooltipText =>
        LabelText + "\n" + CapturedText + "\n" + SizeText
        + "\n\nCaptured automatically after Rain World saved. Kept until five newer saves exist for each listed campaign.";
}
