using System.IO.Compression;
using System.Text;
using System.Text.Json;

using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Library;
using RainWorldCompanion.Core.Mods;
using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Tests;

/// <summary>
/// A .rwsave carrying the mod settings that were beside the save. A bundle is a file people email
/// each other, so every path in one is treated as something a stranger wrote.
/// </summary>
public class ModConfigBundleTests
{
    private static readonly SaveSlotRef Slot1 = new(SaveRealm.Local, 1);

    /// <summary>Exports an entry and imports it into a second library, which is what happens when
    /// somebody sends you their save.</summary>
    private static LibraryImportResult RoundTrip(LibraryWorld from, LibraryWorld to, LibraryEntry entry)
    {
        var file = Path.Combine(from.LibraryRoot.Path, "carried.rwsave");
        from.Library.ExportEntry(entry, file);
        return to.Library.ImportFile(file);
    }

    // ---- what a bundle carries ----

    [Fact]
    public void An_exported_save_carries_the_settings_and_an_import_keeps_them()
    {
        using var source = new LibraryWorld();
        using var elsewhere = new LibraryWorld();
        LibraryEntry stored = source.Library.StoreSlot(Slot1, "a save", null);

        LibraryEntry imported = RoundTrip(source, elsewhere, stored).Entry!;

        Assert.Equal(
            SaveTree.Sorted(stored.Manifest!.Configs!.Files.Select(file => file.RelativePath)),
            SaveTree.Sorted(imported.Manifest!.Configs!.Files.Select(file => file.RelativePath)));
        Assert.True(imported.HasConfigs);
    }

    /// <summary>Full precision floats come off slider drags, so the point of carrying settings at
    /// all is that they arrive byte for byte.</summary>
    [Fact]
    public void A_carried_settings_file_arrives_byte_for_byte()
    {
        using var source = new LibraryWorld();
        using var elsewhere = new LibraryWorld();
        source.Live.WriteText(@"ModConfigs\devourment.txt", ModConfigs.SampleConfig);
        LibraryEntry stored = source.Library.StoreSlot(Slot1, "a save", null);

        LibraryEntry imported = RoundTrip(source, elsewhere, stored).Entry!;

        Assert.Equal(
            File.ReadAllBytes(Path.Combine(stored.ConfigsPath, "devourment.txt")),
            File.ReadAllBytes(Path.Combine(imported.ConfigsPath, "devourment.txt")));
    }

    [Fact]
    public void A_whole_folder_of_settings_arrives_laid_out_the_same_way()
    {
        using var source = new LibraryWorld();
        using var elsewhere = new LibraryWorld();
        LibraryEntry stored = source.Library.StoreSlot(Slot1, "a save", null);

        LibraryEntry imported = RoundTrip(source, elsewhere, stored).Entry!;

        Assert.True(File.Exists(Path.Combine(imported.ConfigsPath, "DvrmentConfs", "current.json")));
    }

    [Fact]
    public void A_campaign_bundle_carries_them_too()
    {
        using var source = new LibraryWorld();
        using var elsewhere = new LibraryWorld();
        LibraryEntry stored = source.Library.StoreCampaign(Slot1, "White", "a campaign", null);

        var file = Path.Combine(source.LibraryRoot.Path, "carried.rwcampaign");
        source.Library.ExportEntry(stored, file);
        LibraryEntry imported = elsewhere.Library.ImportFile(file).Entry!;

        Assert.True(imported.IsCampaign);
        Assert.NotEmpty(imported.Manifest!.Configs!.Files);
    }

    /// <summary>Settings entries live under configs/, so they can never be the name that decides
    /// whether a bundle holds a slot or a campaign.</summary>
    [Fact]
    public void Carrying_settings_does_not_change_which_kind_a_bundle_is()
    {
        using var source = new LibraryWorld();
        using var elsewhere = new LibraryWorld();
        LibraryEntry stored = source.Library.StoreSlot(Slot1, "a save", null);

        LibraryEntry imported = RoundTrip(source, elsewhere, stored).Entry!;

        Assert.False(imported.IsCampaign);
        Assert.Equal(LibraryEntryKind.WholeSlot, imported.Manifest!.Kind);
    }

