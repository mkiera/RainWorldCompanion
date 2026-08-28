using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace RainWorldCompanion.App.Tests;

/// <summary>
/// Themes/Controls.xaml retemplates the stock controls, and a template that names a part wrongly
/// or binds to a property the control does not have fails when the control is first laid out, not
/// when the dictionary is loaded. Laying one of each out here is what turns that into a test
/// failure rather than a crash in front of the user.
/// </summary>
public class ControlTemplateTests
{
    [Fact]
    public void Every_retemplated_control_lays_out_under_both_palettes()
    {
        // One thread for both palettes: an Application can be made once per process and belongs
        // to the thread that made it.
        var failure = OnStaThread(() =>
        {
            if (Application.Current is null)
            {
                _ = new Application();
            }

            foreach (var palette in new[] { "Palette.Light.xaml", "Palette.Dark.xaml" })
            {
                var resources = new ResourceDictionary();
                resources.MergedDictionaries.Add(Load(palette));
                resources.MergedDictionaries.Add(Load("Controls.xaml"));

                foreach (var control in Retemplated())
                {
                    // The style is assigned rather than left to implicit lookup, and the control
                    // is laid out unparented: a ToolTip refuses a parent, being a popup root.
                    control.Resources = resources;
                    control.Style = (Style)resources[control.GetType()];
                    control.Measure(new Size(400, 400));
                    control.Arrange(new Rect(0, 0, 400, 400));
                }
            }
        });

        Assert.Null(failure);
    }

    private static IEnumerable<FrameworkElement> Retemplated()
    {
        yield return new CheckBox { Content = "on" };
        yield return new CheckBox { IsChecked = true, Content = "on" };
        yield return new CheckBox { IsThreeState = true, IsChecked = null, Content = "part" };
        yield return new RadioButton { Content = "one" };
        yield return new RadioButton { IsChecked = true, Content = "one" };
        yield return new ComboBox { ItemsSource = new[] { "a", "b" }, SelectedIndex = 0 };
        yield return new ComboBox { ItemsSource = new[] { "a", "b" }, IsEditable = true, Text = "a" };
        yield return new ComboBox();
        yield return new ScrollBar { Orientation = Orientation.Vertical };
        yield return new ScrollBar { Orientation = Orientation.Horizontal };
        yield return new ProgressBar { Value = 40 };
        yield return new ProgressBar { IsIndeterminate = true };
        yield return new Expander { Header = "head", Content = "body" };
        yield return new Expander { Header = "head", Content = "body", IsExpanded = true };
        yield return new ToolTip { Content = "tip" };
        yield return new ScrollViewer { Content = new TextBlock { Text = "long" } };
    }

    private static ResourceDictionary Load(string name) => new()
    {
        Source = new Uri(
            "pack://application:,,,/RainWorldCompanion;component/Themes/" + name,
            UriKind.Absolute),
    };

    /// <summary>WPF controls can only be built on an STA thread, and xunit's is not one.</summary>
    private static Exception? OnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return failure;
    }
}
