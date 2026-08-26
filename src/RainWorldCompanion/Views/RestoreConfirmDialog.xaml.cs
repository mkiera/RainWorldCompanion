using System.Windows;
using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.Views;

/// <summary>
/// Every list the plan carries is rendered, including the two that say where the restore stops
/// short of an exact match: files this backup's rules never covered, which survive it, and files
/// it holds that today's rules exclude, which are not written back. Leaving either out tells the
/// user the save folder will match the snapshot when it will not.
/// </summary>
public partial class RestoreConfirmDialog : Window
{
    public RestoreConfirmDialog(RestorePlan plan, string snapshotName)
    {
        SnapshotName = snapshotName;
        ModDiff = new ModListDiffViewModel(plan.Mods, fromABackup: true);
        Added = plan.Added;
        Overwritten = plan.Overwritten;
        Unchanged = plan.Unchanged;
        Deleted = plan.Deleted;
        LeftAlone = plan.LeftAlone;
        NotRestored = plan.NotRestored;

        AddedHeader = $"Files added ({Added.Count})";
        OverwrittenHeader = $"Files overwritten ({Overwritten.Count})";
        UnchangedHeader = $"Files unchanged ({Unchanged.Count})";
        DeletedHeader = $"Files DELETED ({Deleted.Count})";
        LeftAloneHeader = $"Files kept, this backup predates the rules that cover them ({LeftAlone.Count})";
        NotRestoredHeader = $"Files not written back, this app no longer manages them ({NotRestored.Count})";

        DeletionListVisibility = Deleted.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoDeletionVisibility = Deleted.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        LeftAloneVisibility = LeftAlone.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NotRestoredVisibility = NotRestored.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ExceptionsVisibility = LeftAlone.Count > 0 || NotRestored.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        InitializeComponent();
        DataContext = this;

        // Cancel keeps the focus so Enter never starts a restore by accident
        Loaded += (_, _) => CancelButton.Focus();
    }

    public string SnapshotName { get; }

    public ModListDiffViewModel ModDiff { get; }

    public IReadOnlyList<string> Added { get; }

    public IReadOnlyList<string> Overwritten { get; }

    public IReadOnlyList<string> Unchanged { get; }

    public IReadOnlyList<string> Deleted { get; }

    /// <summary>
    /// In-scope live files this restore will not delete, because the rules the backup was taken
    /// under did not cover them. The save folder will not match the snapshot while this has
    /// anything in it.
    /// </summary>
    public IReadOnlyList<string> LeftAlone { get; }

    /// <summary>Files inside the backup that today's rules exclude, so they are not put back.</summary>
    public IReadOnlyList<string> NotRestored { get; }

    public string AddedHeader { get; }

    public string OverwrittenHeader { get; }

    public string UnchangedHeader { get; }

    public string DeletedHeader { get; }

    public string LeftAloneHeader { get; }

    public string NotRestoredHeader { get; }

    public Visibility DeletionListVisibility { get; }

    public Visibility NoDeletionVisibility { get; }

    public Visibility LeftAloneVisibility { get; }

    public Visibility NotRestoredVisibility { get; }

    public Visibility ExceptionsVisibility { get; }

    private void OnRestore(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
