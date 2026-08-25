using RainWorldSaveManager.Core.Backups;
using RainWorldSaveManager.Core.Editing;
using RainWorldSaveManager.Core.Saves;
using RainWorldSaveManager.Core.Saves.Models;

namespace RainWorldSaveManager.Tests;

/// <summary>
/// Moving a campaign onto a live slot.
///
/// The write runs the same ladder an edit runs, so what is checked here is the part that is new: a
/// change to the record list rather than to the characters inside one record. The plan proves the
/// splice did what it said and nothing else, and these prove the plan would notice if it had not.
/// </summary>
public class CampaignMoveTests
{
    private static readonly SaveSlotRef LocalTwo = new(SaveRealm.Local, 2);

    // ---- onto a live slot ----

    [Fact]
    public void A_campaign_loaded_onto_a_slot_lands_on_disk_and_the_game_would_accept_it()
    {
        using var world = new MoveWorld();
        CampaignSlice slice = world.CampaignIn("sav3", "White");

        SaveEditSession session = world.OpenSlotTwo();
        session.PutCampaignIn(slice);

        SaveWriteResult result = world.Write(session);
        Assert.True(result.Success, string.Join("; ", result.Errors));

        SlotMetadata metadata = SaveMetadataExtractor.Extract(world.Live.Resolve("sav2"), 2);
        Assert.Null(metadata.ParseError);
        Assert.True(metadata.ChecksumValid);

        // The campaign that landed is the one from sav3, not the one sav2 had.
        CampaignSummary loaded = Assert.Single(metadata.Campaigns);
        Assert.Equal(world.CampaignSummaryIn("sav3", 3).CycleNum, loaded.CycleNum);
    }

    [Fact]
    public void A_campaign_the_slot_did_not_have_joins_the_one_it_did()
    {
        using var world = new MoveWorld();

        SaveEditSession session = world.OpenSlotTwo();
        CampaignSpliceReport report = session.PutCampaignIn(GourmandSlice());

        Assert.Equal(CampaignSpliceOutcome.Added, report.Outcome);
        Assert.True(world.Write(session).Success);

        SlotMetadata metadata = SaveMetadataExtractor.Extract(world.Live.Resolve("sav2"), 2);
        Assert.Equal(new[] { "White", "Gourmand" }, metadata.Campaigns.Select(c => c.SlugcatId));
    }

    [Fact]
    public void A_campaign_taken_out_leaves_a_slot_the_game_still_reads()
    {
        using var world = new MoveWorld();

        SaveEditSession session = world.OpenSlotTwo();
        CampaignSpliceReport report = session.TakeCampaignOut("White", includeMaps: false);

        Assert.Equal(CampaignSpliceOutcome.Removed, report.Outcome);
        Assert.True(world.Write(session).Success);

        SlotMetadata metadata = SaveMetadataExtractor.Extract(world.Live.Resolve("sav2"), 2);
        Assert.Null(metadata.ParseError);
        Assert.True(metadata.ChecksumValid);
        Assert.Empty(metadata.Campaigns);

        // The map stays, the way WipeSaveState leaves it, so the slot is a played one with no
        // campaign in it rather than an empty file.
        Assert.True(metadata.RecordCount > 1);
    }

