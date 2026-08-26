// Usings sit above the namespace: RainWorldCompanion.Core.System would otherwise shadow System.
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.Core.Saves.Models;
using RainWorldCompanion.Services;

namespace RainWorldCompanion.ViewModels;

public sealed partial class BackupItemViewModel : ObservableObject
{
    private const int MaxRowPortraits = 8;

    public BackupItemViewModel(BackupSnapshot snapshot, ISlugcatIconProvider icons)
    {
        Snapshot = snapshot;
        Portraits = BuildPortraits(snapshot, icons);
        CampaignCountText = BuildCampaignCount(snapshot);
    }

    public BackupSnapshot Snapshot { get; }

    /// <summary>Verify result for this session. Null until Verify has been run.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    [NotifyPropertyChangedFor(nameof(HasState))]
    [NotifyPropertyChangedFor(nameof(IsProblem))]
    [NotifyPropertyChangedFor(nameof(IsVerified))]
    [NotifyPropertyChangedFor(nameof(TooltipText))]
    // AccessibleName ends in StateText, so without this a screen reader keeps announcing
    // "Not verified yet" after the row has been verified.
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    private bool? verifiedOk;

    public string Id => Snapshot.Id;

    public bool IsComplete => Snapshot.IsComplete;

    public bool CanRestore => Snapshot.IsComplete;

    /// <summary>
    /// Invariant culture, matching the folder name shown beside it. The current culture would print
    /// 2569 next to 2026 on a machine set to the Thai Buddhist calendar.
    /// </summary>
    public string CreatedText =>
        Snapshot.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public string LabelText
    {
        get
        {
            var label = Snapshot.Manifest?.Label;
            return string.IsNullOrWhiteSpace(label) ? "(no label)" : label.Trim();
        }
    }

    public string NoteText => Snapshot.Manifest?.Note?.Trim() ?? "";

    public bool HasNote => NoteText.Length > 0;

    public bool IsSafetyBackup => Snapshot.Manifest?.Kind == BackupKind.PreRestoreSafety;

    public string KindText => IsSafetyBackup ? "Auto (pre-restore)" : "Manual";

    public IReadOnlyList<PortraitViewModel> Portraits { get; }

    public bool HasPortraits => Portraits.Count > 0;

    public string CampaignCountText { get; }

    public int FileCount => Snapshot.Manifest?.Files.Count ?? 0;

    public string FileCountText => FileCount == 1
        ? "1 file"
        : FileCount.ToString(CultureInfo.InvariantCulture) + " files";

    public string SizeText => FormatSize(Snapshot.TotalSizeBytes);

    public string DisplayName => $"{CreatedText}  {LabelText}";

    /// <summary>
    /// The row is a grid of separate text blocks, so without this a screen reader falls back to
    /// naming the view model type.
    /// </summary>
    public string AccessibleName =>
        $"{KindText} backup, {CreatedText}, {LabelText}, {CampaignCountText}, {SizeText}, {StateText}";

    public string StateText
    {
        get
        {
            if (!Snapshot.IsComplete)
            {
                var problem = Snapshot.Problem;
                return string.IsNullOrWhiteSpace(problem) ? "Incomplete" : "Incomplete: " + problem.Trim();
            }

            return VerifiedOk switch
            {
                true => "Verified",
                false => "Verify failed",
                _ => "Not verified yet",
            };
        }
    }

    public bool HasState => StateText.Length > 0;

    public bool IsProblem => !Snapshot.IsComplete || VerifiedOk == false;

    public bool IsVerified => Snapshot.IsComplete && VerifiedOk == true;

    public string TooltipText
    {
        get
        {
            var text = new StringBuilder();
            text.Append(LabelText).Append('\n');
            text.Append(CreatedText).Append("   ").Append(KindText).Append('\n');
            text.Append(FileCount).Append(" file").Append(FileCount == 1 ? "" : "s").Append("   ").Append(SizeText).Append('\n');
            text.Append("Folder: ").Append(Snapshot.Id);

            if (NoteText.Length > 0)
            {
                text.Append("\n\n").Append(NoteText);
            }

            if (StateText.Length > 0)
            {
                text.Append("\n\n").Append(StateText);
            }

            return text.ToString();
        }
    }

    /// <summary>Invariant culture, the same as every size Core prints.</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024L * 1024L)
        {
            return (bytes / (1024d * 1024d * 1024d)).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
        }

        if (bytes >= 1024L * 1024L)
        {
            return (bytes / (1024d * 1024d)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        }

        if (bytes >= 1024L)
        {
            return (bytes / 1024d).ToString("0", CultureInfo.InvariantCulture) + " KB";
        }

        return bytes.ToString(CultureInfo.InvariantCulture) + " B";
    }

    private static IReadOnlyList<PortraitViewModel> BuildPortraits(BackupSnapshot snapshot, ISlugcatIconProvider icons)
    {
        var slots = snapshot.Manifest?.Slots;
        if (slots is null)
        {
            return Array.Empty<PortraitViewModel>();
        }

        var portraits = new List<PortraitViewModel>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var slot in slots)
        {
            foreach (var campaign in slot.Campaigns)
            {
                if (!seen.Add(campaign.SlugcatId))
                {
                    continue;
                }

                var info = SlugcatCatalog.ForId(campaign.SlugcatId);
                portraits.Add(new PortraitViewModel(info, icons.GetIcon(campaign.SlugcatId)));

                if (portraits.Count == MaxRowPortraits)
                {
                    return portraits;
                }
            }
        }

        return portraits;
    }

    private static string BuildCampaignCount(BackupSnapshot snapshot)
    {
        var slots = snapshot.Manifest?.Slots;
        if (slots is null || slots.Count == 0)
        {
            return "no slot data";
        }

        var local = 0;
        var online = 0;

        foreach (var slot in slots)
        {
            if (slot.Realm == SaveRealm.Online)
            {
                online += slot.Campaigns.Count;
            }
            else
            {
                local += slot.Campaigns.Count;
            }
        }

        return CampaignCount.Describe(local, online);
    }
}
