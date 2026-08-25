using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Editing;
using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Tests;

/// <summary>
/// Writing an edit is the one operation in this app that puts bytes into the save folder that no
/// other file holds, so the safety snapshot taken before it is the only copy of what was there.
/// These check that the snapshot is always taken, that every refusal leaves the folder as it was,
/// and that what lands on disk is a file the game will accept.
/// </summary>
public class SaveSlotWriterTests
{
    private static readonly SaveSlotRef LocalTwo = new(SaveRealm.Local, 2);
    private static readonly SaveSlotRef LocalThree = new(SaveRealm.Local, 3);

    [Fact]
    public void An_edit_lands_on_disk_and_the_game_would_accept_it()
    {
        using var world = new EditWorld();
        var session = world.OpenEdit();
        session.SetField(session.Campaigns[0], "CYCLENUM", "1234");

        var result = world.Write(session);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        var metadata = SaveMetadataExtractor.Extract(world.Live.Resolve("sav2"), 2);
        Assert.Null(metadata.ParseError);
        Assert.True(metadata.ChecksumValid);
        Assert.Equal(1234, metadata.Campaigns[0].CycleNum);
    }

    [Fact]
    public void Writing_an_edit_always_takes_a_safety_snapshot_holding_the_slot_as_it_was()
    {
        using var world = new EditWorld();
        var before = world.Live.ReadBytes("sav2");
        var session = world.OpenEdit();
        session.SetField(session.Campaigns[0], "CYCLENUM", "1234");

        var result = world.Write(session);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        var safety = Assert.IsType<BackupSnapshot>(result.SafetySnapshot);
        Assert.Equal(BackupKind.PreRestoreSafety, safety.Manifest!.Kind);

        // The snapshot is the undo, so it has to hold the bytes that were replaced.
        var snapshotted = File.ReadAllBytes(Path.Combine(safety.DirectoryPath, "sav2"));
        SnapshotLayout.AssertBytesEqual(before, snapshotted, "sav2 in the safety snapshot");
    }