    [Fact]
    public void The_map_goes_too_when_the_campaign_is_being_moved_away()
    {
        using var world = new MoveWorld();

        SaveEditSession session = world.OpenSlotTwo();
        CampaignSpliceReport report = session.TakeCampaignOut("White", includeMaps: true);

        Assert.Equal(7, report.MapsRemoved);
        Assert.True(world.Write(session).Success);

        SlotMetadata metadata = SaveMetadataExtractor.Extract(world.Live.Resolve("sav2"), 2);
        Assert.Null(metadata.ParseError);
        Assert.Empty(metadata.Campaigns);
        Assert.DoesNotContain("MAP_White", world.PayloadOf("sav2"), StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_slot_being_written_to_is_touched()
    {
        using var world = new MoveWorld();
        Dictionary<string, byte[]> others = new[] { "sav", "sav3", "online_sav", "exp1" }
            .ToDictionary(name => name, name => world.Live.ReadBytes(name));

        SaveEditSession session = world.OpenSlotTwo();
        session.PutCampaignIn(GourmandSlice());
        world.Write(session);

        foreach ((string name, byte[] bytes) in others)
        {
            SnapshotLayout.AssertBytesEqual(bytes, world.Live.ReadBytes(name), name);
        }
    }

    [Fact]
    public void The_safety_snapshot_says_which_campaign_moved()
    {
        using var world = new MoveWorld();
        byte[] before = world.Live.ReadBytes("sav2");

        SaveEditSession session = world.OpenSlotTwo();
        session.PutCampaignIn(GourmandSlice());

        SaveWriteResult result = world.Write(session);

        BackupSnapshot safety = Assert.IsType<BackupSnapshot>(result.SafetySnapshot);
        Assert.Contains("Gourmand", safety.Manifest!.Note, StringComparison.Ordinal);
        SnapshotLayout.AssertBytesEqual(
            before,
            File.ReadAllBytes(Path.Combine(safety.DirectoryPath, "sav2")),
            "sav2 in the safety snapshot");
    }

    [Fact]
    public void What_moved_is_written_down_in_words()
    {
        using var world = new MoveWorld();
        SaveEditSession session = world.OpenSlotTwo();

        session.PutCampaignIn(world.CampaignIn("sav3", "White"));
        Assert.Equal(new[] { "Survivor: replaced this campaign" }, session.Changes);

        session.TakeCampaignOut("Gourmand", includeMaps: true);
        Assert.Single(session.Changes);
    }

    // ---- what the plan holds a splice to ----

    /// <summary>
    /// Every record the move was not about has to come back in the same order, and here they are
    /// every record in the file: the campaign put back is the one taken out.
    /// </summary>
    [Fact]
    public void Taking_a_campaign_out_and_putting_it_straight_back_is_a_plan_with_no_problems()
    {
        using var world = new MoveWorld();
        SaveEditSession session = world.OpenSlotTwo();

        CampaignSlice slice = session.TakeCampaign("White")!;
        session.TakeCampaignOut("White", includeMaps: true);
        session.PutCampaignIn(slice);

        SaveWritePlan plan = session.BuildWritePlan();

        Assert.Empty(plan.Problems);
        Assert.Equal(
            CampaignSplicer.Campaigns(world.PayloadOf("sav2")),
            CampaignSplicer.Campaigns(session.Payload));
    }

    [Fact]
    public void A_splice_that_lost_a_record_it_was_not_meant_to_touch_is_refused()
    {
        using var world = new MoveWorld();
        SaveEditSession session = world.OpenSlotTwo();

        string mangled = string.Join(
            SavePayloadReader.RecordSeparator,
            world.PayloadOf("sav2")
                .Split(SavePayloadReader.RecordSeparator, StringSplitOptions.None)
                .Where(record => !record.StartsWith("MISCPROG", StringComparison.Ordinal)));

        SaveWritePlan plan = world.PlanFor(session, mangled, new RecordSetChange(
            Array.Empty<string>(),
            Array.Empty<string>()));

        Assert.Contains(plan.Problems, problem => problem.Contains("other records", StringComparison.Ordinal));
        Assert.False(plan.CanWrite);
    }

    [Fact]
    public void A_splice_that_rewrote_a_record_it_was_not_meant_to_touch_names_it()
    {
        using var world = new MoveWorld();
        SaveEditSession session = world.OpenSlotTwo();

        string mangled = world.PayloadOf("sav2").Replace(
            "MISCPROG" + SavePayloadReader.HeaderSeparator,
            "MISCPROG" + SavePayloadReader.HeaderSeparator + "MEDDLED<misA>1<misA>",
            StringComparison.Ordinal);

        SaveWritePlan plan = world.PlanFor(session, mangled, new RecordSetChange(
            Array.Empty<string>(),
            Array.Empty<string>()));

        Assert.Contains(plan.Problems, problem => problem.Contains("'MISCPROG'", StringComparison.Ordinal));
    }

    /// <summary>
    /// Records are addressed by position while only their contents change, and by what moved once
    /// the list itself changes. A session that did both has no single answer to check against.
    /// </summary>
    [Fact]
    public void Editing_a_field_and_moving_a_campaign_in_one_session_is_refused()
    {
        using var world = new MoveWorld();
        SaveEditSession session = world.OpenSlotTwo();

        session.SetField(session.Campaigns[0], "CYCLENUM", "1234");
        session.PutCampaignIn(GourmandSlice());

        SaveWritePlan plan = session.BuildWritePlan();

        Assert.False(plan.CanWrite);
        Assert.Contains(plan.Problems, problem => problem.Contains("one at a time", StringComparison.Ordinal));
    }

    [Fact]
    public void A_campaign_that_is_not_there_to_take_out_leaves_the_session_clean()
    {
        using var world = new MoveWorld();
        SaveEditSession session = world.OpenSlotTwo();

        CampaignSpliceReport report = session.TakeCampaignOut("Saint", includeMaps: true);

        Assert.Equal(CampaignSpliceOutcome.NotFound, report.Outcome);
        Assert.False(session.IsDirty);
        Assert.Empty(session.Changes);
    }

    // ---- reading a campaign on its own ----

    [Fact]
    public void A_campaign_taken_out_on_its_own_reads_back_as_the_campaign_it_was()
    {
        using var world = new MoveWorld();
        CampaignSlice slice = world.CampaignIn("sav3", "White");

        string payload = slice.SaveStateRecord + SavePayloadReader.RecordSeparator;
        SlotMetadata metadata = SaveMetadataExtractor.FromPayload(payload, "campaign.bin", 0, SaveRealm.Local);

        CampaignSummary campaign = Assert.Single(metadata.Campaigns);
        Assert.Equal("White", campaign.SlugcatId);
        Assert.Equal(world.CampaignSummaryIn("sav3", 3).CycleNum, campaign.CycleNum);

        // No container round it, so there is no digest to have an opinion about.
        Assert.Null(metadata.ChecksumValid);
        Assert.Null(metadata.ParseError);
    }

    [Fact]
    public void A_payload_with_nothing_in_it_reads_as_nothing_rather_than_as_a_failure()
    {
        SlotMetadata metadata = SaveMetadataExtractor.FromPayload("", "campaign.bin", 0, SaveRealm.Local);

        Assert.Empty(metadata.Campaigns);
        Assert.Null(metadata.ParseError);
    }

    // ---- helpers ----

    private static CampaignSlice GourmandSlice() => new(
        "Gourmand",
        "SAVE STATE" + SyntheticSave.HeaderSeparator + SyntheticSave.SaveStateBody("Gourmand", cycle: 42),
        new[] { "MAP_Gourmand" + SyntheticSave.HeaderSeparator + "HI" + SyntheticSave.HeaderSeparator + "map" });

    private sealed class MoveWorld : IDisposable
    {
        public MoveWorld()
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

        public SaveEditSession OpenSlotTwo() => SaveEditSession.Open(Live.Resolve("sav2"));

        public SaveWriteResult Write(SaveEditSession session)
            => Service.SlotWriter.Write(session.BuildWritePlan(), LocalTwo);

        public string PayloadOf(string fileName)
        {
            SaveContainer container = SaveContainer.Read(Live.Resolve(fileName));
            SaveChecksum.TryUnwrap(container.Entries["save"], out string payload, out _);
            return payload;
        }

        public CampaignSlice CampaignIn(string fileName, string slugcat)
            => CampaignSplicer.Extract(PayloadOf(fileName), slugcat)!;

        public CampaignSummary CampaignSummaryIn(string fileName, int slot)
            => SaveMetadataExtractor.Extract(Live.Resolve(fileName), slot).Campaigns[0];

        /// <summary>
        /// Builds a plan straight from a payload, so a splice that went wrong can be handed to the
        /// checks. Nothing in the app can produce one of these; the checks exist for the case where
        /// something one day does.
        /// </summary>
        public SaveWritePlan PlanFor(SaveEditSession session, string newPayload, RecordSetChange spliced)
            => SaveWritePlan.Build(
                session,
                ContainerText.Load(Live.ReadBytes("sav2")),
                PayloadOf("sav2"),
                newPayload,
                new HashSet<int>(),
                new[] { "a campaign moved" },
                spliced,
                SizePolicy.GrowIfNeeded);

        public void Dispose()
        {
            Live.Dispose();
            BackupRoot.Dispose();
        }
    }
}
