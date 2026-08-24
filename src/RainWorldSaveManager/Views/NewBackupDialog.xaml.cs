using System.Windows;
using RainWorldSaveManager.Core.Backups;

namespace RainWorldSaveManager.Views;

public partial class NewBackupDialog : Window
{
    public NewBackupDialog()
    {
        ScopeRules = ReadScopeRules();
        InitializeComponent();
        DataContext = this;
        Loaded += (_, _) => LabelBox.Focus();
    }

    public string? BackupLabel { get; private set; }

    public string? BackupNote { get; private set; }

    public IReadOnlyList<string> ScopeRules { get; }

    private void OnCreate(object sender, RoutedEventArgs e)
    {
        BackupLabel = Clean(LabelBox.Text);
        BackupNote = Clean(NoteBox.Text);
        DialogResult = true;
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static IReadOnlyList<string> ReadScopeRules()
    {
        try
        {
            return BackupScope.DescribeRules();
        }
        catch (Exception ex)
        {
            return new[] { "The backup rules could not be listed: " + ex.Message };
        }
    }
}
