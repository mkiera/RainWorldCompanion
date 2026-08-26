using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Tests;

/// <summary>
/// Rain Meadow stores an online campaign in online_sav, online_sav2 and online_sav3, in the same
/// format and under the same slot numbering as sav, sav2 and sav3. Copying between the two is
/// therefore a whole-file copy and nothing else: the bytes are not decoded, no payload is
/// rewritten and no MD5 is recomputed, because recomputing one wrongly is what wipes a save.
///
/// Every test here proves one of two things. Either the bytes came across untouched, which is
/// checked by parsing the copy and verifying its checksum afterwards, or the copy was refused and
/// the save folder was left exactly as it was.
/// </summary>
public class SlotCopyTests
{
    private static readonly SaveSlotRef LocalOne = new(SaveRealm.Local, 1);
    private static readonly SaveSlotRef LocalTwo = new(SaveRealm.Local, 2);
    private static readonly SaveSlotRef LocalThree = new(SaveRealm.Local, 3);
    private static readonly SaveSlotRef OnlineOne = new(SaveRealm.Online, 1);
    private static readonly SaveSlotRef OnlineTwo = new(SaveRealm.Online, 2);
    private static readonly SaveSlotRef OnlineThree = new(SaveRealm.Online, 3);

    // ---- naming ----

    [Theory]
    [InlineData(SaveRealm.Local, 1, "sav")]
    [InlineData(SaveRealm.Local, 2, "sav2")]
    [InlineData(SaveRealm.Local, 3, "sav3")]
    [InlineData(SaveRealm.Online, 1, "online_sav")]
    [InlineData(SaveRealm.Online, 2, "online_sav2")]
    [InlineData(SaveRealm.Online, 3, "online_sav3")]
    public void A_slot_reference_knows_the_file_it_names(SaveRealm realm, int slot, string expected)
        => Assert.Equal(expected, new SaveSlotRef(realm, slot).FileName);

    [Theory]
    [InlineData("online_sav", 1)]
    [InlineData("online_sav2", 2)]
    [InlineData("online_sav3", 3)]
    public void The_online_containers_map_to_the_same_slot_numbers_as_the_local_ones(string fileName, int slot)
    {
        // Rain Meadow hooks Options.GetSaveFileName_SavOrExp and returns "online_sav" for save
        // slot 0 and "online_sav" + (slot + 1) above it, off the same Options.saveSlot the local
        // file comes from. Online slot 2 and local slot 2 are the same UI slot, so the realm is
        // what tells them apart and the number alone never does.
        Assert.Equal(new SaveSlotRef(SaveRealm.Online, slot), SaveMetadataExtractor.SlotForFileName(fileName));
        Assert.Equal(slot, SaveMetadataExtractor.SlotNumberForFileName(fileName));
    }

    [Theory]
    [InlineData("sav", 1)]
    [InlineData("sav2", 2)]
    [InlineData("sav3", 3)]
    public void The_local_containers_still_map_to_the_local_realm(string fileName, int slot)
    {
        Assert.Equal(new SaveSlotRef(SaveRealm.Local, slot), SaveMetadataExtractor.SlotForFileName(fileName));
        Assert.Equal(slot, SaveMetadataExtractor.SlotNumberForFileName(fileName));
    }

    [Theory]
    [InlineData("sav - Copy")]
    [InlineData("sav - Copy (2)")]
    [InlineData("sav.bak")]
    [InlineData("sav4")]
    [InlineData("sav0")]
    [InlineData("online_sav4")]
    [InlineData("online_sav0")]
    [InlineData("online_save")]
    [InlineData("save")]
    [InlineData("exp1")]
    [InlineData("expCore1")]
    [InlineData("meadow.json")]
    [InlineData("options")]
    [InlineData("steam_autocloud.vdf")]
    [InlineData("")]
    public void An_unknown_name_is_not_a_slot(string fileName)
    {
        Assert.Null(SaveMetadataExtractor.SlotForFileName(fileName));

        // SlotNumberForFileName is SlotForFileName()?.Slot, and callers that only want the
        // number read the null the same way.
        Assert.Null(SaveMetadataExtractor.SlotNumberForFileName(fileName));
    }