    [Fact]
    public void A_save_with_no_settings_carries_none_and_says_it_looked()
    {
        using var source = new LibraryWorld();
        using var elsewhere = new LibraryWorld();
        Directory.Delete(source.Live.Resolve("ModConfigs"), recursive: true);
        LibraryEntry stored = source.Library.StoreSlot(Slot1, "a save", null);

        LibraryEntry imported = RoundTrip(source, elsewhere, stored).Entry!;

        Assert.True(imported.Manifest!.Configs!.ReadTheFolder);
        Assert.Empty(imported.Manifest.Configs.Files);
    }

    /// <summary>An import belongs to whoever is importing, so it starts with no earlier generation
    /// to go back to.</summary>
    [Fact]
    public void An_import_carries_no_earlier_generation_of_settings()
    {
        using var source = new LibraryWorld();
        using var elsewhere = new LibraryWorld();

        LibraryEntry stored = source.Library.StoreSlot(Slot1, "a save", null);
        source.PlayASlot(Slot1, "CYCLENUM", "40");
        stored = source.Library.UpdateEntry(stored, Slot1);

        LibraryEntry imported = RoundTrip(source, elsewhere, stored).Entry!;

        Assert.Null(imported.Manifest!.PreviousConfigs);
        Assert.False(Directory.Exists(imported.PreviousConfigsPath));
    }

    // ---- reading somebody else's file ----

    /// <summary>The one case worth stating plainly: a hostile path refuses the whole import, so a
    /// bundle that tries this lands nothing at all rather than landing the save.</summary>
    [Theory]
    [InlineData(@"..\..\ModConfigs\x.txt")]
    [InlineData(@"ModConfigs\..\..\x.txt")]
    [InlineData(@"\\server\share\x.txt")]
    [InlineData(@"C:\Windows\x.txt")]
    [InlineData(@"Other\x.txt")]
    [InlineData(@"ModConfigs")]
    [InlineData("")]
    public void A_recorded_path_that_could_name_somewhere_else_refuses_the_whole_import(string relativePath)
    {
        using var world = new LibraryWorld();
        var bundle = HandBuilt(world, ModConfigs.File(relativePath, "x"), Encoding.UTF8.GetBytes("x"));

        LibraryImportResult result = world.Library.ImportFile(bundle);

        Assert.False(result.Success);
        Assert.Single(result.Errors);
        Assert.Empty(world.Library.ListEntries());
    }

    /// <summary>
    /// Windows drops a trailing dot while it resolves a path, so a segment left as written is the
    /// only way to tell a recorded path from the file it would actually reach.
    /// </summary>
    [Fact]
    public void A_recorded_path_that_does_not_resolve_back_to_itself_refuses_the_import()
    {
        using var world = new LibraryWorld();
        var bundle = HandBuilt(
            world,
            ModConfigs.File(@"ModConfigs\DvrmentConfs\current.json.", "devourment"),
            Encoding.UTF8.GetBytes("{}"));

        Assert.False(world.Library.ImportFile(bundle).Success);
    }

