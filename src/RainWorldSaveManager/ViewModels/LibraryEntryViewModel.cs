// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists in the referenced assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using RainWorldSaveManager.Core.Library;
using RainWorldSaveManager.Core.Saves;
using RainWorldSaveManager.Core.Saves.Models;
using RainWorldSaveManager.Services;

namespace RainWorldSaveManager.ViewModels;

/// <summary>
/// One row in the library list. The row carries the faces of the slugcats in the stored save, so
/// one named save can be told from another without selecting it.
/// </summary>
public sealed partial class LibraryEntryViewModel : ObservableObject
{
    private const int MaxRowPortraits = 8;

    public LibraryEntryViewModel(LibraryEntry entry, ISlugcatIconProvider icons, string saveRoot)
    {
        Entry = entry;
        Portraits = BuildPortraits(entry, icons);
        SlotBadgeText = BuildSlotBadge(entry, saveRoot);
    }

    public LibraryEntry Entry { get; }

    /// <summary>Verify result for this session. Null until the sweep has reached this row.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    [NotifyPropertyChangedFor(nameof(IsProblem))]
    [NotifyPropertyChangedFor(nameof(IsVerified))]
    [NotifyPropertyChangedFor(nameof(TooltipText))]
    // AccessibleName ends in StateText, so without this a screen reader keeps announcing
    // "Not checked yet" after the row has been checked.
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    private bool? verifiedOk;

    public string Id => Entry.Id;

    public string Name => Entry.Name;

    public bool IsComplete => Entry.IsComplete;

    /// <summary>
    /// The time the save was stored, in the same calendar as the folder name beside it. The folder
    /// name is built with the invariant culture, so formatting this with the current one would print
    /// 2569 next to 2026 on a machine set to the Thai Buddhist calendar.
    /// </summary>
    public string CreatedText =>
        Entry.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public string NoteText => Entry.Manifest?.Note?.Trim() ?? "";

    public bool HasNote => NoteText.Length > 0;

    public string SizeText => BackupItemViewModel.FormatSize(Entry.Manifest?.SizeBytes ?? 0);

    /// <summary>"from sav2", or empty when nothing recorded where it came from.</summary>
    public string SourceText
    {
        get
        {
            var fileName = Entry.Manifest?.SourceFileName;
            return string.IsNullOrWhiteSpace(fileName) ? "" : "from " + fileName;
        }
    }

    /// <summary>Faces for the slugcats in the stored save.</summary>
    public IReadOnlyList<PortraitViewModel> Portraits { get; }

    public bool HasPortraits => Portraits.Count > 0;

    /// <summary>For example "3 campaigns", or "no campaign" for a Rain Meadow online save.</summary>
    public string CampaignCountText
    {
        get
        {
            if (Entry.Manifest?.Metadata is not { } metadata)
            {
                return "no save detail";
            }

            if (metadata.ParseError is not null)
            {
                return "could not be read";
            }

            return metadata.Campaigns.Count switch
            {
                0 => SlotMetadata.DescribeWithoutCampaigns(metadata.RecordCount),
                1 => "1 campaign",
                _ => metadata.Campaigns.Count.ToString(CultureInfo.InvariantCulture) + " campaigns",
            };
        }
    }

    /// <summary>
    /// "in slot 1", or "in slot 1, changed since loaded" when the slot file no longer looks like it
    /// did straight after the load.
    ///
    /// A size and a write time rather than a hash. This is a hint on a row, and re-hashing every
    /// slot on every refresh would cost several megabytes of reading to answer a question the user
    /// has not asked yet.
    /// </summary>
    public string SlotBadgeText { get; }

    public bool HasSlotBadge => SlotBadgeText.Length > 0;

    public bool CanUndoUpdate => Entry.HasPrevious;

    public string StateText
    {
        get
        {
            if (!Entry.IsComplete)
            {
                var problem = Entry.Problem;
                return string.IsNullOrWhiteSpace(problem) ? "Unfinished" : "Unfinished: " + problem.Trim();
            }

            return VerifiedOk switch
            {
                true => "Checked",
                false => "Check failed",
                _ => "Not checked yet",
            };
        }
    }

    public bool IsProblem => !Entry.IsComplete || VerifiedOk == false;

    public bool IsVerified => Entry.IsComplete && VerifiedOk == true;

    public string DisplayName => Name;

    /// <summary>
    /// What a screen reader announces for the row. The row is a grid of separate text blocks, so
    /// without this the container falls back to naming the view model type.
    /// </summary>
    public string AccessibleName
    {
        get
        {
            var text = new StringBuilder();
            text.Append("Library save, ").Append(Name);
            text.Append(", ").Append(CampaignCountText);
            text.Append(", ").Append(SizeText);
            text.Append(", stored ").Append(CreatedText);

            // Which container it was taken from is announced here and nowhere else. On screen it is
            // read off the row and the panel subtitle, neither of which a screen reader reaches
            // through the row's own name.
            if (SourceText.Length > 0)
            {
                text.Append(", ").Append(SourceText);
            }

            if (SlotBadgeText.Length > 0)
            {
                text.Append(", ").Append(SlotBadgeText);
            }

            text.Append(", ").Append(StateText);
            return text.ToString();
        }
    }

    public string TooltipText
    {
        get
        {
            var text = new StringBuilder();
            text.Append(Name).Append('\n');
            text.Append("Stored ").Append(CreatedText);

            if (SourceText.Length > 0)
            {
                text.Append("   ").Append(SourceText);
            }

            text.Append('\n').Append(CampaignCountText).Append("   ").Append(SizeText);
            text.Append("\nFolder: ").Append(Entry.Id);

            if (NoteText.Length > 0)
            {
                text.Append("\n\n").Append(NoteText);
            }

            if (SlotBadgeText.Length > 0)
            {
                text.Append("\n\n").Append(char.ToUpperInvariant(SlotBadgeText[0])).Append(SlotBadgeText[1..]);
            }

            text.Append("\n\n").Append(StateText);
            return text.ToString();
        }
    }

    private static IReadOnlyList<PortraitViewModel> BuildPortraits(LibraryEntry entry, ISlugcatIconProvider icons)
    {
        if (entry.Manifest?.Metadata is not { } metadata)
        {
            return Array.Empty<PortraitViewModel>();
        }

        var portraits = new List<PortraitViewModel>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var campaign in metadata.Campaigns)
        {
            if (!seen.Add(campaign.SlugcatId))
            {
                continue;
            }

            var info = SlugcatCatalog.ForId(campaign.SlugcatId);
            portraits.Add(new PortraitViewModel(info, icons.GetIcon(campaign.SlugcatId)));

            if (portraits.Count == MaxRowPortraits)
            {
                break;
            }
        }

        return portraits;
    }

    private static string BuildSlotBadge(LibraryEntry entry, string saveRoot)
    {
        if (entry.Manifest is not { } manifest || manifest.LastLoadedSlotRef is not { } slot)
        {
            return "";
        }

        var where = "in " + slot.FileName;

        if (manifest.LastLoadedSizeBytes is not { } size || manifest.LastLoadedWriteUtc is not { } written)
        {
            return where;
        }

        try
        {
            var info = new FileInfo(Path.Combine(saveRoot, slot.FileName));
            if (!info.Exists)
            {
                return where + ", no longer there";
            }

            return info.Length == size && info.LastWriteTimeUtc == written
                ? where
                : where + ", changed since loaded";
        }
        catch (Exception)
        {
            // A slot that cannot be measured says only where the save went, which is the part that
            // came out of the manifest and is still true.
            return where;
        }
    }
}
