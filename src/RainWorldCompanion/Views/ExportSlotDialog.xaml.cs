// Usings sit above the namespace declaration on purpose. RainWorldCompanion.Core.System
// exists in the referenced assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.Core.Saves.Models;

namespace RainWorldCompanion.Views;

/// <summary>
/// Every side of the picked slot comes from what Core read off disk, so the campaigns shown are
/// the campaigns that will be exported.
/// </summary>
public partial class ExportSlotDialog : Window, INotifyPropertyChanged
{
    public sealed record SlotChoice(SlotSide Side, string Label)
    {
        /// <summary>
        /// DisplayMemberPath settles what is drawn, but a ComboBoxItem takes its automation name
        /// from this.
        /// </summary>
        public override string ToString() => Label;
    }

    private SlotChoice _selectedSource;
    public ExportSlotDialog(IReadOnlyList<SlotSide> sides, SaveSlotRef initial)
    {
        Choices = BuildChoices(sides);
        _selectedSource = Find(initial) ?? Choices[0];

        InitializeComponent();
        DataContext = this;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<SlotChoice> Choices { get; }

    public SlotChoice SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedSource))
            {
                return;
            }

            _selectedSource = value;
            RaiseAll();
        }
    }

    public SaveSlotRef ChosenSource => new(_selectedSource.Side.Realm, _selectedSource.Side.Slot);

    public string ChosenName => SuggestName(_selectedSource.Side);

    public bool CanExport => _selectedSource.Side.Exists;

    public string BlockedReason
    {
        get
        {
            if (!_selectedSource.Side.Exists)
            {
                return _selectedSource.Side.FileName + " has no save in it.";
            }

            return "";
        }
    }

    public string SourceName => _selectedSource.Side.FileName;

    public string SourceSummary => Summarise(_selectedSource.Side);

    public IReadOnlyList<string> SourceCampaigns => DescribeCampaigns(_selectedSource.Side);

    private static IReadOnlyList<SlotChoice> BuildChoices(IReadOnlyList<SlotSide> sides)
    {
        var choices = new List<SlotChoice>(sides.Count);

        foreach (var side in sides)
        {
            var kind = side.Realm == SaveRealm.Online ? "Online slot " : "Local slot ";
            choices.Add(new SlotChoice(side, kind + Number(side.Slot) + "  (" + side.FileName + ")"));
        }

        return choices;
    }

    private SlotChoice? Find(SaveSlotRef slot)
    {
        foreach (var choice in Choices)
        {
            if (choice.Side.Realm == slot.Realm && choice.Side.Slot == slot.Slot)
            {
                return choice;
            }
        }

        return null;
    }

    /// <summary>A starting name built from what is in the slot, for example "Survivor cycle 87".</summary>
    private static string SuggestName(SlotSide side)
    {
        if (side.Metadata is not { ParseError: null } metadata || metadata.Campaigns.Count == 0)
        {
            return side.FileName;
        }

        var first = metadata.Campaigns[0];
        var name = SlugcatCatalog.ForId(first.SlugcatId).DisplayName;

        if (metadata.Campaigns.Count > 1)
        {
            return name + " and " + Number(metadata.Campaigns.Count - 1) + " more";
        }

        return first.CycleNum is { } cycle ? name + " cycle " + Number(cycle) : name;
    }

    private static string Summarise(SlotSide side)
    {
        if (!side.Exists)
        {
            return "not in the save folder";
        }

        var size = SlotCopyService.FormatSize(side.SizeBytes);

        if (side.Metadata is not { } metadata)
        {
            return size;
        }

        if (metadata.ParseError is { } error)
        {
            return size + "    could not be read: " + error;
        }

        var count = metadata.Campaigns.Count switch
        {
            0 => SlotMetadata.DescribeWithoutCampaigns(metadata.RecordCount),
            1 => "1 campaign",
            _ => Number(metadata.Campaigns.Count) + " campaigns",
        };

        return size + "    " + count;
    }

    private static IReadOnlyList<string> DescribeCampaigns(SlotSide side)
    {
        if (side.Metadata is not { } metadata)
        {
            return Array.Empty<string>();
        }

        var lines = new List<string>();

        foreach (var campaign in metadata.Campaigns)
        {
            var name = SlugcatCatalog.ForId(campaign.SlugcatId).DisplayName;
            lines.Add(campaign.CycleNum.HasValue
                ? name + "    cycle " + Number(campaign.CycleNum.Value)
                : name);
        }

        return lines;
    }

    private void RaiseAll()
    {
        foreach (var name in new[]
                 {
                     nameof(SelectedSource), nameof(ChosenName), nameof(CanExport), nameof(BlockedReason),
                     nameof(SourceName), nameof(SourceSummary), nameof(SourceCampaigns),
                 })
        {
            Raise(name);
        }
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private void OnChoose(object sender, RoutedEventArgs e)
    {
        if (CanExport)
        {
            DialogResult = true;
        }
    }
}
