using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Mods;

namespace RainWorldCompanion.Tests;

// Mod settings on their own: exported to a .rwconfigs, imported from one, or deleted from the save
// folder. The file is something people send each other, so every path in one is treated as
// something a stranger wrote, and every change to the folder has a safety snapshot to undo it.
public class ModConfigServiceTests
{
    private const string Devourment = "devourment";
    private const string OtherMod = "moreslugcats";
    private const string DevourmentFile = @"ModConfigs\devourment.txt";

    private static ModConfigService ServiceFor(LibraryWorld world) => new(world.Backups, LibraryWorld.AppVersion);

    private static string[] EntryNames(string archivePath)
    {
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        return archive.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal).ToArray();
    }

    private static bool Holds(BackupSnapshot snapshot, string relativePath) =>
        snapshot.Manifest!.Files.Any(entry =>
            string.Equals((entry.RelativePath ?? "").Replace('/', '\\'), relativePath, StringComparison.OrdinalIgnoreCase));

    // A hand-built archive: the manifest names each file, with a checksum that is right or wrong.
    private static void WriteArchive(string path, params (string RelativePath, string Content, bool HashRight)[] files)
    {
        var configs = new ModConfigSet { ReadTheFolder = true };

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        foreach ((string relativePath, string content, bool hashRight) in files)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            string below = string.Join('/', relativePath.Split('\\').Skip(1));

            ZipArchiveEntry entry = archive.CreateEntry("configs/" + below);
            using (Stream target = entry.Open())
            {
                target.Write(bytes);
            }

            configs.Files.Add(new ModConfigFile
            {
                RelativePath = relativePath,
                ModId = ModConfigReader.ModIdFor(relativePath),
                SizeBytes = bytes.Length,
                Sha256 = hashRight
                    ? Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
                    : new string('a', 64),
            });
        }

        var manifest = new ModConfigArchiveManifest { AppVersion = "test", CreatedUtc = DateTime.UtcNow, Configs = configs };
        ZipArchiveEntry manifestEntry = archive.CreateEntry(ModConfigArchive.ManifestFileName);
        using var writer = new StreamWriter(manifestEntry.Open());
        writer.Write(JsonSerializer.Serialize(manifest, BackupJson.Options));
    }

    // ---- export ----

    [Fact]
    public void Export_carries_only_the_chosen_mods_with_their_checksums()
    {
        using var world = new LibraryWorld();
        ModConfigs.Populate(world.Live);
        string file = Path.Combine(world.LibraryRoot.Path, "shared.rwconfigs");

        int count = ServiceFor(world).Export(file, new[] { Devourment });

        Assert.Equal(3, count);
        Assert.Equal(
            new[]
            {
                "configs.json",
                "configs/DvrmentConfs/Preset-kieracustom.txt",
                "configs/DvrmentConfs/current.json",
                "configs/devourment.txt",
            },
            EntryNames(file));

        ModConfigArchiveManifest manifest = ModConfigArchive.ReadManifest(file);
        Assert.Equal(LibraryWorld.AppVersion, manifest.AppVersion);
        Assert.Equal(3, manifest.Configs.Files.Count);
        Assert.All(manifest.Configs.Files, entry => Assert.Equal(64, entry.Sha256.Length));
        Assert.All(manifest.Configs.Files, entry => Assert.Equal(Devourment, entry.ModId));
    }

    [Fact]
    public void Export_with_no_settings_for_the_chosen_mods_is_refused()
    {
        using var world = new LibraryWorld();
        ModConfigs.Populate(world.Live);
        string file = Path.Combine(world.LibraryRoot.Path, "nothing.rwconfigs");

        Assert.Throws<InvalidOperationException>(() => ServiceFor(world).Export(file, new[] { "nosuchmod" }));
        Assert.False(File.Exists(file));
    }

    // ---- import ----

    [Fact]
    public void Import_offers_what_the_file_carries_and_writes_only_what_is_ticked()
    {
        using var theirs = new LibraryWorld();
        using var mine = new LibraryWorld();
        ModConfigs.Populate(theirs.Live);
        ModConfigs.Populate(mine.Live);
        mine.Live.WriteText(DevourmentFile, "DvrmentPredatorMode = false\n");
        mine.Live.WriteText(@"ModConfigs\moreslugcats.txt", "Mine = 1\n");
        string file = Path.Combine(theirs.LibraryRoot.Path, "shared.rwconfigs");
        ServiceFor(theirs).Export(file, new[] { Devourment, OtherMod });

        using ModConfigImport import = ServiceFor(mine).BeginImport(file);

        Assert.Equal("shared.rwconfigs", import.SourceName);
        Assert.Empty(import.Warnings);
        Assert.Equal(new[] { Devourment, OtherMod }, import.Offer.ByMod().Select(group => group.ModId).Order());

        ModConfigGroup devourment = import.Offer.ByMod().Single(group => group.ModId == Devourment);
        Assert.Equal(ModConfigMatch.Different, ModConfigMatching.For(devourment, import.Offer.Live));

        SettingsWriteResult result = import.Apply(new[] { Devourment });

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal(3, result.SettingsWritten);
        Assert.Equal(ModConfigs.SampleConfig, File.ReadAllText(mine.Live.Resolve(DevourmentFile)));
        Assert.Equal("Mine = 1\n", File.ReadAllText(mine.Live.Resolve(@"ModConfigs\moreslugcats.txt")));

        BackupSnapshot safety = Assert.IsType<BackupSnapshot>(result.SafetySnapshot);
        Assert.True(safety.IsComplete);
        Assert.True(Holds(safety, DevourmentFile));
    }

    [Fact]
    public void Import_refuses_a_file_that_is_not_a_settings_archive()
    {
        using var world = new LibraryWorld();
        string file = world.LibraryRoot.WriteText("notes.rwconfigs", "just text");

        Assert.Throws<InvalidDataException>(() => ServiceFor(world).BeginImport(file));
    }

    [Fact]
    public void Import_refuses_a_recorded_path_that_leaves_ModConfigs()
    {
        using var world = new LibraryWorld();
        string file = Path.Combine(world.LibraryRoot.Path, "hostile.rwconfigs");
        WriteArchive(file, (@"ModConfigs\..\evil.txt", "evil = 1\n", true));

        var problem = Assert.Throws<InvalidDataException>(() => ServiceFor(world).BeginImport(file));
        Assert.Contains("Nothing was imported", problem.Message);
    }

    [Fact]
    public void Import_drops_a_file_that_does_not_match_its_checksum_and_says_so()
    {
        using var world = new LibraryWorld();
        string file = Path.Combine(world.LibraryRoot.Path, "tampered.rwconfigs");
        WriteArchive(file, (DevourmentFile, ModConfigs.SampleConfig, false));

        using ModConfigImport import = ServiceFor(world).BeginImport(file);

        Assert.Empty(import.Offer.Recorded.Files);
        string warning = Assert.Single(import.Warnings);
        Assert.Contains("checksum", warning);
    }

    [Fact]
    public void Import_names_the_keys_that_are_about_the_machine()
    {
        using var world = new LibraryWorld();
        string file = Path.Combine(world.LibraryRoot.Path, "camera.rwconfigs");
        WriteArchive(file, (@"ModConfigs\SBCameraScroll.txt", ModConfigs.ConfigWithDisplaySettings, true));

        using ModConfigImport import = ServiceFor(world).BeginImport(file);

        IReadOnlyList<string> keys = Assert.Contains(@"ModConfigs\SBCameraScroll.txt", import.Offer.MachineSpecific);
        Assert.Contains("customResolution", keys);
    }

    // ---- delete ----

    [Fact]
    public void Delete_takes_a_safety_snapshot_then_removes_the_mods_files()
    {
        using var world = new LibraryWorld();
        ModConfigs.Populate(world.Live);

        SettingsDeleteResult result = ServiceFor(world).Delete(new[] { Devourment });

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal(3, result.SettingsDeleted);
        Assert.False(world.Live.FileExists(DevourmentFile));
        Assert.False(Directory.Exists(world.Live.Resolve(@"ModConfigs\DvrmentConfs")));
        Assert.True(world.Live.FileExists(@"ModConfigs\moreslugcats.txt"));

        BackupSnapshot safety = Assert.IsType<BackupSnapshot>(result.SafetySnapshot);
        Assert.True(safety.IsComplete);
        Assert.True(Holds(safety, DevourmentFile));
        Assert.True(Holds(safety, @"ModConfigs\DvrmentConfs\current.json"));
        Assert.True(Holds(safety, @"ModConfigs\DvrmentConfs\Preset-kieracustom.txt"));
    }

    [Fact]
    public void Delete_refuses_while_the_game_is_running()
    {
        using var world = new LibraryWorld(new FakeGameDetector("RainWorld"));
        ModConfigs.Populate(world.Live);

        Assert.Throws<GameRunningException>(() => ServiceFor(world).Delete(new[] { Devourment }));
        Assert.True(world.Live.FileExists(DevourmentFile));
    }

    [Fact]
    public void Delete_with_nothing_to_delete_changes_nothing()
    {
        using var world = new LibraryWorld();
        ModConfigs.Populate(world.Live);
        var before = world.Live.ReadTree();

        SettingsDeleteResult result = ServiceFor(world).Delete(new[] { "nosuchmod" });

        Assert.False(result.Success);
        Assert.Null(result.SafetySnapshot);
        Assert.Contains(result.Errors, error => error.Contains("No settings were chosen"));
        SnapshotLayout.AssertTreeUnchanged(before, world.Live.ReadTree());
    }
}
