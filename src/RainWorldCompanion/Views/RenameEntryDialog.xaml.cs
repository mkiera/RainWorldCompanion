using System.Windows;

namespace RainWorldCompanion.Views;

/// <summary>
/// Renaming a library save is what it was written for, and storing a campaign asks for the same
/// two things, so the wording is passed in rather than a second dialog being drawn with the same
/// two boxes on it.
/// </summary>
public partial class RenameEntryDialog : Window
{
    public RenameEntryDialog(string currentName, string currentNote)
        : this(
            currentName,
            currentNote,
            "Rename a library save",
            "Only the name and the note change. The save itself is not touched.",
            "Rename")
    {
    }

    public RenameEntryDialog(
        string currentName,
        string currentNote,
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
        NoteBox.Text = currentNote;

        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    public string Headline { get; }

    public string Subtitle { get; }

    /// <summary>What the accepting button says, which is also this window's title.</summary>
    public string ActionText { get; }

    public string EntryName => NameBox.Text.Trim();

    public string? EntryNote
    {
        get
        {
            var note = NoteBox.Text?.Trim();
            return string.IsNullOrWhiteSpace(note) ? null : note;
        }
    }

    private void OnRename(object sender, RoutedEventArgs e)
    {
        // A blank name is refused by Core anyway. Stopping here saves the user the round trip.
        if (EntryName.Length > 0)
        {
            DialogResult = true;
        }
    }
}
