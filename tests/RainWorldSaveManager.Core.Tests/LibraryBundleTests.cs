using System.IO.Compression;
using System.Text;
using RainWorldSaveManager.Core.Backups;
using RainWorldSaveManager.Core.Library;
using RainWorldSaveManager.Core.Saves;

namespace RainWorldSaveManager.Tests;

/// <summary>
/// A .rwsave file is a zip holding the save and the manifest that describes it, which is what makes
/// a named save something you can send to somebody. A bare save file can be imported too, but it
/// arrives with nothing but its own bytes.
///
/// The line between the two runs through the recorded hash. A bundle has one, so a bundle whose
/// save no longer matches it has been damaged in transit and is refused. A bare file has none, so a
/// damaged one is imported with a warning: getting a broken save into the library is how somebody
/// looks at what is left of it.
/// </summary>
public class LibraryBundleTests
{
    private static readonly SaveSlotRef LocalTwo = new(SaveRealm.Local, 2);
    private static readonly SaveSlotRef LocalThree = new(SaveRealm.Local, 3);

    // ---- round trip ----

    [Fact]
    public void An_exported_save_imports_somewhere_else_byte_for_byte()
    {
        using var world = new LibraryWorld();
        using var outbox = new TempDirectory("outbox");
        using var elsewhere = new TempDirectory("other-library");
        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", "worth keeping");
        var bundle = outbox.Resolve("ironclaw.rwsave");

        world.Library.ExportEntry(entry, bundle);
        var other = new SaveLibrary(world.Backups, elsewhere.Path, world.Detector, LibraryWorld.AppVersion);
        var imported = other.ImportFile(bundle);

        Assert.True(imported.Success, string.Join("; ", imported.Errors));
        SnapshotLayout.AssertBytesEqual(
            File.ReadAllBytes(entry.SavePath), File.ReadAllBytes(imported.Entry!.SavePath), "save.bin");
        Assert.Equal(entry.Manifest!.Sha256, imported.Entry.Manifest!.Sha256);
    }

    [Fact]
    public void An_imported_save_keeps_its_name_note_and_campaigns()
    {
        using var world = new LibraryWorld();
        using var outbox = new TempDirectory("outbox");
        using var elsewhere = new TempDirectory("other-library");
        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", "worth keeping");
        var bundle = outbox.Resolve("ironclaw.rwsave");

        world.Library.ExportEntry(entry, bundle);
        var other = new SaveLibrary(world.Backups, elsewhere.Path, world.Detector, LibraryWorld.AppVersion);
        var imported = other.ImportFile(bundle).Entry!;

        Assert.Equal("Ironclaw run", imported.Name);
        Assert.Equal("worth keeping", imported.Manifest!.Note);
        Assert.Equal("sav2", imported.Manifest.SourceFileName);
        var campaign = Assert.Single(imported.Manifest.Metadata!.Campaigns);
        Assert.Equal("White", campaign.SlugcatId);
        Assert.Equal(17, campaign.CycleNum);
    }

    [Fact]
    public void An_imported_save_verifies_and_loads_like_any_other()
    {
        using var world = new LibraryWorld();
        using var outbox = new TempDirectory("outbox");
        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);
        var bundle = outbox.Resolve("ironclaw.rwsave");
        world.Library.ExportEntry(entry, bundle);
        world.Library.DeleteEntry(entry);

        var imported = world.Library.ImportFile(bundle).Entry!;
        var loaded = world.Library.LoadEntry(imported, LocalThree);

