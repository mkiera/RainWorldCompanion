using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using RainWorldCompanion.Core.CompanionMods;
using RainWorldCompanion.Core.Live;
using RainWorldCompanion.Core.Mods;
using RainWorldCompanion.Core.Updates;
using RainWorldCompanion.LiveProtocol;
using RainWorldCompanion.Views;

namespace RainWorldCompanion.ViewModels;

public sealed partial class MainViewModel
{
    public LiveSessionViewModel Live { get; }
    private LiveConnectionServer? _liveServer;
    private LiveSessionWindow? _liveWindow;
    private DeveloperWindow? _developerWindow;

    public void OpenDeveloperWindow()
    {
        if (_developerWindow is not null)
        {
            if (_developerWindow.WindowState == WindowState.Minimized) _developerWindow.WindowState = WindowState.Normal;
            _developerWindow.Activate();
            return;
        }
        var view = new DeveloperViewModel(Live.MapView);
        _developerWindow = new DeveloperWindow(view, () => view.Refresh(_liveServer?.CaptureDiagnostics(), Live,
            new Dictionary<string, string>
            {
                ["App version"] = _appVersion, ["Game running"] = IsGameRunning.ToString(),
                ["Configured game installation"] = _settings.GameInstallPath ?? "Unset",
                ["Update channel"] = _settings.UpdateChannel,
                ["Update deferred"] = _modUpdateDeferred.ToString(),
                ["Install requested"] = (_settings.CompanionModInstallRequestedPath is not null).ToString(),
                ["Next update check (UTC)"] = _nextModUpdate.ToString("O")
            }, DateTimeOffset.UtcNow));
        _developerWindow.Closed += (_, _) => _developerWindow = null;
        _developerWindow.Show();
    }
    private DispatcherTimer? _liveTimer;
    private readonly HttpClient _modHttp = new() { Timeout = TimeSpan.FromSeconds(30) };
    private bool _livePolling;
    private DateTimeOffset _nextModUpdate;
    private bool _modUpdateDeferred;
    private string? _observedModPath;
    private string? _observedModChannel;

    private void StartLiveFeatures()
    {
        try
        {
            _liveServer = new LiveConnectionServer(gameInstallPath: () => _settings.GameInstallPath);
            _liveServer.Changed += OnLiveConnectionChanged;
            _liveServer.Start();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException)
        {
            Live.OperationText = "The live connection could not start: " + error.Message;
        }
        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _liveTimer.Tick += OnLiveTimerTick;
        _liveTimer.Start();
        _ = PollLiveFeaturesAsync();
    }

    private void StopLiveFeatures()
    {
        if (_liveTimer is not null)
        {
            _liveTimer.Stop();
            _liveTimer.Tick -= OnLiveTimerTick;
        }
        if (_liveServer is not null)
        {
            _liveServer.Changed -= OnLiveConnectionChanged;
            _liveServer.Dispose();
        }
        _liveWindow?.Close();
        _developerWindow?.Close();
        _modHttp.Dispose();
    }

