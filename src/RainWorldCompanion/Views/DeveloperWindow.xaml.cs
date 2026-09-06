using System.Windows;
using System.Windows.Threading;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.Views;

public partial class DeveloperWindow : Window
{
    public DeveloperWindow(DeveloperViewModel viewModel, Action refresh)
    {
        InitializeComponent();
        DataContext = viewModel;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => refresh();
        Loaded += (_, _) => { refresh(); timer.Start(); };
        Closed += (_, _) => timer.Stop();
    }
}