    // ---- copying ----

    [Fact]
    public void Copying_a_local_slot_onto_the_online_slot_of_the_same_number_produces_the_same_bytes()
    {
        using var world = new SlotWorld();
        var source = world.Live.ReadBytes("sav2");

        var result = world.Service.CopySlot(LocalTwo, OnlineTwo);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        SnapshotLayout.AssertBytesEqual(source, world.Live.ReadBytes("online_sav2"), "online_sav2");
    }

    [Fact]
    public void Copying_leaves_the_source_slot_untouched()
    {
        using var world = new SlotWorld();
        var source = world.Live.ReadBytes("sav2");

        world.Service.CopySlot(LocalTwo, OnlineTwo);

        SnapshotLayout.AssertBytesEqual(source, world.Live.ReadBytes("sav2"), "sav2");
    }

    [Fact]
    public void The_copy_still_parses_and_its_checksum_still_verifies()
    {
        // This is the assertion that proves nothing was rewritten. The digest covers the payload
        // plus the game's salt, so a payload that came across even one character short would
        // parse and then fail this check.
        using var world = new SlotWorld();

        world.Service.CopySlot(LocalTwo, OnlineTwo);

        var metadata = SaveMetadataExtractor.Extract(world.Live.Resolve("online_sav2"), 2);
        Assert.Null(metadata.ParseError);
        Assert.True(metadata.ChecksumValid);
        var campaign = Assert.Single(metadata.Campaigns);
        Assert.Equal("White", campaign.SlugcatId);
        Assert.Equal(17, campaign.CycleNum);
    }

    [Fact]
    public void Copying_the_other_way_works_the_same()
    {
        using var world = new SlotWorld();
        var source = world.Live.ReadBytes("online_sav2");

        var result = world.Service.CopySlot(OnlineTwo, LocalTwo);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        SnapshotLayout.AssertBytesEqual(source, world.Live.ReadBytes("sav2"), "sav2");

        var metadata = SaveMetadataExtractor.Extract(world.Live.Resolve("sav2"), 2);
        Assert.Null(metadata.ParseError);
        Assert.True(metadata.ChecksumValid);
    }

    [Fact]
    public void Copying_across_slot_numbers_works()
    {
        using var world = new SlotWorld();
        var source = world.Live.ReadBytes("sav");

        var result = world.Service.CopySlot(LocalOne, OnlineThree);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        SnapshotLayout.AssertBytesEqual(source, world.Live.ReadBytes("online_sav3"), "online_sav3");
        SnapshotLayout.AssertBytesEqual(source, world.Live.ReadBytes("sav"), "sav");
    }

    [Fact]
    public void Copying_over_a_slot_that_does_not_exist_yet_creates_it()
    {
        using var world = new SlotWorld();
        File.Delete(world.Live.Resolve("online_sav3"));
        var source = world.Live.ReadBytes("sav");

        var result = world.Service.CopySlot(LocalOne, OnlineThree);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        SnapshotLayout.AssertBytesEqual(source, world.Live.ReadBytes("online_sav3"), "online_sav3");
    }

    // ---- the safety copy ----

    [Fact]
    public void A_safety_snapshot_is_taken_before_the_copy()
    {
        using var world = new SlotWorld();

        var result = world.Service.CopySlot(LocalTwo, OnlineTwo);

        Assert.NotNull(result.SafetySnapshot);
        Assert.True(result.SafetySnapshot!.IsComplete, result.SafetySnapshot.Problem);
    }