    private void OnLiveConnectionChanged()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || _shutdown.IsCancellationRequested) return;
        dispatcher.BeginInvoke(() =>
        {
            if (!_shutdown.IsCancellationRequested) AdoptLiveConnection();
        });
    }

    private void AdoptLiveConnection() => Live.AdoptConnection(
        _liveServer?.Status ?? LiveConnectionStatus.Waiting, _liveServer?.Snapshot, IsGameRunning);

    [RelayCommand]
    private void OpenLiveFeatures()
    {
        if (_liveWindow is not null)
        {
            if (_liveWindow.WindowState == WindowState.Minimized) _liveWindow.WindowState = WindowState.Normal;
            _liveWindow.Activate();
            return;
        }
        _liveWindow = new LiveSessionWindow(Live) { Owner = OwnerWindow };
        _liveWindow.Closed += (_, _) => _liveWindow = null;
        _liveWindow.Show();
    }

    private async void OnLiveTimerTick(object? sender, EventArgs args) => await PollLiveFeaturesAsync();

    private async Task PollLiveFeaturesAsync()
    {
        if (_livePolling || Live.IsWorking || _shutdown.IsCancellationRequested) return;
        _livePolling = true;
        try
        {
            var path = _settings.GameInstallPath;
            var savePath = _settings.GameSavePath;
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                Live.AdoptSetup(false, false, null, "Choose the Rain World installation in Settings to install rwcompanion.");
                AdoptLiveConnection();
                return;
            }
            if (_observedModPath != path || _observedModChannel != _settings.UpdateChannel)
            {
                _observedModPath = path;
                _observedModChannel = _settings.UpdateChannel;
                _nextModUpdate = DateTimeOffset.MinValue;
                _modUpdateDeferred = false;
            }
            var manager = new CompanionModManager(path, () => _gameDetector.IsGameRunning(out _));
            var status = await Task.Run(() =>
            {
                manager.Recover();
                var options = OptionsFile.Read(savePath);
                return manager.Inspect(options.Read && options.EnabledModIds.Contains("rwcompanion", StringComparer.OrdinalIgnoreCase),
                    _appVersion, ProtocolInfo.Version);
            }, _shutdown.Token);
            if (_shutdown.IsCancellationRequested || path != _settings.GameInstallPath) return;
            Live.AdoptSetup(status.Installed, status.Ready, status.Version, status.Problem ?? "rwcompanion is installed and enabled.");
            AdoptLiveConnection();
            if (IsBusy) return;
            if (_settings.CompanionModInstallRequestedPath == path && !IsGameRunning)
            {
                await InstallCompanionModAsync();
            }
            else if (status.Installed && (_modUpdateDeferred ? !IsGameRunning : DateTimeOffset.UtcNow >= _nextModUpdate))
            {
                _nextModUpdate = DateTimeOffset.UtcNow.AddHours(6);
                await ManageCompanionModAsync(manager, status.Version, false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or HttpRequestException or JsonException or InvalidOperationException)
        {
            Live.OperationText = "Companion mod check failed: " + error.Message;
        }
        finally { _livePolling = false; }
    }

    private async Task InstallCompanionModAsync()
    {
        if (Live.IsWorking || IsBusy) return;
        var path = _settings.GameInstallPath;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path) || _modSync is null)
        {
            Live.OperationText = "Choose the game installation, save folder, and backup folder in Settings first.";
            return;
        }
        PersistSetting(settings => settings.CompanionModInstallRequestedPath = path);
        var running = await Task.Run(() => _gameDetector.IsGameRunning(out _), _shutdown.Token);
        if (running)
        {
            Live.OperationText = "Installation is queued. Close Rain World and Companion will install and enable rwcompanion.";
            return;
        }
        await ManageCompanionModAsync(new CompanionModManager(path, () => _gameDetector.IsGameRunning(out _)), null, true);
    }

    private async Task ManageCompanionModAsync(CompanionModManager manager, string? installedVersion, bool enable)
    {
        if (Live.IsWorking || IsBusy) return;
        Live.IsWorking = true;
        BeginBusy("Companion mod", enable ? "Installing and enabling rwcompanion…" : "Checking for compatible mod updates…");
        var sync = _modSync;
        try
        {
            var existing = enable
                ? await Task.Run(() => manager.Inspect(true, _appVersion, ProtocolInfo.Version), _shutdown.Token)
                : null;
            if (existing?.Compatible == true) installedVersion = existing.Version;
            var channel = UpdateChannels.Parse(_settings.UpdateChannel);
            var source = await Task.Run(() => CreateModUpdateSource(channel), _shutdown.Token);
            var result = await new CompanionModUpdater(source, manager).CheckAndInstallAsync(
                installedVersion, _appVersion, ProtocolInfo.Version, channel, _shutdown.Token);
            if (result?.Outcome == CompanionModInstallOutcome.Deferred)
            {
                _modUpdateDeferred = true;
                Live.OperationText = result.Problem ?? "The mod update will install when Rain World closes.";
                return;
            }
            _modUpdateDeferred = false;
            if (enable) PersistSetting(settings => settings.CompanionModInstallRequestedPath = null);
            if (result is null && !(enable && existing?.Compatible == true))
            {
                Live.OperationText = enable ? "No compatible mod package is available in this build or update channel." : "rwcompanion is up to date.";
                return;
            }
            if (result?.Outcome == CompanionModInstallOutcome.Failed)
            {
                Live.OperationText = result.Problem ?? "The companion mod could not be installed.";
                return;
            }
            if (enable && sync is not null)
            {
                var applied = await Task.Run(() =>
                {
                    var plan = sync.BuildPlan(null);
                    var row = plan.Rows.Single(row => row.Id.Equals("rwcompanion", StringComparison.OrdinalIgnoreCase));
                    row.Wanted = true;
                    return sync.Apply(plan, "Before enabling rwcompanion");
                }, _shutdown.Token);
                Live.OperationText = applied.Problem ?? $"rwcompanion {result?.Version ?? existing?.Version} is installed and enabled. Start Rain World to connect.";
                if (applied.Problem is not null && _gameDetector.IsGameRunning(out _))
                    PersistSetting(settings => settings.CompanionModInstallRequestedPath = sync.GameInstallPath);
            }
            else Live.OperationText = $"rwcompanion updated to {result!.Version}.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or HttpRequestException or JsonException or InvalidOperationException)
        {
            Live.OperationText = "Companion mod installation failed: " + error.Message;
            if (enable) PersistSetting(settings => settings.CompanionModInstallRequestedPath = null);
        }
        finally
        {
            Live.IsWorking = false;
            EndBusy();
        }
    }

    private ICompanionModUpdateSource CreateModUpdateSource(UpdateChannel channel)
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "CompanionMod");
        var release = JsonSerializer.Deserialize<CompanionModRelease>(File.ReadAllText(Path.Combine(folder, "release.json")), CompanionModManifest.JsonOptions)
            ?? throw new InvalidDataException("The bundled companion mod metadata is missing.");
        release = release with { PackageUrl = new Uri(Path.Combine(folder, "rwcompanion.zip")).AbsoluteUri, Channel = channel.ToStorageString() };
        var testSource = Environment.GetEnvironmentVariable("RWCOMPANION_MOD_UPDATE_MANIFEST");
#if DEBUG
        var testSourceFile = Path.Combine(folder, "update-source.txt");
        if (testSource is null && File.Exists(testSourceFile)) testSource = File.ReadAllText(testSourceFile).Trim();
#endif
        Uri? overrideUri = null;
        if (testSource is not null && (!Uri.TryCreate(testSource, UriKind.Absolute, out overrideUri) || !overrideUri.IsLoopback || overrideUri.Scheme != "http"))
            throw new InvalidDataException("The controlled mod update source must be an HTTP loopback address.");
        return new CompanionModUpdateSource(_modHttp,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RainWorldCompanion", "mod-updates"),
            selected => overrideUri ?? new Uri($"https://github.com/mkiera/RainWorldCompanion/releases/download/rwcompanion-{selected.ToStorageString()}/companion-releases.json"), [release]);
    }
}
