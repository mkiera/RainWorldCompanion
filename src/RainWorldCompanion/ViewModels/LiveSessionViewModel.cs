using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RainWorldCompanion.Core.Live;
using RainWorldCompanion.LiveProtocol;

namespace RainWorldCompanion.ViewModels;

public sealed record LivePlayerRow(string Id, string Name, string Location, string Region, string State, string Source);

public sealed partial class LiveSessionViewModel : ObservableObject
{
    private readonly Func<Task> _install;

    public LiveSessionViewModel(Func<Task>? install = null) => _install = install ?? (() => Task.CompletedTask);

    public LiveMapViewModel MapView { get; } = new();

    public ObservableCollection<LivePlayerRow> Players { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IndicatorText))]
    private bool installed;

    [ObservableProperty]
    private bool setupReady;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IndicatorText))]
    private string installedVersion = "";

    [ObservableProperty]
    private string setupText = "rwcompanion is not installed.";

    [ObservableProperty]
    private string connectionText = "Waiting for the game connection.";

    [ObservableProperty]
    private string runningVersion = "Not connected";

    [ObservableProperty]
    private string gameVersion = "Unknown";

    [ObservableProperty]
    private string campaign = "Waiting for gameplay";

    [ObservableProperty]
    private string timeline = "Unknown";

    [ObservableProperty]
    private string gameState = "Not connected";

    [ObservableProperty]
    private string expansions = "Unknown";

    [ObservableProperty]
    private string operationText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private bool isWorking;

    [ObservableProperty]
    private string installLabel = "Install and enable rwcompanion";

    public string IndicatorText => Installed ? $"rwcompanion {InstalledVersion}" : "rwcompanion: not installed";

    public void AdoptSetup(bool exists, bool ready, string? version, string description)
    {
        Installed = exists;
        SetupReady = ready;
        InstalledVersion = version ?? "unknown version";
        SetupText = description;
        InstallLabel = ready ? "Repair rwcompanion" : exists ? "Enable or repair rwcompanion" : "Install and enable rwcompanion";
    }

    public void AdoptConnection(LiveConnectionStatus status, LiveSnapshot? snapshot, bool gameRunning)
    {
        ConnectionText = status switch
        {
            LiveConnectionStatus.Connected => "Connected",
            LiveConnectionStatus.Incompatible => "The running mod uses an incompatible live protocol.",
            _ => gameRunning ? "Waiting for connection" : SetupReady ? "Ready, game closed" : "Not connected",
        };
        bool current = status == LiveConnectionStatus.Connected && snapshot is not null;
        MapView.Adopt(current ? snapshot : null);
        RunningVersion = current ? snapshot!.ModVersion : "Not connected";
        GameVersion = current ? snapshot!.GameVersion : "Unknown";
        Campaign = current && !string.IsNullOrWhiteSpace(snapshot!.Campaign) ? snapshot.Campaign : "Waiting for gameplay";
        Timeline = current && !string.IsNullOrWhiteSpace(snapshot!.Timeline) ? snapshot.Timeline : "Unknown";
        GameState = current ? snapshot!.State : "Not connected";
        Expansions = current ? string.Join(", ", snapshot!.EnabledExpansions) : "Unknown";
        var rows = current ? snapshot!.Players.Select(p => new LivePlayerRow(p.Id, p.Name,
            string.IsNullOrWhiteSpace(p.RoomId) ? "Location unavailable" : p.RoomId,
            p.Region ?? "Unknown", p.Dead switch { true => "Dead", false => "Alive", null => "Unknown" }, p.IsLocal ? "Local" : "Online")).ToArray() : [];
        if (!Players.SequenceEqual(rows))
        {
            Players.Clear();
            foreach (var row in rows) Players.Add(row);
        }
    }

    private bool CanInstall() => !IsWorking;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private Task Install() => _install();
}
