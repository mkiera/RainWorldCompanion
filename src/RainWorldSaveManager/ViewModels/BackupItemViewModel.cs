using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using RainWorldSaveManager.Core.Backups;

namespace RainWorldSaveManager.ViewModels;

/// <summary>
/// One row in the backup list.
/// </summary>
public sealed partial class BackupItemViewModel : ObservableObject
{
    public BackupItemViewModel(BackupSnapshot snapshot)
    {
        Snapshot = snapshot;
    }

    public BackupSnapshot Snapshot { get; }

    /// <summary>Verify result for this session. Null until Verify has been run.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    [NotifyPropertyChangedFor(nameof(HasState))]
    [NotifyPropertyChangedFor(nameof(IsProblem))]
    [NotifyPropertyChangedFor(nameof(TooltipText))]
    private bool? verifiedOk;

    public string Id => Snapshot.Id;

    public bool IsComplete => Snapshot.IsComplete;

    public bool CanRestore => Snapshot.IsComplete;

    public string CreatedText => Snapshot.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string LabelText
    {
        get
        {
            var label = Snapshot.Manifest?.Label;
            return string.IsNullOrWhiteSpace(label) ? "(no label)" : label.Trim();
        }
    }

    public string NoteText => Snapshot.Manifest?.Note?.Trim() ?? "";

    public bool IsSafetyBackup => Snapshot.Manifest?.Kind == BackupKind.PreRestoreSafety;

    public string KindText => IsSafetyBackup ? "Auto (pre-restore)" : "Manual";

    public string SlotsText
    {
        get
        {
            var slots = Snapshot.Manifest?.Slots;
            if (slots is null || slots.Count == 0)
            {
                return "no slot data";
            }

            var parts = new List<string>(slots.Count);
            foreach (var slot in slots)
            {
                var line = slot.Describe();
                if (!string.IsNullOrWhiteSpace(line))
                {
                    parts.Add(line.Trim());
                }
            }

            return parts.Count == 0 ? "no slot data" : string.Join("   |   ", parts);
        }
    }

    public int FileCount => Snapshot.Manifest?.Files.Count ?? 0;

    public string SizeText => FormatSize(Snapshot.TotalSizeBytes);

    public string DisplayName => $"{CreatedText}  {LabelText}";

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
                _ => "",
            };
        }
    }

    public bool HasState => StateText.Length > 0;

    public bool IsProblem => !Snapshot.IsComplete || VerifiedOk == false;

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

    public static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024L * 1024L)
        {
            return (bytes / (1024d * 1024d * 1024d)).ToString("0.0") + " GB";
        }

        if (bytes >= 1024L * 1024L)
        {
            return (bytes / (1024d * 1024d)).ToString("0.0") + " MB";
        }

        if (bytes >= 1024L)
        {
            return (bytes / 1024d).ToString("0") + " KB";
        }

        return bytes + " B";
    }
}
