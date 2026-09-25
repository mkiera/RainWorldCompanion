using System.Windows;
using System.Windows.Controls;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.Views;

public partial class LogCaptureAnalysisView : UserControl
{
    public LogCaptureAnalysisView()
    {
        InitializeComponent();
        TimelineCanvas.PlayheadChanged += time => ViewModel?.SetPlayhead(time);
        TimelineCanvas.MomentSelected += moment => SetMoment(moment);
        TimelineCanvas.IncidentSelected += incident => SetIncident(incident);
        TimelineCanvas.HorizontalZoomRequested += (factor, anchor) => ViewModel?.ZoomTimeline(factor, anchor);
        TimelineCanvas.HorizontalPanRequested += fraction => ViewModel?.PanTimeline(fraction);
        TimelineCanvas.LaneHeightRequested += height => SetLaneHeight(height);
    }

    private LogCaptureAnalysisViewModel? ViewModel => DataContext as LogCaptureAnalysisViewModel;

    private void SetMoment(Core.LogStreaming.Analysis.CaptureTimelineMoment moment)
    {
        if (ViewModel is { } view) view.SelectedMoment = moment;
    }

    private void SetIncident(CaptureIncidentViewModel incident)
    {
        if (ViewModel is { } view) view.SelectedIncident = incident;
    }

    private void SetLaneHeight(double height)
    {
        if (ViewModel is { } view) view.LaneHeight = height;
    }

    private void FitMap_Click(object sender, RoutedEventArgs e) => ReplayMap.Fit();

    private void ZoomMapIn_Click(object sender, RoutedEventArgs e) => ReplayMap.Zoom(1.25);

    private void ZoomMapOut_Click(object sender, RoutedEventArgs e) => ReplayMap.Zoom(1 / 1.25);
}