    /// <summary>
    /// The destination comes from the manifest and the archive is then asked for that one name, so
    /// an entry the manifest never named is never opened and never written.
    /// </summary>
    [Fact]
    public void An_archive_entry_the_manifest_never_named_is_never_written()
    {
        using var world = new LibraryWorld();
        LibraryEntry stored = world.Library.StoreSlot(Slot1, "a save", null);
        var bundle = Path.Combine(world.LibraryRoot.Path, "extra.rwsave");
        world.Library.ExportEntry(stored, bundle);

        using (var archive = ZipFile.Open(bundle, ZipArchiveMode.Update))
        {
            using var stream = archive.CreateEntry(SaveBundle.ConfigsEntryPrefix + "uninvited.txt").Open();
            stream.Write(Encoding.UTF8.GetBytes("never asked for"));
        }

        using var elsewhere = new LibraryWorld();
        LibraryEntry imported = elsewhere.Library.ImportFile(bundle).Entry!;

        Assert.False(File.Exists(Path.Combine(imported.ConfigsPath, "uninvited.txt")));
        Assert.DoesNotContain(
            imported.Manifest!.Configs!.Files,
            file => file.RelativePath.Contains("uninvited", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_settings_file_far_larger_than_one_is_dropped_and_the_save_still_lands()
    {
        using var world = new LibraryWorld();

        LibraryImportResult result = world.Library.ImportFile(
            HandBuilt(world, ModConfigs.File(@"ModConfigs\huge.txt", "huge"), new byte[16 * 1024 * 1024]));

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Contains(result.Warnings, warning => warning.Contains("huge.txt"));
        Assert.Null(result.Entry!.Manifest!.Configs);
    }

    /// <summary>
    /// Bounded by the bytes arriving rather than the lengths declared, so a pile of entries each
    /// under the per-file cap still cannot write more than a save's worth between them.
    /// </summary>
    [Fact]
    public void Settings_coming_to_more_than_a_save_carries_refuse_the_import()
    {
        using var world = new LibraryWorld();
        var many = Enumerable.Range(0, 10)
            .Select(i => ModConfigs.File($@"ModConfigs\mod{i}.txt", $"mod{i}"))
            .ToArray();

        LibraryImportResult result = world.Library.ImportFile(
            HandBuilt(world, many, new byte[7 * 1024 * 1024]));

        Assert.False(result.Success);
        Assert.Empty(world.Library.ListEntries());
    }

    [Fact]
    public void More_settings_files_than_a_save_has_refuses_the_import()
    {
        using var world = new LibraryWorld();
        var many = Enumerable.Range(0, 600)
            .Select(i => ModConfigs.File($@"ModConfigs\mod{i}.txt", $"mod{i}"))
            .ToArray();

        LibraryImportResult result = world.Library.ImportFile(HandBuilt(world, many));

        Assert.False(result.Success);
        Assert.Contains("600", result.Errors[0]);
    }

    /// <summary>A settings file that did not survive the trip is not a reason to refuse the save it
    /// came with. It is dropped, named, and left out of what the entry claims to hold.</summary>
    [Fact]
    public void A_settings_file_that_does_not_match_its_checksum_is_dropped_and_the_save_still_lands()
    {
        using var world = new LibraryWorld();
        var recorded = ModConfigs.File(@"ModConfigs\devourment.txt", "devourment");

        LibraryImportResult result = world.Library.ImportFile(
            HandBuilt(world, recorded, Encoding.UTF8.GetBytes("not what the hash says"), recordTheRealHash: false));

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Contains(result.Warnings, warning => warning.Contains("devourment.txt"));
        Assert.Null(result.Entry!.Manifest!.Configs);
    }

    [Fact]
    public void A_settings_file_the_manifest_names_but_the_archive_lacks_is_dropped_and_named()
    {
        using var world = new LibraryWorld();
        var recorded = ModConfigs.File(@"ModConfigs\devourment.txt", "devourment");

        LibraryImportResult result = world.Library.ImportFile(HandBuilt(world, recorded));

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Contains(result.Warnings, warning => warning.Contains("devourment.txt"));
    }

    /// <summary>
    /// What one file's rot costs is exactly that file. The others in the same bundle land, and what
    /// the entry claims matches what is beside it.
    /// </summary>
    [Fact]
    public void One_damaged_settings_file_does_not_take_the_others_with_it()
    {
        using var world = new LibraryWorld();
        LibraryEntry stored = world.Library.StoreSlot(Slot1, "a save", null);
        var bundle = Path.Combine(world.LibraryRoot.Path, "damaged.rwsave");
        world.Library.ExportEntry(stored, bundle);

        using (var archive = ZipFile.Open(bundle, ZipArchiveMode.Update))
        {
            archive.GetEntry(SaveBundle.ConfigsEntryPrefix + "devourment.txt")!.Delete();
            using var stream = archive.CreateEntry(SaveBundle.ConfigsEntryPrefix + "devourment.txt").Open();
            stream.Write(Encoding.UTF8.GetBytes("somebody edited this in transit"));
        }

        using var elsewhere = new LibraryWorld();
        LibraryImportResult result = elsewhere.Library.ImportFile(bundle);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.DoesNotContain(
            result.Entry!.Manifest!.Configs!.Files,
            file => file.RelativePath.EndsWith("devourment.txt", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            result.Entry.Manifest.Configs.Files,
            file => file.RelativePath.EndsWith("moreslugcats.txt", StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(Path.Combine(result.Entry.ConfigsPath, "devourment.txt")));
    }

    /// <summary>The mod a file is offered under is worked out from the path here rather than taken
    /// from the sender, so a mislabelled file cannot be ticked as somebody else's mod.</summary>
    [Fact]
    public void A_mislabelled_settings_file_is_attributed_by_its_path_not_by_what_was_recorded()
    {
        using var world = new LibraryWorld();
        LibraryEntry stored = world.Library.StoreSlot(Slot1, "a save", null);
        var bundle = Path.Combine(world.LibraryRoot.Path, "mislabelled.rwsave");
        world.Library.ExportEntry(stored, bundle);

        Rewrite(bundle, manifest =>
        {
            foreach (var file in manifest.Configs!.Files)
            {
                file.ModId = "somethingelse";
            }
        });

        using var elsewhere = new LibraryWorld();
        LibraryEntry imported = elsewhere.Library.ImportFile(bundle).Entry!;

        Assert.DoesNotContain("somethingelse", imported.Manifest!.Configs!.Files.Select(file => file.ModId));
        Assert.Contains("devourment", imported.Manifest.Configs.Files.Select(file => file.ModId));
    }

    // ---- builds on either side of this change ----

    /// <summary>A bundle from before this existed records nothing, which must read as nothing
    /// rather than as a save whose folder was looked at and found empty.</summary>
    [Fact]
    public void A_bundle_from_before_settings_existed_imports_with_nothing_recorded()
    {
        using var world = new LibraryWorld();
        LibraryEntry stored = world.Library.StoreSlot(Slot1, "a save", null);
        var bundle = Path.Combine(world.LibraryRoot.Path, "older.rwsave");
        world.Library.ExportEntry(stored, bundle);

        Rewrite(bundle, manifest => manifest.Configs = null, dropConfigEntries: true);

        using var elsewhere = new LibraryWorld();
        LibraryEntry imported = elsewhere.Library.ImportFile(bundle).Entry!;

        Assert.Null(imported.Manifest!.Configs);
        Assert.False(imported.HasConfigs);
    }

    /// <summary>An older build reads three fixed names out of a bundle and skips the rest, so the
    /// save still arrives from a bundle carrying settings it knows nothing about.</summary>
    [Fact]
    public void A_build_that_predates_settings_still_gets_the_save_out_of_a_new_bundle()
    {
        using var world = new LibraryWorld();
        LibraryEntry stored = world.Library.StoreSlot(Slot1, "a save", null);
        var bundle = Path.Combine(world.LibraryRoot.Path, "newer.rwsave");
        world.Library.ExportEntry(stored, bundle);

        using var archive = ZipFile.OpenRead(bundle);

        Assert.NotNull(archive.GetEntry(LibraryEntry.ManifestFileName));
        Assert.NotNull(archive.GetEntry(LibraryEntry.SaveFileName));
        Assert.Null(archive.GetEntry(LibraryEntry.CampaignFileName));
    }

    /// <summary>A settings path a later build carries and this one does not is left behind with a
    /// word about it, rather than refused the way a hostile path is.</summary>
    [Fact]
    public void A_settings_file_shaped_the_way_a_later_build_writes_is_left_behind_not_refused()
    {
        using var world = new LibraryWorld();
        var recorded = ModConfigs.File(@"ModConfigs\SomeLaterMod\settings.json", "SomeLaterMod");

        LibraryImportResult result = world.Library.ImportFile(
            HandBuilt(world, recorded, Encoding.UTF8.GetBytes("{}")));

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Contains(result.Warnings, warning => warning.Contains("SomeLaterMod"));
    }

    // ---- building a bundle by hand ----

    /// <summary>
    /// A bundle written from the outside, which is the only way to test what this app would never
    /// write: a recorded path pointing somewhere else, a length that lies, a file that is not there.
    /// </summary>
    private static string HandBuilt(
        LibraryWorld world,
        ModConfigFile recorded,
        byte[]? bytes = null,
        bool recordTheRealHash = true)
        => HandBuilt(world, new[] { recorded }, bytes, recordTheRealHash);

    /// <param name="recordTheRealHash">False records a hash the bytes do not have, which is what a
    /// settings file damaged in transit looks like.</param>
    private static string HandBuilt(
        LibraryWorld world,
        ModConfigFile[] recorded,
        byte[]? bytes = null,
        bool recordTheRealHash = true)
    {
        var savePath = world.Live.Resolve("sav");
        var saveBytes = File.ReadAllBytes(savePath);

        var manifest = new LibraryManifest
        {
            SchemaVersion = LibraryManifest.CurrentSchemaVersion,
            Name = "from somewhere else",
            CreatedUtc = DateTime.UtcNow,
            AppVersion = LibraryWorld.AppVersion,
            SourceFileName = "sav",
            SizeBytes = saveBytes.Length,
            Sha256 = Hashing.ComputeFileSha256(savePath),
            Configs = new ModConfigSet { ReadTheFolder = true, Files = recorded.ToList() },
        };

        if (bytes is not null && recordTheRealHash)
        {
            var hash = Sha256Of(bytes);
            foreach (var file in recorded)
            {
                file.SizeBytes = bytes.Length;
                file.Sha256 = hash;
            }
        }

        var path = Path.Combine(world.LibraryRoot.Path, "handbuilt.rwsave");

        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(archive.CreateEntry(LibraryEntry.ManifestFileName).Open()))
            {
                writer.Write(JsonSerializer.Serialize(manifest, BackupJson.Options));
            }

            using (var save = archive.CreateEntry(LibraryEntry.SaveFileName).Open())
            {
                save.Write(saveBytes);
            }

            if (bytes is not null)
            {
                foreach (var file in recorded)
                {
                    var below = file.RelativePath.Replace('\\', '/');
                    var cut = below.IndexOf('/');
                    if (cut < 0)
                    {
                        continue;
                    }

                    using var entry = archive.CreateEntry(SaveBundle.ConfigsEntryPrefix + below[(cut + 1)..]).Open();
                    entry.Write(bytes);
                }
            }
        }

        return path;
    }

    /// <summary>Rewrites the manifest inside a bundle, leaving everything else in it as it was.</summary>
    private static void Rewrite(string bundlePath, Action<LibraryManifest> change, bool dropConfigEntries = false)
    {
        using var archive = ZipFile.Open(bundlePath, ZipArchiveMode.Update);

        var entry = archive.GetEntry(LibraryEntry.ManifestFileName)!;
        LibraryManifest manifest;

        using (var reader = new StreamReader(entry.Open()))
        {
            manifest = JsonSerializer.Deserialize<LibraryManifest>(reader.ReadToEnd(), BackupJson.Options)!;
        }

        change(manifest);
        entry.Delete();

        using (var writer = new StreamWriter(archive.CreateEntry(LibraryEntry.ManifestFileName).Open()))
        {
            writer.Write(JsonSerializer.Serialize(manifest, BackupJson.Options));
        }

        if (!dropConfigEntries)
        {
            return;
        }

        foreach (var config in archive.Entries
            .Where(e => e.FullName.StartsWith(SaveBundle.ConfigsEntryPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList())
        {
            config.Delete();
        }
    }

    private static string Sha256Of(byte[] bytes)
    {
        using var temp = new TempDirectory("hash");
        return Hashing.ComputeFileSha256(temp.WriteBytes("bytes", bytes));
    }
}
