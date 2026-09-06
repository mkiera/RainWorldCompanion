using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using RainWorldCompanion.Core.CompanionMods;
using RainWorldCompanion.Core.Updates;

namespace RainWorldCompanion.Tests;

public sealed class CompanionModTests
{
    [Fact]
    public async Task Packaged_upgrade_preserves_other_mods_and_reports_ready_without_connection()
    {
        using var temp = new TempDirectory();
        var manager = new CompanionModManager(temp.Path, () => false);
        var unrelated = temp.WriteText("RainWorld_Data/StreamingAssets/mods/other/modinfo.json", "untouched");
        var first = Package(temp, "1.0.0");
        var second = Package(temp, "1.1.0");
        Assert.Equal(CompanionModInstallOutcome.Installed, (await manager.InstallAsync(first, CompanionModPackage.Hash(first), "1.3.0", 1)).Outcome);
        var source = new CompanionModUpdateSource(new HttpClient(new PackageHandler(second)), temp.Resolve("cache"),
            _ => new Uri("http://localhost/manifest.json"), []);
        var result = await new CompanionModUpdater(source, manager).CheckAndInstallAsync("1.0.0", "1.3.0", 1, UpdateChannel.Stable);
        Assert.Equal(CompanionModInstallOutcome.Installed, result!.Outcome);
        var status = manager.Inspect(true, "1.3.0", 1);
        Assert.True(status.Ready);
        Assert.Equal("1.1.0", status.Version);
        Assert.False(manager.Inspect(false, "1.3.0", 1).Ready);
        Assert.Equal("untouched", File.ReadAllText(unrelated));
        Assert.Equal("1.0.0", CompanionModPackage.ReadManifest(manager.PreviousFolder).Version);
    }

    [Fact]
    public async Task Running_game_defers_before_install_and_when_it_starts_during_staging()
    {
        using var temp = new TempDirectory();
        var zip = Package(temp, "1.0.0");
        var manager = new CompanionModManager(temp.Path, () => true);
        Assert.Equal(CompanionModInstallOutcome.Deferred, (await manager.InstallAsync(zip, CompanionModPackage.Hash(zip), "1.3.0", 1)).Outcome);
        Assert.False(Directory.Exists(manager.InstallFolder));
        var checks = 0;
        manager = new CompanionModManager(temp.Path, () => ++checks >= 3);
        Assert.Equal(CompanionModInstallOutcome.Deferred, (await manager.InstallAsync(zip, CompanionModPackage.Hash(zip), "1.3.0", 1)).Outcome);
        Assert.False(Directory.Exists(manager.InstallFolder));
    }

    [Fact]
    public async Task Failed_replacement_restores_previous_and_interrupted_move_recovers()
    {
        using var temp = new TempDirectory();
        var manager = new CompanionModManager(temp.Path, () => false);
        var first = Package(temp, "1.0.0");
        var second = Package(temp, "1.1.0");
        await manager.InstallAsync(first, CompanionModPackage.Hash(first), "1.3.0", 1);
        manager.AfterPreviousMoved = () => throw new IOException("Simulated replacement failure");
        var result = await manager.InstallAsync(second, CompanionModPackage.Hash(second), "1.3.0", 1);
        Assert.Equal(CompanionModInstallOutcome.Failed, result.Outcome);
        Assert.Equal("1.0.0", manager.Inspect(true, "1.3.0", 1).Version);
        Directory.Move(manager.InstallFolder, manager.PreviousFolder);
        manager.Recover();
        Assert.True(manager.Inspect(true, "1.3.0", 1).Ready);
    }

