using System.Windows;

namespace RainWorldSaveManager.Views;

/// <summary>Changes a library save's name and note. Nothing else about the entry moves.</summary>
public partial class RenameEntryDialog : Window
{
    public RenameEntryDialog(string currentName, string currentNote)
    {
        InitializeComponent();

        NameBox.Text = currentName;
        NoteBox.Text = currentNote;

        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

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
