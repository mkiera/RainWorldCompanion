using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Editing;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.Core.Saves.Models;

namespace RainWorldCompanion.Tests;

/// <summary>
/// Deleting a whole slot, which takes every campaign in it at once.
///
/// The game's own WipeAll leaves a slot holding nothing but a reset MISCPROG. This app stops short
/// of that, because rebuilding MISCPROG would drop every field of it this app does not model. What
/// is checked here is that everything it does take out is taken out, that MISCPROG comes back
/// character for character, and that the file the game reads afterwards is one it will accept.
/// </summary>
public class SlotDeleteTests
{
    private static readonly SaveSlotRef LocalTwo = new(SaveRealm.Local, 2);
    private static readonly SaveSlotRef LocalThree = new(SaveRealm.Local, 3);

    // ---- what it takes out ----

    [Fact]
    public void Deleting_a_slot_leaves_it_holding_no_campaign()
    {
        using var world = new DeleteWorld();

        SlotDeletePlan plan = world.Writer.PlanDeleteSlot(LocalTwo, includeMaps: false);
        Assert.True(plan.CanWrite);

        SaveWriteResult result = world.Writer.Write(plan);
        Assert.True(result.Success, string.Join("; ", result.Errors));

        SlotMetadata after = SaveMetadataExtractor.Extract(world.Live.Resolve("sav2"), 2);
        Assert.Null(after.ParseError);
        Assert.True(after.ChecksumValid);
        Assert.Empty(after.Campaigns);
    }

    /// <summary>
    /// A slot deleted this way is not an untouched one. It still holds the map and the progression
    /// record, which is what separates a slot played and cleared from one never played at all.
    /// </summary>
    [Fact]
    public void The_map_and_the_progression_record_stay_unless_the_map_is_asked_for()
    {
        using var world = new DeleteWorld();
        string before = world.MiscProg("sav2");

        world.Writer.Write(world.Writer.PlanDeleteSlot(LocalTwo, includeMaps: false));

        Assert.Equal(before, world.MiscProg("sav2"));
        Assert.Contains("MAP_White", world.PayloadOf("sav2"), StringComparison.Ordinal);
        Assert.True(SaveMetadataExtractor.Extract(world.Live.Resolve("sav2"), 2).RecordCount > 1);
    }

    [Fact]
    public void Asking_for_the_map_takes_the_map_as_well()
    {
        using var world = new DeleteWorld();
        string before = world.MiscProg("sav2");

        SlotDeletePlan plan = world.Writer.PlanDeleteSlot(LocalTwo, includeMaps: true);
        Assert.Equal(7, plan.MapsRemoved);

        world.Writer.Write(plan);

        Assert.DoesNotContain("MAP_White", world.PayloadOf("sav2"), StringComparison.Ordinal);
        Assert.Equal(before, world.MiscProg("sav2"));
    }

    [Fact]
    public void Every_campaign_goes_not_only_the_first()
    {
        using var world = new DeleteWorld();
        world.AddACampaign("sav2", "Gourmand");
        world.AddACampaign("sav2", "Saint");

        Assert.Equal(3, SaveMetadataExtractor.Extract(world.Live.Resolve("sav2"), 2).Campaigns.Count);

        SlotDeletePlan plan = world.Writer.PlanDeleteSlot(LocalTwo, includeMaps: true);

        Assert.Equal(new[] { "Survivor", "Gourmand", "Saint" }, plan.Campaigns);
        Assert.True(world.Writer.Write(plan).Success);
        Assert.Empty(SaveMetadataExtractor.Extract(world.Live.Resolve("sav2"), 2).Campaigns);
    }

    // ---- what it says before it does it ----

