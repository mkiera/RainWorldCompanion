using System.Windows;

namespace RainWorldCompanion.Views;

public partial class ModProfileNameDialog : Window
{
    public ModProfileNameDialog(
        string currentName,
        string headline,
        string subtitle,
        string actionText)
    {
        Headline = headline;
        Subtitle = subtitle;
        ActionText = actionText;

        InitializeComponent();
        DataContext = this;
        NameBox.Text = currentName;

        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    public string Headline { get; }

    public string Subtitle { get; }

    public string ActionText { get; }

    public string EntryName => NameBox.Text.Trim();

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        if (EntryName.Length > 0)
        {
            DialogResult = true;
        }
    }
}