        Assert.True(world.Library.VerifyEntry(imported).Ok);
        Assert.True(loaded.Success, string.Join("; ", loaded.Errors));
        var metadata = SaveMetadataExtractor.Extract(world.Live.Resolve("sav3"), 3);
        Assert.True(metadata.ChecksumValid);
    }

    [Fact]
    public void An_imported_save_does_not_carry_the_load_history_of_the_machine_it_came_from()
    {
        using var world = new LibraryWorld();
        using var outbox = new TempDirectory("outbox");
        using var elsewhere = new TempDirectory("other-library");
        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);
        world.Library.LoadEntry(entry, LocalThree);
        var bundle = outbox.Resolve("ironclaw.rwsave");

        world.Library.ExportEntry(world.Reload(entry), bundle);
        var other = new SaveLibrary(world.Backups, elsewhere.Path, world.Detector, LibraryWorld.AppVersion);
        var imported = other.ImportFile(bundle).Entry!;

        Assert.Null(imported.Manifest!.LastLoadedSlotRef);
        Assert.Null(imported.Manifest.LastLoadedUtc);
    }

    [Fact]
    public void Exporting_over_an_existing_file_replaces_it()
    {
        using var world = new LibraryWorld();
        using var outbox = new TempDirectory("outbox");
        var first = world.Library.StoreSlot(LocalTwo, "first", null);
        var second = world.Library.StoreSlot(LocalThree, "second", null);
        var bundle = outbox.Resolve("save.rwsave");

        world.Library.ExportEntry(first, bundle);
        world.Library.ExportEntry(second, bundle);

        using var elsewhere = new TempDirectory("other-library");
        var other = new SaveLibrary(world.Backups, elsewhere.Path, world.Detector, LibraryWorld.AppVersion);
        Assert.Equal("second", other.ImportFile(bundle).Entry!.Name);
    }

    // ---- a bundle that arrived damaged ----

    [Fact]
    public void A_bundle_whose_save_was_rewritten_is_refused()
    {
        using var world = new LibraryWorld();
        using var outbox = new TempDirectory("outbox");
        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);
        var bundle = outbox.Resolve("ironclaw.rwsave");
        world.Library.ExportEntry(entry, bundle);
        world.Library.DeleteEntry(entry);

        ReplaceInsideBundle(bundle, LibraryEntry.SaveFileName, "not a save at all"u8.ToArray());
        var result = world.Library.ImportFile(bundle);

        Assert.Null(result.Entry);
        Assert.NotEmpty(result.Errors);
        Assert.Empty(world.Library.ListEntries());
    }

    [Fact]
    public void A_bundle_with_no_manifest_in_it_is_refused()
    {
        using var world = new LibraryWorld();
        using var outbox = new TempDirectory("outbox");
        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);
        var bundle = outbox.Resolve("ironclaw.rwsave");
        world.Library.ExportEntry(entry, bundle);
        world.Library.DeleteEntry(entry);

        RemoveFromBundle(bundle, LibraryEntry.ManifestFileName);
        var result = world.Library.ImportFile(bundle);

        Assert.Null(result.Entry);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void A_bundle_with_no_save_in_it_is_refused()
    {
        using var world = new LibraryWorld();
        using var outbox = new TempDirectory("outbox");
        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);
        var bundle = outbox.Resolve("ironclaw.rwsave");
        world.Library.ExportEntry(entry, bundle);
        world.Library.DeleteEntry(entry);

        RemoveFromBundle(bundle, LibraryEntry.SaveFileName);
        var result = world.Library.ImportFile(bundle);

        Assert.Null(result.Entry);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void A_refused_import_leaves_no_half_written_entry_behind()
    {
        using var world = new LibraryWorld();
        using var outbox = new TempDirectory("outbox");
        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);
        var bundle = outbox.Resolve("ironclaw.rwsave");
        world.Library.ExportEntry(entry, bundle);
        world.Library.DeleteEntry(entry);
        ReplaceInsideBundle(bundle, LibraryEntry.SaveFileName, "wrong"u8.ToArray());

        world.Library.ImportFile(bundle);

        Assert.Empty(Directory.GetDirectories(world.LibraryRoot.Path));
    }

    // ---- a bare save file ----

    [Fact]
    public void A_bare_save_file_imports_with_its_slot_worked_out_from_its_name()
    {
        using var world = new LibraryWorld();
        using var inbox = new TempDirectory("inbox");
        var source = FixtureFiles.CopyTo(inbox, FixtureFiles.Sav2, "sav2");

        var result = world.Library.ImportFile(source);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        var manifest = result.Entry!.Manifest!;
        Assert.Equal("sav2", manifest.Name);
        Assert.Equal("sav2", manifest.SourceFileName);
        Assert.Equal(SaveRealm.Local, manifest.SourceRealm);
        Assert.Equal(2, manifest.SourceSlot);
        SnapshotLayout.AssertBytesEqual(
            File.ReadAllBytes(source), File.ReadAllBytes(result.Entry.SavePath), "save.bin");
    }

    [Fact]
    public void A_bare_online_save_keeps_its_realm()
    {
        using var world = new LibraryWorld();
        using var inbox = new TempDirectory("inbox");
        var source = FixtureFiles.CopyTo(inbox, FixtureFiles.OnlineSav, "online_sav");

        var manifest = world.Library.ImportFile(source).Entry!.Manifest!;

        Assert.Equal(SaveRealm.Online, manifest.SourceRealm);
        Assert.Equal(1, manifest.SourceSlot);
    }

    [Fact]
    public void A_bare_save_under_a_name_that_is_not_a_slot_still_imports()
    {
        using var world = new LibraryWorld();
        using var inbox = new TempDirectory("inbox");
        var source = FixtureFiles.CopyTo(inbox, FixtureFiles.Sav2, "from a friend.sav");

        var result = world.Library.ImportFile(source);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal("from a friend", result.Entry!.Name);
        Assert.Equal(0, result.Entry.Manifest!.SourceSlot);
    }

    [Fact]
    public void A_bare_save_with_a_checksum_the_game_would_reject_imports_with_a_warning()
    {
        // Unlike a bundle there is no recorded hash to hold it to, and a save the game will not
        // load is still something the user may want in the library to look at.
        using var world = new LibraryWorld();
        using var inbox = new TempDirectory("inbox");
        var source = FixtureFiles.CopyTo(inbox, FixtureFiles.Sav2, "sav2");
        SaveLibraryTests.FlipOneByte(source);

        var result = world.Library.ImportFile(source);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.NotEmpty(result.Warnings);
    }

    // ---- neither one ----

    [Fact]
    public void A_file_that_is_neither_a_bundle_nor_a_save_is_refused()
    {
        using var world = new LibraryWorld();
        using var inbox = new TempDirectory("inbox");
        var source = inbox.WriteBytes("notes.txt", Encoding.UTF8.GetBytes("this is not a save"));

        var result = world.Library.ImportFile(source);

        Assert.Null(result.Entry);
        Assert.NotEmpty(result.Errors);
        Assert.Empty(world.Library.ListEntries());
    }

    [Fact]
    public void A_file_that_is_not_there_is_refused()
    {
        using var world = new LibraryWorld();
        using var inbox = new TempDirectory("inbox");

        var result = world.Library.ImportFile(inbox.Resolve("no-such-file.rwsave"));

        Assert.Null(result.Entry);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Importing_never_touches_the_save_folder()
    {
        using var world = new LibraryWorld();
        using var inbox = new TempDirectory("inbox");
        var before = world.Live.ReadTree();
        var source = FixtureFiles.CopyTo(inbox, FixtureFiles.Sav2, "sav2");

        world.Library.ImportFile(source);

        SnapshotLayout.AssertTreeUnchanged(before, world.Live.ReadTree());
    }

    [Fact]
    public void Exporting_never_touches_the_save_folder()
    {
        using var world = new LibraryWorld();
        using var outbox = new TempDirectory("outbox");
        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);
        var before = world.Live.ReadTree();

        world.Library.ExportEntry(entry, outbox.Resolve("out.rwsave"));

        SnapshotLayout.AssertTreeUnchanged(before, world.Live.ReadTree());
    }

    [Fact]
    public void Exporting_refuses_a_save_that_no_longer_matches_its_checksum()
    {
        using var world = new LibraryWorld();
        using var outbox = new TempDirectory("outbox");
        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);
        SaveLibraryTests.FlipOneByte(entry.SavePath);

        Assert.Throws<IOException>(
            () => world.Library.ExportEntry(world.Reload(entry), outbox.Resolve("out.rwsave")));

        Assert.False(File.Exists(outbox.Resolve("out.rwsave")));
    }

    /// <summary>Rewrites one file inside a bundle, standing in for damage in transit.</summary>
    private static void ReplaceInsideBundle(string bundlePath, string entryName, byte[] content)
    {
        using var archive = ZipFile.Open(bundlePath, ZipArchiveMode.Update);
        archive.GetEntry(entryName)?.Delete();

        var replacement = archive.CreateEntry(entryName);
        using var stream = replacement.Open();
        stream.Write(content, 0, content.Length);
    }

    private static void RemoveFromBundle(string bundlePath, string entryName)
    {
        using var archive = ZipFile.Open(bundlePath, ZipArchiveMode.Update);
        archive.GetEntry(entryName)?.Delete();
    }
}