    [Theory]
    [InlineData("../escape.dll")]
    [InlineData("plugins/../../escape.dll")]
    [InlineData("plugins/file.dll:stream")]
    [InlineData("plugins/file.dll.")]
    public async Task Package_paths_cannot_escape_or_use_windows_aliases(string invalidPath)
    {
        using var temp = new TempDirectory();
        var zip = Package(temp, "1.0.0");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update))
        using (var writer = new StreamWriter(archive.CreateEntry(invalidPath).Open())) writer.Write("bad");
        var manager = new CompanionModManager(temp.Path, () => false);
        Assert.Equal(CompanionModInstallOutcome.Failed, (await manager.InstallAsync(zip, CompanionModPackage.Hash(zip), "1.3.0", 1)).Outcome);
        Assert.False(Directory.Exists(manager.InstallFolder));
    }

    [Fact]
    public async Task Rejects_corrupt_download_payload_and_incompatible_protocol()
    {
        using var temp = new TempDirectory();
        var manager = new CompanionModManager(temp.Path, () => false);
        var zip = Package(temp, "1.0.0");
        Assert.Equal(CompanionModInstallOutcome.Failed, (await manager.InstallAsync(zip, new string('0', 64), "1.3.0", 1)).Outcome);
        Assert.Equal(CompanionModInstallOutcome.Failed, (await manager.InstallAsync(zip, CompanionModPackage.Hash(zip), "1.3.0", 2)).Outcome);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update))
        {
            archive.GetEntry("plugins/RWCompanion.Mod.dll")!.Delete();
            using var writer = new StreamWriter(archive.CreateEntry("plugins/RWCompanion.Mod.dll").Open());
            writer.Write("corrupt");
        }
        Assert.Equal(CompanionModInstallOutcome.Failed, (await manager.InstallAsync(zip, CompanionModPackage.Hash(zip), "1.3.0", 1)).Outcome);
    }

    [Fact]
    public async Task Locked_existing_payload_leaves_installed_version_intact()
    {
        using var temp = new TempDirectory();
        var manager = new CompanionModManager(temp.Path, () => false);
        var first = Package(temp, "1.0.0");
        var second = Package(temp, "1.1.0");
        await manager.InstallAsync(first, CompanionModPackage.Hash(first), "1.3.0", 1);
        using (File.Open(Path.Combine(manager.InstallFolder, "plugins", "RWCompanion.Mod.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await manager.InstallAsync(second, CompanionModPackage.Hash(second), "1.3.0", 1);
            Assert.Equal(CompanionModInstallOutcome.Failed, result.Outcome);
        }
        Assert.Equal("1.0.0", manager.Inspect(true, "1.3.0", 1).Version);
    }

    [Fact]
    public async Task Offline_source_keeps_bundled_install_available()
    {
        using var temp = new TempDirectory();
        var zip = Package(temp, "1.0.0");
        var release = new CompanionModRelease("1.0.0", 1, "0.0.0", "stable", new Uri(zip).AbsoluteUri, CompanionModPackage.Hash(zip));
        var source = new CompanionModUpdateSource(new HttpClient(new OfflineHandler()), temp.Resolve("cache"), _ => new Uri("https://example.invalid/mod.json"), [release]);
        Assert.Equal(release, Assert.Single(await source.GetReleasesAsync(UpdateChannel.Stable, default)));
        Assert.Equal(zip, await source.GetPackageAsync(release, default));
    }

    [Theory]
    [InlineData(UpdateChannel.Stable, "1.1.0")]
    [InlineData(UpdateChannel.Prerelease, "1.2.0-beta.1")]
    [InlineData(UpdateChannel.Alpha, "1.3.0-alpha.1")]
    public void Update_selection_respects_channel_and_compatibility(UpdateChannel channel, string expected)
    {
        CompanionModRelease Release(string version, string releaseChannel, int protocol = 1, string minimum = "0.0.0") =>
            new(version, protocol, minimum, releaseChannel, "https://example.com/mod.zip", new string('A', 64));
        var selected = CompanionModUpdater.Select([
            Release("1.1.0", "stable"), Release("1.2.0-beta.1", "prerelease"), Release("1.3.0-alpha.1", "alpha"),
            Release("9.0.0", "stable", 2), Release("8.0.0", "stable", 1, "99.0.0")], "1.0.0", "1.3.0", 1, channel);
        Assert.Equal(expected, selected!.Version);
    }

    private static string Package(TempDirectory temp, string version)
    {
        var folder = temp.Resolve("package-" + version);
        Directory.CreateDirectory(Path.Combine(folder, "plugins"));
        File.WriteAllText(Path.Combine(folder, "modinfo.json"), JsonSerializer.Serialize(new { id = "rwcompanion", version }));
        File.WriteAllText(Path.Combine(folder, "plugins", "RWCompanion.Mod.dll"), "test payload " + version);
        var manifest = new CompanionModManifest
        {
            Version = version, ProtocolVersion = 1,
            Files = new() { ["modinfo.json"] = CompanionModPackage.Hash(Path.Combine(folder, "modinfo.json")),
                ["plugins/RWCompanion.Mod.dll"] = CompanionModPackage.Hash(Path.Combine(folder, "plugins", "RWCompanion.Mod.dll")) }
        };
        File.WriteAllText(Path.Combine(folder, CompanionModPackage.ManifestName), JsonSerializer.Serialize(manifest, CompanionModManifest.JsonOptions));
        var zip = temp.Resolve(version + ".zip");
        ZipFile.CreateFromDirectory(folder, zip);
        return zip;
    }

    private sealed class PackageHandler(string package) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpContent content = request.RequestUri!.AbsolutePath.EndsWith(".json", StringComparison.Ordinal)
                ? new StringContent(JsonSerializer.Serialize(new[] { new CompanionModRelease("1.1.0", 1, "0.0.0", "stable", "http://localhost/mod.zip", CompanionModPackage.Hash(package)) }, CompanionModManifest.JsonOptions), Encoding.UTF8, "application/json")
                : new ByteArrayContent(File.ReadAllBytes(package));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Offline");
    }
}
