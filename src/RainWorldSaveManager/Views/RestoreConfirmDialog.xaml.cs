using System.Windows;
using RainWorldSaveManager.Core.Backups;

namespace RainWorldSaveManager.Views;

public partial class RestoreConfirmDialog : Window
{
    public RestoreConfirmDialog(RestorePlan plan, string snapshotName)
    {
        SnapshotName = snapshotName;
        Added = plan.Added;
        Overwritten = plan.Overwritten;
        Unchanged = plan.Unchanged;
        Deleted = plan.Deleted;

        AddedHeader = $"Files added ({Added.Count})";
        OverwrittenHeader = $"Files overwritten ({Overwritten.Count})";
        UnchangedHeader = $"Files unchanged ({Unchanged.Count})";
        DeletedHeader = $"Files DELETED ({Deleted.Count})";

        DeletionListVisibility = Deleted.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoDeletionVisibility = Deleted.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

        InitializeComponent();
        DataContext = this;

        // Cancel keeps the focus so Enter never starts a restore by accident
        Loaded += (_, _) => CancelButton.Focus();
    }

    public string SnapshotName { get; }

    public IReadOnlyList<string> Added { get; }

    public IReadOnlyList<string> Overwritten { get; }

    public IReadOnlyList<string> Unchanged { get; }

    public IReadOnlyList<string> Deleted { get; }

    public string AddedHeader { get; }

    public string OverwrittenHeader { get; }

    public string UnchangedHeader { get; }

    public string DeletedHeader { get; }

    public Visibility DeletionListVisibility { get; }

    public Visibility NoDeletionVisibility { get; }

    private void OnRestore(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
