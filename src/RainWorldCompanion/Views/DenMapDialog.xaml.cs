using System.ComponentModel;
using System.Windows;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.Views;

public partial class DenMapDialog : Window
{
    private readonly DenMapViewModel _view;
    private bool _fromMap;

    public DenMapDialog(string currentRoomId, string fieldName)
    {
        InitializeComponent();
        _view = new DenMapViewModel(currentRoomId, fieldName);
        DataContext = _view;
        Title = $"Choose a den: {fieldName}";
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Map.Load();
        Map.CurrentDen = _view.CurrentDen;
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

    public string? SelectedRoomId => _view.SelectedDen?.RoomId;

    private void SelectFromMap(MappedDen den)
    {
        _fromMap = true;
        try
        {
            _view.Search = "";
            _view.SelectedDen = den;
            DenList.ScrollIntoView(den);
        }
        finally
        {
            _fromMap = false;
        }
    }

    private void SelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DenMapViewModel.SelectedDen))
        {
            Map.Select(_view.SelectedDen, center: !_fromMap);
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