    [Fact]
    public void The_safety_snapshot_holds_the_target_as_it_was_before_the_copy()
    {
        // Without this the copy is not undoable: the target's old bytes exist nowhere else once
        // it has been overwritten.
        using var world = new SlotWorld();
        var targetBefore = world.Live.ReadBytes("online_sav2");

        var result = world.Service.CopySlot(LocalTwo, OnlineTwo);

        var held = SnapshotLayout.FindFile(result.SafetySnapshot!, "online_sav2");
        Assert.NotNull(held);
        SnapshotLayout.AssertBytesEqual(targetBefore, File.ReadAllBytes(held!), "online_sav2 in the safety snapshot");
    }

    [Fact]
    public void The_safety_snapshot_verifies_against_its_own_manifest()
    {
        using var world = new SlotWorld();

        var result = world.Service.CopySlot(LocalTwo, OnlineTwo);
        var verification = world.Service.Verify(result.SafetySnapshot!);

        Assert.True(verification.Ok, string.Join("; ", verification.Problems));
    }

    // ---- refusals ----

    [Fact]
    public void Copying_refuses_while_the_game_is_running_and_touches_nothing()
    {
        using var world = new SlotWorld();
        world.Detector.RunningProcessName = "RainWorld";
        var before = world.Live.ReadTree();

        Assert.Throws<GameRunningException>(() => world.Service.CopySlot(LocalTwo, OnlineTwo));

        SnapshotLayout.AssertTreeUnchanged(before, world.Live.ReadTree());
        Assert.Empty(world.Service.ListBackups());
    }

    [Fact]
    public void Copying_refuses_when_the_source_slot_has_no_file()
    {
        using var world = new SlotWorld();
        File.Delete(world.Live.Resolve("sav3"));
        var before = world.Live.ReadTree();

        var result = world.Service.CopySlot(LocalThree, OnlineThree);

        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
        SnapshotLayout.AssertTreeUnchanged(before, world.Live.ReadTree());
    }

    [Fact]
    public void Copying_refuses_when_the_source_and_the_target_are_the_same_file()
    {
        using var world = new SlotWorld();
        var before = world.Live.ReadTree();

        var result = world.Service.CopySlot(LocalTwo, LocalTwo);

        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
        SnapshotLayout.AssertTreeUnchanged(before, world.Live.ReadTree());
    }

    [Fact]
    public void A_refused_copy_does_not_leave_a_safety_snapshot_behind()
    {
        // The refusals above are decided from the arguments and the folder, before anything is
        // written, so a refused copy costs the user nothing: no snapshot, no backup folder.
        using var world = new SlotWorld();
        File.Delete(world.Live.Resolve("sav3"));

        var result = world.Service.CopySlot(LocalThree, OnlineThree);

        Assert.Null(result.SafetySnapshot);
        Assert.Empty(world.Service.ListBackups());
    }

    [Fact]
    public void A_refused_copy_leaves_the_target_exactly_as_it_was()
    {
        using var world = new SlotWorld();
        File.Delete(world.Live.Resolve("sav3"));
        var targetBefore = world.Live.ReadBytes("online_sav3");

        world.Service.CopySlot(LocalThree, OnlineThree);

        SnapshotLayout.AssertBytesEqual(targetBefore, world.Live.ReadBytes("online_sav3"), "online_sav3");
    }

    [Fact]
    public void A_target_that_appears_while_the_safety_copy_runs_is_not_overwritten()
    {
        // The target is sampled once before the safety snapshot, and the snapshot then takes
        // seconds of scanning, copying and hashing. A file that arrives in that window, which is
        // what Steam Cloud syncing a save down from another machine looks like, is in neither the
        // sample nor the snapshot, so overwriting it would leave its bytes nowhere on disk.
        using var world = new SlotWorld(live =>
        {
            File.Delete(live.Resolve("online_sav3"));
            return new ScopeWithSideEffect(
                live.Path,
                onCall: 1,
                after: () => live.CopyFrom(FixtureFiles.PathTo(FixtureFiles.Sav3), "online_sav3"));
        });

        var result = world.Service.CopySlot(LocalOne, OnlineThree);

        Assert.False(result.Success);
        Assert.False(result.LiveFolderModified);
        Assert.NotEmpty(result.Errors);

        // The synced file is still the file that arrived, not the copy that was asked for.
        SnapshotLayout.AssertBytesEqual(
            FixtureFiles.Bytes(FixtureFiles.Sav3),
            world.Live.ReadBytes("online_sav3"),
            "online_sav3");

        // And the reason it was refused: the safety snapshot really does not hold it.
        Assert.NotNull(result.SafetySnapshot);
        Assert.Null(SnapshotLayout.FindFile(result.SafetySnapshot!, "online_sav3"));
    }

