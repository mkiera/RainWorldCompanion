using CommunityToolkit.Mvvm.ComponentModel;
using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.ViewModels;

public sealed partial class DenMapViewModel : ObservableObject
{
    public DenMapViewModel(string currentRoomId, string fieldName)
    {
        FieldName = fieldName;
        CurrentRoomId = currentRoomId.Trim();
        CurrentDen = DenMapCatalog.Find(currentRoomId);
        selectedDen = CurrentDen;
        matches = DenMapCatalog.All;
    }

    public string FieldName { get; }
    public string CurrentRoomId { get; }
    public MappedDen? CurrentDen { get; }
    public bool HasCurrentDen => CurrentDen is not null;
    public string CurrentText => CurrentDen is not null ? $"Current: {CurrentRoomId}"
        : string.IsNullOrEmpty(CurrentRoomId) ? "No current den is set." : $"{CurrentRoomId} is not on this map.";

    [ObservableProperty]
    private string search = "";

    [ObservableProperty]
    private IReadOnlyList<MappedDen> matches;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUseDen))]
    [NotifyPropertyChangedFor(nameof(SelectedRoomText))]
    [NotifyPropertyChangedFor(nameof(SelectedRegionText))]
    private MappedDen? selectedDen;

    public bool CanUseDen => SelectedDen is not null;
    public string SelectedRoomText => SelectedDen?.RoomId ?? "Choose a den";
    public string SelectedRegionText => SelectedDen?.RegionName ?? "Click a marker or select from the list.";
    public string MatchCountText => $"{Matches.Count} of {DenMapCatalog.All.Count} dens";

    partial void OnSearchChanged(string value)
    {
        string query = value.Trim();
        Matches = DenMapCatalog.All.Where(den => den.RoomId.Contains(query, StringComparison.OrdinalIgnoreCase)
            || den.RegionName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || den.RegionCode.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        OnPropertyChanged(nameof(MatchCountText));
    }
}
