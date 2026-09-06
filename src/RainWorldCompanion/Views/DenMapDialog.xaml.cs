using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.Services;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.Views;

public partial class DenMapDialog : Window
{
    private readonly DenMapViewModel _view;
    private bool _fromMap;

    public DenMapDialog(string currentRoomId, string fieldName, string timeline, DenWorldCatalog world)
    {
        InitializeComponent();
        _view = new DenMapViewModel(currentRoomId, fieldName, timeline, world);
        DataContext = _view;
        Title = $"Choose a den: {fieldName}";
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Map.Load();
        Map.CurrentDen = _view.CurrentDen;
        Map.IsAvailable = _view.IsAvailable;
        Map.DenSelected += SelectFromMap;
        _view.PropertyChanged += SelectionChanged;
        Loaded += (_, _) =>
        {
            Map.Select(_view.SelectedDen, center: true);
            if (_view.SelectedDen is not null)
            {
                DenList.ScrollIntoView(_view.SelectedDen);
            }
            SearchBox.Focus();
        };
    }

    public DenMapSelection? Selection => _view.CanUseDen
        ? new(_view.SelectedDen!.RoomId, _view.Timeline) : null;

    private void TimelineChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not DenMapViewModel view || TimelineBox.SelectedValue is not string requested || requested == view.Timeline)
            return;
        bool accepted = view.TryChangeTimeline(requested, () => ConfirmTimelineChange(requested));
        if (!accepted) TimelineBox.SelectedValue = view.Timeline;
    }

    private bool ConfirmTimelineChange(string requested) => MessageBox.Show(this,
            $"Change this campaign's timeline from {SlugcatCatalog.ForId(_view.Timeline).DisplayName} to {SlugcatCatalog.ForId(requested).DisplayName}?\n\n" +
            "This changes the campaign world, including its rooms and creatures." +
            (_view.NeedsTimelineChoice ? "" : " Further timeline choices in this map will not ask again."),
            "Change campaign timeline?", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    private void SelectFromMap(MappedDen den)
    {
        _fromMap = true;
        try
        {
            if (_view.TrySelectDen(den, ConfirmTimelineChange)) DenList.ScrollIntoView(den);
            else if (_view.NeedsTimelineChoice) TimelineBox.IsDropDownOpen = true;
        }
        finally
        {
            _fromMap = false;
        }
    }

    private void SelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DenMapViewModel.VisibleDens))
        {
            Map.InvalidateVisual();
        }
        if (e.PropertyName == nameof(DenMapViewModel.SelectedDen))
        {
            Map.Select(_view.SelectedDen, center: !_fromMap);
            if (_view.SelectedDen is not null) DenList.ScrollIntoView(_view.SelectedDen);
        }
    }

    private void ZoomIn(object sender, RoutedEventArgs e) => Map.Zoom(1.4);
    private void ZoomOut(object sender, RoutedEventArgs e) => Map.Zoom(1 / 1.4);
    private void FitWorld(object sender, RoutedEventArgs e) => Map.Fit();

    private void FocusCurrent(object sender, RoutedEventArgs e)
    {
        if (_view.CurrentDen is { } den)
        {
            Map.FocusDen(den);
        }
    }

    private void UseDen(object sender, RoutedEventArgs e)
    {
        if (_view.CanUseDen)
        {
            DialogResult = true;
        }
    }
}