    [Fact]
    public void A_target_present_all_along_is_still_copied_over()
    {
        // The mirror image of the test above. The re-check must refuse only the file the safety
        // snapshot does not hold, not every target.
        using var world = new SlotWorld();

        var result = world.Service.CopySlot(LocalOne, OnlineThree);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        SnapshotLayout.AssertBytesEqual(
            world.Live.ReadBytes("sav"),
            world.Live.ReadBytes("online_sav3"),
            "online_sav3");
    }

    // ---- what a slot with no campaign in it is called ----

    [Fact]
    public void A_container_holding_map_and_progression_records_is_not_described_as_empty()
    {
        // The real online_sav is 12 KB of explored map plus the MISCPROG record and no SAVE STATE
        // at all. Calling that empty, next to a button that overwrites it, is wrong.
        using var world = new SlotWorld();

        var plan = world.Service.SlotCopies.PlanCopy(LocalTwo, OnlineOne);
        string target = plan.Target.Describe();

        Assert.Empty(plan.Target.Metadata!.Campaigns);
        Assert.True(plan.Target.Metadata.RecordCount > 0, "the fixture holds no records at all");
        Assert.Contains("map and progression", target, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("empty", target, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_container_whose_payload_is_only_the_digest_is_still_described_as_empty()
    {
        // An untouched online slot: the stored value is the 32 character digest and nothing after
        // it. That one really is empty and has to keep saying so.
        using var world = new SlotWorld();
        world.Live.WriteBytes("online_sav3", SyntheticSave.SaveFile(""));

        var plan = world.Service.SlotCopies.PlanCopy(LocalTwo, OnlineThree);

        Assert.Equal(0, plan.Target.Metadata!.RecordCount);
        Assert.Contains("empty", plan.Target.Describe(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Replacing_a_campaign_with_a_map_only_container_warns_without_calling_it_empty()
    {
        using var world = new SlotWorld();

        var plan = world.Service.SlotCopies.PlanCopy(OnlineOne, LocalTwo);

        Assert.Contains(
            plan.Warnings,
            warning => warning.Contains("only map and progression data", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A live folder holding all six slot files with different contents in each realm, so a copy
    /// that did nothing is not mistaken for a copy that worked.
    /// </summary>
    private sealed class SlotWorld : IDisposable
    {
        public SlotWorld()
            : this(null)
        {
        }

        /// <param name="buildScope">
        /// Given the populated live folder, returns the scope the service should use. The folder is
        /// already laid out when this runs, so a test can delete a file or arrange a side effect
        /// before the service ever looks at it.
        /// </param>
        public SlotWorld(Func<TempDirectory, BackupScope>? buildScope)
        {
            Live = new TempDirectory("live");
            BackupRoot = new TempDirectory("backups");
            WideSaveTree.Populate(Live);
            Detector = FakeGameDetector.NotRunning();

            Service = buildScope is null
                ? new BackupService(Live.Path, BackupRoot.Path, Detector, "1.0.0-test")
                : new BackupService(Live.Path, BackupRoot.Path, Detector, "1.0.0-test", buildScope(Live));
        }

        public TempDirectory Live { get; }

        public TempDirectory BackupRoot { get; }

        public FakeGameDetector Detector { get; }

        public BackupService Service { get; }

        public void Dispose()
        {
            Live.Dispose();
            BackupRoot.Dispose();
        }
    }
}
