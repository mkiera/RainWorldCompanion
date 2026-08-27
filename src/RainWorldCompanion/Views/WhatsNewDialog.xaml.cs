using System.Windows;

namespace RainWorldCompanion.Views;

public partial class WhatsNewDialog : Window
{
    public WhatsNewDialog(string headline, string notes)
    {
        Headline = headline;
        Notes = notes;

        InitializeComponent();
        DataContext = this;
    }

    public string Headline { get; }

    public string Notes { get; }
}
