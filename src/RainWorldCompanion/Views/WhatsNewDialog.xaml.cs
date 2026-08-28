using System.Windows;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.Views;

public partial class WhatsNewDialog : Window
{
    public WhatsNewDialog(string headline, IReadOnlyList<WhatsNewSection> sections)
    {
        Headline = headline;
        Sections = sections;

        InitializeComponent();
        DataContext = this;
    }

    public string Headline { get; }

    public IReadOnlyList<WhatsNewSection> Sections { get; }

    public bool ShowVersions => Sections.Count > 1;
}
