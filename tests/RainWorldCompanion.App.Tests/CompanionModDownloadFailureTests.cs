using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using RainWorldCompanion.Core.CompanionMods;
using RainWorldCompanion.Core.Mods;
using RainWorldCompanion.Core.Settings;
using RainWorldCompanion.Core.System;
using RainWorldCompanion.LiveProtocol;
using RainWorldCompanion.Services;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.App.Tests;

public class CompanionModDownloadFailureTests
{
    [Fact]
    public async Task Requested_install_with_a_bad_download_hash_reports_once_and_stops_retrying()
    {
        using var files = new TempDirectory("rwc-hook-hash");
        var game = Directory.CreateDirectory(Path.Combine(files.Path, "game")).FullName;
        var view = Build(files.Path, game);
        var handler = new MismatchedPackageHandler();
        view.ModUpdateSourceFactory = _ => new CompanionModUpdateSource(new HttpClient(handler),
            Path.Combine(files.Path, "cache"), _ => new Uri("https://example.invalid/companion-releases.json"), []);
        Settings(view).CompanionModInstallRequestedPath = game;

        for (var tick = 0; tick < 3; tick++)
        {
            var error = await WpfTestHost.RunAsync(() => Poll(view));
            Assert.Null(error);
        }

        Assert.Equal(1, handler.PackageDownloads);
        Assert.Null(Settings(view).CompanionModInstallRequestedPath);
        Assert.Contains("failed verification", view.Live.OperationText);
    }

    [Fact]
    public async Task Unreadable_previous_hook_folder_is_reported_not_thrown()
    {
        using var files = new TempDirectory("rwc-hook-recover");
        var game = Directory.CreateDirectory(Path.Combine(files.Path, "game")).FullName;
        var previous = new CompanionModManager(game, () => false).PreviousFolder;
        Directory.CreateDirectory(previous);
        File.WriteAllText(Path.Combine(previous, CompanionModPackage.ManifestName), "null");
        var view = Build(files.Path, game);

        var error = await WpfTestHost.RunAsync(() => Poll(view));

        Assert.Null(error);
        Assert.StartsWith("Companion Game Hook check failed", view.Live.OperationText);
    }

    private static MainViewModel Build(string root, string game)
    {
        var saves = Directory.CreateDirectory(Path.Combine(root, "saves")).FullName;
        var detector = new NullGameProcessDetector();
        var view = new MainViewModel(new SettingsStore(Path.Combine(root, "settings.json")), detector,
            new SlugcatIconProvider(), "1.4.0");
        var settings = Settings(view);
        settings.GameInstallPath = game;
        settings.GameSavePath = saves;
        settings.CompanionGameHookAutomaticSetup = true;
        typeof(MainViewModel).GetField("_modSync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(view, new ModSyncService(saves, game, detector, new ModStateStore(Path.Combine(root, "mod-state"))));
        return view;
    }

    private static AppSettings Settings(MainViewModel view) =>
        (AppSettings)typeof(MainViewModel).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;

    private static Task Poll(MainViewModel view) =>
        (Task)typeof(MainViewModel).GetMethod("PollLiveFeaturesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(view, null)!;

    private sealed class MismatchedPackageHandler : HttpMessageHandler
    {
        public int PackageDownloads { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(".json", StringComparison.Ordinal))
            {
                var release = new CompanionModRelease("9.9.9", ProtocolInfo.Version, "0.0.1", "stable",
                    "https://example.invalid/rwcompanion.zip", new string('0', 64));
                var json = JsonSerializer.Serialize(new[] { release }, CompanionModManifest.JsonOptions);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8) });
            }

            PackageDownloads++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        }
    }
}
