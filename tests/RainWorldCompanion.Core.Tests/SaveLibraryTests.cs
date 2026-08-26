using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Editing;
using RainWorldCompanion.Core.Library;
using RainWorldCompanion.Core.Mods;
using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Tests;

/// <summary>
/// The library keeps named saves outside the game's three slots. Every test here proves one of two
/// things: the bytes came across untouched, or the operation was refused and left both the save
/// folder and the entry exactly as they were.
///
/// Storing, renaming, deleting and updating never write into the save folder at all. Only a load
/// does, and that has its own file.
/// </summary>
public class SaveLibraryTests
{
    private static readonly SaveSlotRef LocalOne = new(SaveRealm.Local, 1);
    private static readonly SaveSlotRef LocalTwo = new(SaveRealm.Local, 2);
    private static readonly SaveSlotRef LocalThree = new(SaveRealm.Local, 3);

    // ---- storing ----

    [Fact]
    public void Storing_a_slot_copies_its_bytes_exactly()
    {
        using var world = new LibraryWorld();
        var source = world.Live.ReadBytes("sav2");

        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);

        SnapshotLayout.AssertBytesEqual(source, File.ReadAllBytes(entry.SavePath), "save.bin");
    }

    [Fact]
    public void The_stored_save_still_parses_and_its_checksum_still_verifies()
    {
        // The digest covers the payload plus the game's salt, so a payload that came across even
        // one character short would parse and then fail this check.
        using var world = new LibraryWorld();

        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);

        var metadata = SaveMetadataExtractor.Extract(entry.SavePath, 2);
        Assert.Null(metadata.ParseError);
        Assert.True(metadata.ChecksumValid);
    }

    [Fact]
    public void Storing_records_the_name_the_note_and_where_it_came_from()
    {
        using var world = new LibraryWorld();

        var entry = world.Library.StoreSlot(LocalTwo, "  Ironclaw run  ", "  most of a saint run  ");

        var manifest = Assert.IsType<LibraryManifest>(entry.Manifest);
        Assert.Equal("Ironclaw run", manifest.Name);
        Assert.Equal("most of a saint run", manifest.Note);
        Assert.Equal("sav2", manifest.SourceFileName);
        Assert.Equal(SaveRealm.Local, manifest.SourceRealm);
        Assert.Equal(2, manifest.SourceSlot);
        Assert.Equal(LibraryManifest.CurrentSchemaVersion, manifest.SchemaVersion);
    }

    [Fact]
    public void Storing_records_the_campaigns_so_listing_costs_no_disk_read()
    {
        using var world = new LibraryWorld();

        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);

        var metadata = Assert.IsType<Core.Saves.Models.SlotMetadata>(entry.Manifest!.Metadata);
        Assert.Null(metadata.ParseError);
        var campaign = Assert.Single(metadata.Campaigns);
        Assert.Equal("White", campaign.SlugcatId);
        Assert.Equal(17, campaign.CycleNum);
    }

    [Fact]
    public void The_recorded_size_and_hash_match_the_file_that_was_written()
    {
        using var world = new LibraryWorld();

        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);

        Assert.Equal(new FileInfo(entry.SavePath).Length, entry.Manifest!.SizeBytes);
        Assert.Equal(SnapshotLayout.Sha256(entry.SavePath), entry.Manifest.Sha256);
    }

    [Fact]
    public void Storing_leaves_the_save_folder_exactly_as_it_was()
    {
        using var world = new LibraryWorld();
        var before = world.Live.ReadTree();

        world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);

        SnapshotLayout.AssertTreeUnchanged(before, world.Live.ReadTree());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Storing_refuses_a_name_that_is_not_a_name(string name)
    {
        using var world = new LibraryWorld();

        Assert.Throws<ArgumentException>(() => world.Library.StoreSlot(LocalTwo, name, null));
        Assert.Empty(world.Library.ListEntries());
    }

    [Fact]
    public void Storing_refuses_while_the_game_is_running()
    {
        // Throws rather than reporting, the same as CreateBackup and CopySlot, so one handler
        // covers a running game wherever it is met.
        using var world = new LibraryWorld(FakeGameDetector.Running());

        Assert.Throws<GameRunningException>(() => world.Library.StoreSlot(LocalTwo, "Ironclaw run", null));
        Assert.Empty(world.Library.ListEntries());
    }

    [Fact]
    public void Storing_refuses_a_slot_with_no_file_in_it()
    {
        using var world = new LibraryWorld();
        File.Delete(world.Live.Resolve("sav3"));

        Assert.Throws<FileNotFoundException>(() => world.Library.StoreSlot(LocalThree, "nothing", null));
    }

    [Fact]
    public void Storing_refuses_a_slot_number_the_game_does_not_have()
    {
        using var world = new LibraryWorld();

        Assert.Throws<ArgumentException>(
            () => world.Library.StoreSlot(new SaveSlotRef(SaveRealm.Local, 4), "nowhere", null));
    }

    [Fact]
    public void Two_saves_stored_in_the_same_second_get_their_own_folders()
    {
        // Directory.CreateDirectory succeeds on a folder that is already there, so the claim file
        // is what settles which of the two owns the name.
        using var world = new LibraryWorld();

        var first = world.Library.StoreSlot(LocalTwo, "first", null);
        var second = world.Library.StoreSlot(LocalTwo, "second", null);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, world.Library.ListEntries().Count);
    }

    // ---- listing and the unfinished entry ----

    [Fact]
    public void An_entry_folder_with_no_manifest_lists_as_unfinished()
    {
        using var world = new LibraryWorld();
        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);
        File.Delete(entry.ManifestPath);

        var listed = Assert.Single(world.Library.ListEntries());

        Assert.False(listed.IsComplete);
        Assert.NotNull(listed.Problem);
        Assert.Equal(listed.Id, listed.Name);
    }

    [Fact]
    public void An_entry_folder_with_no_save_lists_as_unfinished()
    {
        using var world = new LibraryWorld();
        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);
        File.Delete(entry.SavePath);

        var listed = Assert.Single(world.Library.ListEntries());

        Assert.False(listed.IsComplete);
    }

    [Fact]
    public void An_unfinished_entry_cannot_be_renamed_or_exported()
    {
        using var world = new LibraryWorld();
        using var outbox = new TempDirectory("outbox");
        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);
        File.Delete(entry.ManifestPath);
        var broken = Assert.Single(world.Library.ListEntries());

        Assert.Throws<InvalidOperationException>(() => world.Library.RenameEntry(broken, "new name", null));
        Assert.Throws<InvalidOperationException>(() => world.Library.ExportEntry(broken, outbox.Resolve("out.rwsave")));
    }

    [Fact]
    public void Listing_puts_the_newest_first()
    {
        using var world = new LibraryWorld();
        world.Library.StoreSlot(LocalTwo, "older", null);
        var newer = world.Library.StoreSlot(LocalTwo, "newer", null);

        var entries = world.Library.ListEntries();

        Assert.Equal(newer.Id, entries[0].Id);
    }

    [Fact]
    public void A_library_root_that_is_not_there_lists_as_empty()
    {
        using var live = new TempDirectory("live");
        using var backups = new TempDirectory("backups");
        using var parent = new TempDirectory("library-parent");
        SaveTree.Populate(live);

        var detector = FakeGameDetector.NotRunning();
        var service = new BackupService(live.Path, backups.Path, detector, LibraryWorld.AppVersion);
        var library = new SaveLibrary(service, parent.Resolve("no-such-library"), detector, LibraryWorld.AppVersion);

        Assert.Empty(library.ListEntries());
    }

    // ---- verifying ----

    [Fact]
    public void Verifying_passes_on_a_save_that_has_not_been_touched()
    {
        using var world = new LibraryWorld();
        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);

        Assert.True(world.Library.VerifyEntry(entry).Ok);
    }

    [Fact]
    public void Verifying_catches_a_single_flipped_byte()
    {
        using var world = new LibraryWorld();
        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);
        FlipOneByte(entry.SavePath);

        var result = world.Library.VerifyEntry(world.Reload(entry));

        Assert.False(result.Ok);
        Assert.NotEmpty(result.Problems);
    }

    [Fact]
    public void Verifying_catches_a_save_that_went_away()
    {
        using var world = new LibraryWorld();
        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);
        File.Delete(entry.SavePath);

        Assert.False(world.Library.VerifyEntry(entry).Ok);
    }

    // ---- renaming ----

    [Fact]
    public void Renaming_changes_the_name_and_nothing_else()
    {
        using var world = new LibraryWorld();
        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", "first note");
        var bytesBefore = File.ReadAllBytes(entry.SavePath);

        var renamed = world.Library.RenameEntry(entry, "Saint attempt", "second note");

        Assert.Equal("Saint attempt", renamed.Name);
        Assert.Equal("second note", renamed.Manifest!.Note);
        Assert.Equal(entry.Id, renamed.Id);
        Assert.Equal(entry.DirectoryPath, renamed.DirectoryPath);
        SnapshotLayout.AssertBytesEqual(bytesBefore, File.ReadAllBytes(renamed.SavePath), "save.bin");
        Assert.Equal(entry.Manifest!.Sha256, renamed.Manifest.Sha256);
    }

    [Fact]
    public void Renaming_accepts_a_name_no_folder_could_have()
    {
        // The folder is named for the time, so a name full of characters Windows refuses in a path
        // is only ever written into json.
        using var world = new LibraryWorld();
        var entry = world.Library.StoreSlot(LocalTwo, "first", null);

        var renamed = world.Library.RenameEntry(entry, @"CON: <what?> / \ ""quoted""", null);

        Assert.Equal(@"CON: <what?> / \ ""quoted""", renamed.Name);
        Assert.True(File.Exists(renamed.SavePath));
    }

    [Fact]
    public void Renaming_refuses_a_blank_name()
    {
        using var world = new LibraryWorld();
        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);

        Assert.Throws<ArgumentException>(() => world.Library.RenameEntry(entry, "   ", null));
        Assert.Equal("Ironclaw run", world.Reload(entry).Name);
    }

    // ---- deleting ----

    [Fact]
    public void Deleting_removes_the_folder_and_leaves_the_rest()
    {
        using var world = new LibraryWorld();
        var doomed = world.Library.StoreSlot(LocalTwo, "doomed", null);
        var keep = world.Library.StoreSlot(LocalTwo, "keep", null);

        world.Library.DeleteEntry(doomed);

        Assert.False(Directory.Exists(doomed.DirectoryPath));
        Assert.Equal(keep.Id, Assert.Single(world.Library.ListEntries()).Id);
    }

    [Fact]
    public void Deleting_an_entry_that_is_already_gone_does_nothing()
    {
        using var world = new LibraryWorld();
        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);
        Directory.Delete(entry.DirectoryPath, recursive: true);

        world.Library.DeleteEntry(entry);

        Assert.Empty(world.Library.ListEntries());
    }

    [Fact]
    public void Deleting_refuses_an_entry_from_another_library()
    {
        using var world = new LibraryWorld();
        using var otherRoot = new TempDirectory("other-library");
        var otherLibrary = new SaveLibrary(world.Backups, otherRoot.Path, world.Detector, LibraryWorld.AppVersion);
        var foreign = otherLibrary.StoreSlot(LocalTwo, "somebody else's", null);

        Assert.Throws<InvalidOperationException>(() => world.Library.DeleteEntry(foreign));

        Assert.True(Directory.Exists(foreign.DirectoryPath));
        Assert.True(File.Exists(foreign.SavePath));
    }

    [Fact]
    public void Deleting_refuses_the_library_root_itself()
    {
        using var world = new LibraryWorld();
        world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);
        var root = LibraryEntry.Load(world.LibraryRoot.Path);

        Assert.Throws<InvalidOperationException>(() => world.Library.DeleteEntry(root));

        Assert.True(Directory.Exists(world.LibraryRoot.Path));
    }

    // ---- updating ----

    [Fact]
    public void Updating_replaces_the_save_with_what_is_in_the_slot_now()
    {
        using var world = new LibraryWorld();
        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);
        var replacement = world.Live.ReadBytes("sav3");

        var updated = world.Library.UpdateEntry(entry, LocalThree);

        SnapshotLayout.AssertBytesEqual(replacement, File.ReadAllBytes(updated.SavePath), "save.bin");
        Assert.Equal(SnapshotLayout.Sha256(updated.SavePath), updated.Manifest!.Sha256);
        Assert.Equal("sav3", updated.Manifest.SourceFileName);
    }

    [Fact]
    public void Updating_keeps_the_save_it_replaced()
    {
        using var world = new LibraryWorld();
        var original = world.Live.ReadBytes("sav2");
        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);
        var originalHash = entry.Manifest!.Sha256;

        var updated = world.Library.UpdateEntry(entry, LocalThree);

        Assert.True(updated.HasPrevious);
        Assert.Equal(originalHash, updated.Manifest!.PreviousSha256);
        SnapshotLayout.AssertBytesEqual(original, File.ReadAllBytes(updated.PreviousSavePath), "save.previous.bin");
    }

    [Fact]
    public void Updating_keeps_the_name_and_the_note()
    {
        using var world = new LibraryWorld();
        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", "worth keeping");

        var updated = world.Library.UpdateEntry(entry, LocalThree);

        Assert.Equal("Ironclaw run", updated.Name);
        Assert.Equal("worth keeping", updated.Manifest!.Note);
        Assert.Equal(entry.Id, updated.Id);
    }

    [Fact]
    public void Updating_leaves_the_save_folder_exactly_as_it_was()
    {
        using var world = new LibraryWorld();
        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);
        var before = world.Live.ReadTree();

        world.Library.UpdateEntry(entry, LocalThree);

        SnapshotLayout.AssertTreeUnchanged(before, world.Live.ReadTree());
    }

    [Fact]
    public void Updating_refuses_while_the_game_is_running()
    {
        using var world = new LibraryWorld();
        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);
        var bytesBefore = File.ReadAllBytes(entry.SavePath);
        world.Detector.RunningProcessName = "RainWorld";

        Assert.Throws<GameRunningException>(() => world.Library.UpdateEntry(entry, LocalThree));

        SnapshotLayout.AssertBytesEqual(bytesBefore, File.ReadAllBytes(entry.SavePath), "save.bin");
    }

    [Fact]
    public void Undoing_an_update_puts_the_earlier_save_back_byte_for_byte()
    {
        using var world = new LibraryWorld();
        var original = world.Live.ReadBytes("sav2");
        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);
        var originalHash = entry.Manifest!.Sha256;
        var updated = world.Library.UpdateEntry(entry, LocalThree);

        var undone = world.Library.UndoUpdate(updated);

        SnapshotLayout.AssertBytesEqual(original, File.ReadAllBytes(undone.SavePath), "save.bin");
        Assert.Equal(originalHash, undone.Manifest!.Sha256);
        Assert.True(world.Library.VerifyEntry(undone).Ok);
    }

    [Fact]
    public void An_undone_update_leaves_nothing_to_undo_a_second_time()
    {
        using var world = new LibraryWorld();
        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);
        var undone = world.Library.UndoUpdate(world.Library.UpdateEntry(entry, LocalThree));

        Assert.False(undone.HasPrevious);
        Assert.Throws<InvalidOperationException>(() => world.Library.UndoUpdate(undone));
    }

    [Fact]
    public void Undoing_refuses_when_nothing_was_ever_replaced()
    {
        using var world = new LibraryWorld();
        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);

        Assert.Throws<InvalidOperationException>(() => world.Library.UndoUpdate(entry));
    }

    [Fact]
    public void Undoing_refuses_an_earlier_save_that_no_longer_matches_its_checksum()
    {
        using var world = new LibraryWorld();
        var entry = world.Library.StoreSlot(LocalTwo, "Ironclaw run", null);
        var updated = world.Library.UpdateEntry(entry, LocalThree);
        var currentBytes = File.ReadAllBytes(updated.SavePath);
        FlipOneByte(updated.PreviousSavePath);

        Assert.Throws<IOException>(() => world.Library.UndoUpdate(world.Reload(updated)));

        SnapshotLayout.AssertBytesEqual(currentBytes, File.ReadAllBytes(updated.SavePath), "save.bin");
    }

    // ---- the service refuses a library that overlaps another folder ----

    [Fact]
    public void A_library_inside_the_save_folder_is_refused()
    {
        using var live = new TempDirectory("live");
        using var backups = new TempDirectory("backups");
        SaveTree.Populate(live);
        var detector = FakeGameDetector.NotRunning();
        var service = new BackupService(live.Path, backups.Path, detector, LibraryWorld.AppVersion);

        Assert.Throws<ArgumentException>(
            () => new SaveLibrary(service, live.Resolve("library"), detector, LibraryWorld.AppVersion));
    }

    [Fact]
    public void A_library_inside_the_backup_folder_is_refused()
    {
        using var live = new TempDirectory("live");
        using var backups = new TempDirectory("backups");
        SaveTree.Populate(live);
        var detector = FakeGameDetector.NotRunning();
        var service = new BackupService(live.Path, backups.Path, detector, LibraryWorld.AppVersion);

        Assert.Throws<ArgumentException>(
            () => new SaveLibrary(service, backups.Resolve("library"), detector, LibraryWorld.AppVersion));
    }

    internal static void FlipOneByte(string path)
    {
        var bytes = File.ReadAllBytes(path);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
    }
}

