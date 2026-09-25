using System.IO;
using System.Text.Json;
using RainWorldCompanion.Core.CompanionMods;
using RainWorldCompanion.LiveProtocol;

namespace RainWorldCompanion.App.Tests;

public class BundledCompanionModTests
{
    [Fact]
    public async Task Bundled_release_installs_and_verifies_without_network()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "CompanionMod");
        var release = JsonSerializer.Deserialize<CompanionModRelease>(File.ReadAllText(Path.Combine(folder, "release.json")), CompanionModManifest.JsonOptions)!;
        var game = Path.Combine(Path.GetTempPath(), "rwc-bundle-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var manager = new CompanionModManager(game, () => false);
            var result = await manager.InstallAsync(Path.Combine(folder, "rwcompanion.zip"), release.Sha256,
                "1.3.0", ProtocolInfo.Version);
            Assert.Equal(CompanionModInstallOutcome.Installed, result.Outcome);
            var status = manager.Inspect(true, "1.3.0", ProtocolInfo.Version);
            Assert.True(status.Ready, status.Problem);
            Assert.Equal(release.Version, status.Version);
            Assert.True(File.Exists(Path.Combine(manager.InstallFolder, "plugins", "RWCompanion.Mod.dll")));
            Assert.True(File.Exists(Path.Combine(manager.InstallFolder, "plugins", "RainWorldCompanion.LiveProtocol.dll")));
        }
        finally { if (Directory.Exists(game)) Directory.Delete(game, true); }
    }
}