    [Fact]
    public void A_plan_names_what_is_about_to_go()
    {
        using var world = new DeleteWorld();

        SlotDeletePlan one = world.Writer.PlanDeleteSlot(LocalTwo, includeMaps: false);
        Assert.Equal("Takes Survivor out of sav2, and leaves the map they explored behind.", one.Describe());
        Assert.Contains("keeps the map", one.WhatStays, StringComparison.Ordinal);

        SlotDeletePlan withMap = world.Writer.PlanDeleteSlot(LocalTwo, includeMaps: true);
        Assert.Contains("7 regions of map", withMap.Describe(), StringComparison.Ordinal);
        Assert.DoesNotContain("keeps the map", withMap.WhatStays, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plan_counts_the_campaigns_when_there_is_more_than_one()
    {
        using var world = new DeleteWorld();
        world.AddACampaign("sav2", "Gourmand");

        Assert.Contains(
            "Takes all 2 campaigns out of sav2",
            world.Writer.PlanDeleteSlot(LocalTwo, includeMaps: false).Describe(),
            StringComparison.Ordinal);
    }

    // ---- when it refuses ----

    [Fact]
    public void A_slot_with_no_campaign_in_it_is_refused_rather_than_rewritten()
    {
        using var world = new DeleteWorld();
        world.Writer.Write(world.Writer.PlanDeleteSlot(LocalTwo, includeMaps: false));

        byte[] cleared = world.Live.ReadBytes("sav2");
        SlotDeletePlan again = world.Writer.PlanDeleteSlot(LocalTwo, includeMaps: false);

        Assert.False(again.CanWrite);
        Assert.Contains(again.Problems, p => p.Contains("nothing in it to delete", StringComparison.Ordinal));

        SaveWriteResult result = world.Writer.Write(again);
        Assert.False(result.Success);
        Assert.False(result.LiveFolderModified);
        SnapshotLayout.AssertBytesEqual(cleared, world.Live.ReadBytes("sav2"), "sav2");
    }

    [Fact]
    public void A_running_game_stops_a_slot_being_deleted()
    {
        using var world = new DeleteWorld();
        byte[] before = world.Live.ReadBytes("sav2");

        SlotDeletePlan plan = world.Writer.PlanDeleteSlot(LocalTwo, includeMaps: true);
        world.Detector.RunningProcessName = "RainWorld";

        SlotDeletePlan blocked = world.Writer.PlanDeleteSlot(LocalTwo, includeMaps: true);
        Assert.False(blocked.CanWrite);
        Assert.Contains(blocked.Problems, p => p.Contains("Rain World is running", StringComparison.Ordinal));

        Assert.Throws<GameRunningException>(() => world.Writer.Write(plan));
        SnapshotLayout.AssertBytesEqual(before, world.Live.ReadBytes("sav2"), "sav2");
    }

    [Fact]
    public void A_slot_that_is_not_one_and_a_file_that_is_not_there_are_both_refused()
    {
        using var world = new DeleteWorld();

        SlotDeletePlan notASlot = world.Writer.PlanDeleteSlot(new SaveSlotRef(SaveRealm.Local, 9), includeMaps: true);
        Assert.False(notASlot.CanWrite);
        Assert.Contains(notASlot.Problems, p => p.Contains("not a Rain World slot", StringComparison.Ordinal));

        File.Delete(world.Live.Resolve("sav3"));

        SlotDeletePlan gone = world.Writer.PlanDeleteSlot(LocalThree, includeMaps: true);
        Assert.False(gone.CanWrite);
        Assert.Contains(gone.Problems, p => p.Contains("not in the save folder", StringComparison.Ordinal));
    }

    // ---- what it does not touch ----

    [Fact]
    public void Deleting_a_slot_takes_a_safety_snapshot_of_it_first()
    {
        using var world = new DeleteWorld();
        byte[] before = world.Live.ReadBytes("sav2");

        SaveWriteResult result = world.Writer.Write(world.Writer.PlanDeleteSlot(LocalTwo, includeMaps: true));

        BackupSnapshot safety = Assert.IsType<BackupSnapshot>(result.SafetySnapshot);
        SnapshotLayout.AssertBytesEqual(
            before,
            File.ReadAllBytes(Path.Combine(safety.DirectoryPath, "sav2")),
            "sav2 in the safety snapshot");
    }

    [Fact]
    public void Deleting_one_slot_leaves_every_other_save_alone()
    {
        using var world = new DeleteWorld();
        Dictionary<string, byte[]> others = new[] { "sav", "sav3", "online_sav", "exp1" }
            .ToDictionary(name => name, name => world.Live.ReadBytes(name));

        world.Writer.Write(world.Writer.PlanDeleteSlot(LocalTwo, includeMaps: true));

        foreach ((string name, byte[] bytes) in others)
        {
            SnapshotLayout.AssertBytesEqual(bytes, world.Live.ReadBytes(name), name);
        }
    }

    /// <summary>The game's own previous revision, which is what it falls back to if an edit is wrong.</summary>
    [Fact]
    public void The_games_own_backup_entry_inside_the_file_is_left_alone()
    {
        using var world = new DeleteWorld();
        SaveContainer before = SaveContainer.Read(world.Live.Resolve("sav2"));
        string backupEntry = before.Entries["save__Backup"];

        world.Writer.Write(world.Writer.PlanDeleteSlot(LocalTwo, includeMaps: true));

        SaveContainer after = SaveContainer.Read(world.Live.Resolve("sav2"));
        Assert.Equal(backupEntry, after.Entries["save__Backup"]);
    }

    private sealed class DeleteWorld : IDisposable
    {
        public DeleteWorld()
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

        public SaveSlotWriter Writer => Service.SlotWriter;

        public string PayloadOf(string fileName)
        {
            SaveContainer container = SaveContainer.Read(Live.Resolve(fileName));
            SaveChecksum.TryUnwrap(container.Entries["save"], out string payload, out _);
            return payload;
        }

        public string MiscProg(string fileName)
            => SavePayloadReader.SplitRecords(PayloadOf(fileName))
                .Single(record => record.Header == "MISCPROG").Body;

        /// <summary>
        /// Every fixture is a Survivor campaign, so a test about a slot holding several has to make
        /// the others.
        /// </summary>
        public void AddACampaign(string fileName, string slugcat)
        {
            var slice = new CampaignSlice(
                slugcat,
                "SAVE STATE" + SyntheticSave.HeaderSeparator + SyntheticSave.SaveStateBody(slugcat),
                new[] { "MAP_" + slugcat + SyntheticSave.HeaderSeparator + "HI" + SyntheticSave.HeaderSeparator + "map" });

            SaveEditSession session = SaveEditSession.Open(Live.Resolve(fileName));
            session.PutCampaignIn(slice);

            SaveWriteResult result = Service.SlotWriter.Write(
                session.BuildWritePlan(),
                new SaveSlotRef(SaveRealm.Local, SaveMetadataExtractor.SlotNumberForFileName(fileName) ?? 2));

            if (!result.Success)
            {
                throw new InvalidOperationException(string.Join("; ", result.Errors));
            }
        }

        public void Dispose()
        {
            Live.Dispose();
            BackupRoot.Dispose();
        }
    }
}
