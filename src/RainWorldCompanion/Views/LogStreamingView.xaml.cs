using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.Views;

public partial class LogStreamingView : UserControl
{
    private INotifyCollectionChanged? _lines;
    private bool _scrollPending;

    public LogStreamingView()
    {
        InitializeComponent();
        Loaded += (_, _) => AttachLines();
        Unloaded += (_, _) => DetachLines();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        DetachLines();
        if (IsLoaded) AttachLines();
    }

    private void AttachLines()
    {
        if (DataContext is not LogStreamingViewModel view) return;
        _lines = view.VisibleLogLines;
        _lines.CollectionChanged += OnLinesChanged;
        ScrollToNewest(view);
    }

    private void DetachLines()
    {
        if (_lines is not null) _lines.CollectionChanged -= OnLinesChanged;
        _lines = null;
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (DataContext is LogStreamingViewModel view) ScrollToNewest(view);
    }

    private void ScrollToNewest(LogStreamingViewModel view)
    {
        if (!view.AutoScroll || view.VisibleLogLines.Count == 0 || _scrollPending) return;
        _scrollPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _scrollPending = false;
            if (DataContext is LogStreamingViewModel current &&
                current.AutoScroll && current.VisibleLogLines.Count > 0)
                FindScrollViewer(LiveLogList)?.ScrollToEnd();
        });
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is ScrollViewer scroll) return scroll;
            if (FindScrollViewer(child) is { } nested) return nested;
        }
        return null;
    }

    private void OpenCaptureTimeline_Click(object sender, RoutedEventArgs e) => LogStreamingTabs.SelectedIndex = 1;
}