/// <summary>
/// A live save folder, a backup root and a library root, all throwaway. Nothing here can reach the
/// real Rain World save folder.
/// </summary>
internal sealed class LibraryWorld : IDisposable
{
    public const string AppVersion = "1.0.0-test";

    public LibraryWorld(FakeGameDetector? detector = null, Func<CurrentMods>? modListSource = null)
    {
        Live = new TempDirectory("live");
        BackupRoot = new TempDirectory("backups");
        LibraryRoot = new TempDirectory("library");
        SaveTree.Populate(Live);
        Detector = detector ?? FakeGameDetector.NotRunning();

        Backups = new BackupService(Live.Path, BackupRoot.Path, Detector, AppVersion, modListSource);
        Library = new SaveLibrary(Backups, LibraryRoot.Path, Detector, AppVersion);
    }

    public TempDirectory Live { get; }

    public TempDirectory BackupRoot { get; }

    public TempDirectory LibraryRoot { get; }

    public FakeGameDetector Detector { get; }

    public BackupService Backups { get; }

    public SaveLibrary Library { get; }

    /// <summary>Reads an entry back off disk, for a test that changed it behind the service.</summary>
    public LibraryEntry Reload(LibraryEntry entry) => LibraryEntry.Load(entry.DirectoryPath);

    /// <summary>
    /// Puts a slot holding one campaign for a chosen slugcat in the save folder. Every fixture is a
    /// Survivor campaign, so a test about two campaigns in one slot has to make the second one.
    /// </summary>
    public void Seed(string fileName, string slugcat, int cycle = 12)
        => Live.WriteBytes(fileName, SyntheticSave.SaveFile(SyntheticSave.SavePayload(slugcat, cycle)));

    /// <summary>Moves one field of a live slot, standing in for the game having played it.</summary>
    public void PlayASlot(SaveSlotRef slot, string key, string value)
    {
        var session = SaveEditSession.Open(Live.Resolve(slot.FileName));
        session.SetField(session.Campaigns[0], key, value);
        Backups.SlotWriter.Write(session.BuildWritePlan(), slot);
    }

    public void Dispose()
    {
        Live.Dispose();
        BackupRoot.Dispose();
        LibraryRoot.Dispose();
    }
}