    [Fact]
    public void The_safety_snapshot_records_what_the_edit_was_about_to_do()
    {
        using var world = new EditWorld();
        var session = world.OpenEdit();
        session.SetField(session.Campaigns[0], "CYCLENUM", "1234");

        var result = world.Write(session);

        var note = result.SafetySnapshot!.Manifest!.Note;
        Assert.Contains("CYCLENUM", note, StringComparison.Ordinal);
        Assert.Contains("Survivor", note, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_edited_slot_is_written_to()
    {
        using var world = new EditWorld();
        var others = new[] { "sav", "sav3", "online_sav", "online_sav2", "exp1" }
            .ToDictionary(name => name, name => world.Live.ReadBytes(name));

        var session = world.OpenEdit();
        session.SetField(session.Campaigns[0], "CYCLENUM", "1234");
        world.Write(session);

        foreach (var (name, bytes) in others)
        {
            SnapshotLayout.AssertBytesEqual(bytes, world.Live.ReadBytes(name), name);
        }
    }

    [Fact]
    public void A_running_game_stops_the_write_before_anything_is_touched()
    {
        using var world = new EditWorld();
        var before = world.Live.ReadBytes("sav2");
        var session = world.OpenEdit();
        session.SetField(session.Campaigns[0], "CYCLENUM", "1234");

        world.Detector.RunningProcessName = "RainWorld";

        Assert.Throws<GameRunningException>(() => world.Write(session));
        SnapshotLayout.AssertBytesEqual(before, world.Live.ReadBytes("sav2"), "sav2");
    }

    /// <summary>
    /// The reason the session records a hash when it opens. An edit is built from the bytes that
    /// were in the slot, so writing it over a slot the game has played since would throw those
    /// cycles away and put back an edited copy of an older save.
    /// </summary>
    [Fact]
    public void A_slot_written_to_since_the_edit_began_is_refused()
    {
        using var world = new EditWorld();
        var session = world.OpenEdit();
        session.SetField(session.Campaigns[0], "CYCLENUM", "1234");

        // The game runs a cycle and saves while the editor is open.
        FixtureFiles.CopyTo(world.Live, FixtureFiles.Sav3, "sav2");
        var played = world.Live.ReadBytes("sav2");

        var result = world.Write(session);

        Assert.False(result.Success);
        Assert.False(result.LiveFolderModified);
        Assert.Contains(result.Errors, e => e.Contains("written to since", StringComparison.OrdinalIgnoreCase));
        SnapshotLayout.AssertBytesEqual(played, world.Live.ReadBytes("sav2"), "sav2");
    }

    [Fact]
    public void A_plan_with_problems_is_refused_and_says_so()
    {
        using var world = new EditWorld();
        var session = world.OpenEdit();
        var before = world.Live.ReadBytes("sav2");

        // Too long for the file, and the policy forbids growing it.
        session.SetField(session.Campaigns[0], "DENPOS", new string('x', 200_000));
        var plan = session.BuildWritePlan(SizePolicy.PreserveLength);

        Assert.NotEmpty(plan.Problems);

        var result = world.Service.SlotWriter.Write(plan, LocalTwo);

        Assert.False(result.Success);
        Assert.False(result.LiveFolderModified);
        SnapshotLayout.AssertBytesEqual(before, world.Live.ReadBytes("sav2"), "sav2");
    }

    [Fact]
    public void A_session_with_no_edits_has_nothing_to_save()
    {
        using var world = new EditWorld();
        var session = world.OpenEdit();

        var result = world.Write(session);

        Assert.False(result.Success);
        Assert.False(result.LiveFolderModified);
        Assert.Contains(result.Errors, e => e.Contains("nothing to save", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_edit_will_not_be_written_to_a_slot_it_did_not_come_from()
    {
        using var world = new EditWorld();
        var before = world.Live.ReadBytes("sav3");
        var session = world.OpenEdit();
        session.SetField(session.Campaigns[0], "CYCLENUM", "1234");

        var result = world.Service.SlotWriter.Write(session.BuildWritePlan(), LocalThree);

        Assert.False(result.Success);
        Assert.False(result.LiveFolderModified);
        Assert.Contains(result.Errors, e => e.Contains("sav2", StringComparison.Ordinal));
        SnapshotLayout.AssertBytesEqual(before, world.Live.ReadBytes("sav3"), "sav3");
    }

    /// <summary>
    /// A write that cannot open the slot has to be told apart from one that opened it and stopped
    /// half way. The ladder hashes the target afterwards to find out which happened, so this
    /// failure reports the slot as untouched rather than as possibly part written.
    /// </summary>
    [Fact]
    public void A_write_that_cannot_open_the_slot_leaves_it_alone_and_says_so()
    {
        using var world = new EditWorld();
        var before = world.Live.ReadBytes("sav2");
        var session = world.OpenEdit();
        session.SetField(session.Campaigns[0], "CYCLENUM", "1234");

        using (File.Open(world.Live.Resolve("sav2"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = world.Write(session);

            Assert.False(result.Success);
            Assert.NotEmpty(result.Errors);

            // The safety snapshot is taken before the write is attempted, so it exists either way.
            Assert.NotNull(result.SafetySnapshot);

            Assert.False(result.LiveFolderModified);
            Assert.Contains("nothing in the save folder was changed", result.Headline(), StringComparison.Ordinal);
        }

        SnapshotLayout.AssertBytesEqual(before, world.Live.ReadBytes("sav2"), "sav2");
    }

    [Fact]
    public void Nothing_is_left_in_the_save_folder_after_a_write()
    {
        using var world = new EditWorld();
        var before = Directory.GetFiles(world.Live.Path).Select(Path.GetFileName).OrderBy(n => n).ToArray();

        var session = world.OpenEdit();
        session.SetField(session.Campaigns[0], "CYCLENUM", "1234");
        world.Write(session);

        var after = Directory.GetFiles(world.Live.Path).Select(Path.GetFileName).OrderBy(n => n).ToArray();
        Assert.Equal(before, after);
    }

    [Fact]
    public void The_headline_never_says_nothing_changed_over_a_slot_that_was_written()
    {
        using var world = new EditWorld();
        var session = world.OpenEdit();
        session.SetField(session.Campaigns[0], "CYCLENUM", "1234");

        var result = world.Write(session);

        Assert.True(result.LiveFolderModified);
        Assert.DoesNotContain("nothing", result.Headline(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sav2", result.Headline(), StringComparison.Ordinal);
    }

    [Fact]
    public void Editing_the_same_slot_twice_in_a_row_works_from_a_fresh_session()
    {
        using var world = new EditWorld();

        var first = world.OpenEdit();
        first.SetField(first.Campaigns[0], "CYCLENUM", "100");
        Assert.True(world.Write(first).Success);

        var second = world.OpenEdit();
        second.SetField(second.Campaigns[0], "CYCLENUM", "200");
        var result = world.Write(second);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal(200, SaveMetadataExtractor.Extract(world.Live.Resolve("sav2"), 2).Campaigns[0].CycleNum);
    }

    /// <summary>A live folder with every slot file in it, and a service pointed at it.</summary>
    private sealed class EditWorld : IDisposable
    {
        public EditWorld()
        {
            Live = new TempDirectory("live");
            BackupRoot = new TempDirectory("backups");
            WideSaveTree.Populate(Live);
            Detector = FakeGameDetector.NotRunning();
            Service = new BackupService(Live.Path, BackupRoot.Path, Detector, "1.0.0-test");
        }

        public TempDirectory Live { get; }

        public TempDirectory BackupRoot { get; }

        public FakeGameDetector Detector { get; }

        public BackupService Service { get; }

        public SaveEditSession OpenEdit() => SaveEditSession.Open(Live.Resolve("sav2"));

        public SaveWriteResult Write(SaveEditSession session)
            => Service.SlotWriter.Write(session.BuildWritePlan(), LocalTwo);

        public void Dispose()
        {
            Live.Dispose();
            BackupRoot.Dispose();
        }
    }
}
