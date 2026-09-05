using System.Windows;

using RainWorldCompanion.Core.System;

namespace RainWorldCompanion.Views;

public partial class JoinLobbyDialog : Window
{
    private readonly string? _gameInstallPath;

    public JoinLobbyDialog(string? gameInstallPath, bool gameIsRunning)
    {
        _gameInstallPath = gameInstallPath;

        InitializeComponent();

        if (gameIsRunning)
        {
            GameRunningNote.Text =
                "Rain World is open. Close it first, or use Join Game in Steam, because a lobby is "
                + "only read from the command line while the game starts.";
            GameRunningNote.Visibility = Visibility.Visible;
        }

        Loaded += (_, _) => CodeBox.Focus();
    }

    public MeadowStart? Start { get; private set; }

    private void OnInputChanged(object sender, RoutedEventArgs e) => Preview();

    private void Preview()
    {
        // Constructed before InitializeComponent has run the first TextChanged.
        if (JoinButton is null || PreviewPanel is null)
        {
            return;
        }

        Start = null;
        JoinButton.IsEnabled = false;

        if (CodeBox.Text.Trim().Length == 0)
        {
            PreviewPanel.Visibility = Visibility.Collapsed;
            return;
        }

        PreviewPanel.Visibility = Visibility.Visible;

        MeadowJoin? join = MeadowJoin.Read(CodeBox.Text, out string? problem);
        if (join is null)
        {
            PreviewText.Text = problem ?? "That is not a lobby.";
            return;
        }

        MeadowStart start = join.WithPassword(PasswordEntry.Text).Start(_gameInstallPath);
        if (!start.CanRun)
        {
            PreviewText.Text = start.Problem;
            return;
        }

        Start = start;
        JoinButton.IsEnabled = true;
        PreviewText.Text = start.Headline;
    }

    private void OnJoin(object sender, RoutedEventArgs e)
    {
        if (Start is not null)
        {
            DialogResult = true;
        }
    }
}
