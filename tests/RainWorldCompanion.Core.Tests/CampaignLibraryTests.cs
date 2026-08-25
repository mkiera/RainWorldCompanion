using System.IO.Compression;
using System.Text;
using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Editing;
using RainWorldCompanion.Core.Library;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.Core.Saves.Models;

namespace RainWorldCompanion.Tests;

/// <summary>
/// Keeping one campaign in the library rather than a whole slot.
///
/// A stored campaign is the game's own records and nothing else, so what is checked here is that
/// they survive being written out, carried through a .rwcampaign file and put back into a slot that
/// has campaigns of its own in it.
/// </summary>
public class CampaignLibraryTests
{
    private static readonly SaveSlotRef LocalTwo = new(SaveRealm.Local, 2);
    private static readonly SaveSlotRef LocalThree = new(SaveRealm.Local, 3);

    // ---- storing ----

    [Fact]
    public void Storing_a_campaign_writes_the_campaign_and_leaves_the_slot_alone()
    {
        using var world = new LibraryWorld();
        byte[] before = world.Live.ReadBytes("sav2");

        LibraryEntry entry = world.Library.StoreCampaign(LocalTwo, "White", "Survivor run", "halfway");

        Assert.True(entry.IsComplete);
        Assert.True(entry.IsCampaign);
        Assert.Equal(LibraryEntryKind.Campaign, entry.Manifest!.Kind);
        Assert.Equal("White", entry.Manifest.CampaignSlugcatId);
        Assert.Equal(LibraryManifest.CurrentSchemaVersion, entry.Manifest.SchemaVersion);
        Assert.True(File.Exists(entry.CampaignPath));
        Assert.False(File.Exists(entry.SavePath));

        SnapshotLayout.AssertBytesEqual(before, world.Live.ReadBytes("sav2"), "sav2");
    }

    [Fact]
    public void A_stored_campaign_describes_itself_without_being_opened()
    {
        using var world = new LibraryWorld();
        CampaignSummary slot = SaveMetadataExtractor.Extract(world.Live.Resolve("sav2"), 2).Campaigns[0];

        LibraryEntry entry = world.Library.StoreCampaign(LocalTwo, "White", "Survivor run", null);

        CampaignSummary stored = Assert.Single(entry.Manifest!.Metadata!.Campaigns);
        Assert.Equal(slot.SlugcatId, stored.SlugcatId);
        Assert.Equal(slot.CycleNum, stored.CycleNum);
        Assert.Equal(slot.Karma, stored.Karma);
        Assert.Equal(slot.UnlockedGates.Count, stored.UnlockedGates.Count);

        // No container round it, so nothing claims a digest either way.
        Assert.Null(entry.Manifest.Metadata.ChecksumValid);
    }

    [Fact]
    public void Storing_a_campaign_the_slot_does_not_have_says_so()
    {
        using var world = new LibraryWorld();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => world.Library.StoreCampaign(LocalTwo, "Saint", "nothing", null));

        Assert.Contains("Saint", error.Message, StringComparison.Ordinal);
        Assert.Empty(world.Library.ListEntries());
    }

    [Fact]
    public void A_running_game_stops_a_campaign_being_stored()
    {
        using var world = new LibraryWorld(FakeGameDetector.Running());

        Assert.Throws<GameRunningException>(() => world.Library.StoreCampaign(LocalTwo, "White", "run", null));
    }

    // ---- what the library makes of one ----

    [Fact]
    public void A_stored_campaign_verifies_against_its_own_checksum()
    {
        using var world = new LibraryWorld();
        LibraryEntry entry = world.Library.StoreCampaign(LocalTwo, "White", "run", null);

        Assert.True(world.Library.VerifyEntry(entry).Ok);

        File.WriteAllBytes(entry.CampaignPath, Encoding.UTF8.GetBytes("meddled with"));

        VerifyResult verified = world.Library.VerifyEntry(world.Reload(entry));
        Assert.False(verified.Ok);
        Assert.Contains(verified.Problems, problem => problem.Contains("campaign", StringComparison.Ordinal));
    }

    [Fact]
    public void An_entry_whose_campaign_went_missing_says_which_file_is_gone()
    {
        using var world = new LibraryWorld();
        LibraryEntry entry = world.Library.StoreCampaign(LocalTwo, "White", "run", null);

        File.Delete(entry.CampaignPath);

        LibraryEntry reloaded = world.Reload(entry);
        Assert.False(reloaded.IsComplete);
        Assert.Contains("campaign.bin", reloaded.Problem!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Entries written before campaigns existed carry no kind at all, and every one of them is a
    /// whole slot.
    /// </summary>
    [Fact]
    public void An_entry_written_before_campaigns_existed_still_reads_as_a_whole_slot()
    {
        using var world = new LibraryWorld();
        LibraryEntry entry = world.Library.StoreSlot(LocalTwo, "as it was", null);

        File.WriteAllText(entry.ManifestPath, VersionOneManifest(entry.Manifest!.Sha256, entry.Manifest.SizeBytes));

        LibraryEntry reloaded = world.Reload(entry);
        Assert.True(reloaded.IsComplete);
        Assert.False(reloaded.IsCampaign);
        Assert.Equal(LibraryEntryKind.WholeSlot, reloaded.Manifest!.Kind);
        Assert.Equal(1, reloaded.Manifest.SchemaVersion);
        Assert.True(world.Library.VerifyEntry(reloaded).Ok);
    }

    // ---- putting one into a slot ----

    [Fact]
    public void A_stored_campaign_joins_the_campaigns_a_slot_already_has()
    {
        using var world = new LibraryWorld();
        world.Seed("sav3", "Gourmand", cycle: 55);

        LibraryEntry entry = world.Library.StoreCampaign(LocalThree, "Gourmand", "the big one", null);
        LibraryLoadResult result = world.Library.LoadCampaignOntoSlot(entry, LocalTwo);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        SlotMetadata metadata = SaveMetadataExtractor.Extract(world.Live.Resolve("sav2"), 2);
        Assert.Null(metadata.ParseError);
        Assert.True(metadata.ChecksumValid);
        Assert.Equal(new[] { "White", "Gourmand" }, metadata.Campaigns.Select(c => c.SlugcatId));
    }

    [Fact]
    public void Loading_a_campaign_replaces_only_the_one_for_that_slugcat()
    {
        using var world = new LibraryWorld();
        LibraryEntry entry = world.Library.StoreCampaign(LocalThree, "White", "from slot three", null);

        CampaignMovePlan plan = world.Library.PlanCampaignLoad(entry, LocalTwo);
        Assert.Equal(CampaignSpliceOutcome.Replaced, plan.Splice.Outcome);
        Assert.Contains("Replaces", plan.Describe(), StringComparison.Ordinal);

        Assert.True(world.Library.LoadCampaignOntoSlot(entry, LocalTwo).Success);

        SlotMetadata slotTwo = SaveMetadataExtractor.Extract(world.Live.Resolve("sav2"), 2);
        SlotMetadata slotThree = SaveMetadataExtractor.Extract(world.Live.Resolve("sav3"), 3);

        CampaignSummary landed = Assert.Single(slotTwo.Campaigns);
        Assert.Equal(slotThree.Campaigns[0].CycleNum, landed.CycleNum);
        Assert.Equal(slotThree.Campaigns[0].Karma, landed.Karma);
    }

    [Fact]
    public void Loading_a_campaign_takes_a_safety_snapshot_of_the_slot_it_lands_in()
    {
        using var world = new LibraryWorld();
        byte[] before = world.Live.ReadBytes("sav2");
        LibraryEntry entry = world.Library.StoreCampaign(LocalThree, "White", "from slot three", null);

        LibraryLoadResult result = world.Library.LoadCampaignOntoSlot(entry, LocalTwo);

        BackupSnapshot safety = Assert.IsType<BackupSnapshot>(result.SafetySnapshot);
        SnapshotLayout.AssertBytesEqual(
            before,
            File.ReadAllBytes(Path.Combine(safety.DirectoryPath, "sav2")),
            "sav2 in the safety snapshot");
    }

    [Fact]
    public void Loading_a_campaign_leaves_every_other_slot_alone()
    {
        using var world = new LibraryWorld();
        Dictionary<string, byte[]> others = new[] { "sav", "sav3", "online_sav", "exp1" }
            .ToDictionary(name => name, name => world.Live.ReadBytes(name));

        LibraryEntry entry = world.Library.StoreCampaign(LocalThree, "White", "from slot three", null);
        world.Library.LoadCampaignOntoSlot(entry, LocalTwo);

        foreach ((string name, byte[] bytes) in others)
        {
            SnapshotLayout.AssertBytesEqual(bytes, world.Live.ReadBytes(name), name);
        }
    }

    /// <summary>
    /// A whole slot is written over a slot and a campaign is written into one, so neither operation
    /// takes the other's entry.
    /// </summary>
    [Fact]
    public void The_two_kinds_of_entry_do_not_take_each_others_load()
    {
        using var world = new LibraryWorld();
        LibraryEntry slot = world.Library.StoreSlot(LocalTwo, "whole slot", null);
        LibraryEntry campaign = world.Library.StoreCampaign(LocalTwo, "White", "one campaign", null);

        LibraryLoadPlan wrongWay = world.Library.PlanLoad(campaign, LocalThree);
        Assert.False(wrongWay.CanLoad);
        Assert.Contains(wrongWay.Problems, p => p.Contains("one campaign", StringComparison.Ordinal));

        CampaignMovePlan otherWay = world.Library.PlanCampaignLoad(slot, LocalThree);
        Assert.False(otherWay.CanWrite);
        Assert.Contains(otherWay.Problems, p => p.Contains("whole slot", StringComparison.Ordinal));
    }

    [Fact]
    public void A_campaign_that_fails_its_checksum_is_not_written_to_a_slot()
    {
        using var world = new LibraryWorld();
        byte[] before = world.Live.ReadBytes("sav2");
        LibraryEntry entry = world.Library.StoreCampaign(LocalThree, "White", "run", null);

        File.WriteAllBytes(entry.CampaignPath, CampaignFile.ToBytes(world.Library.ReadStoredCampaign(entry)!)
            .Concat(Encoding.UTF8.GetBytes("<progDivA>")).ToArray());

        LibraryLoadResult result = world.Library.LoadCampaignOntoSlot(world.Reload(entry), LocalTwo);

        Assert.False(result.Success);
        Assert.False(result.LiveFolderModified);
        Assert.Contains(result.Errors, e => e.Contains("checksum check", StringComparison.Ordinal));
        SnapshotLayout.AssertBytesEqual(before, world.Live.ReadBytes("sav2"), "sav2");
    }

    /// <summary>
    /// A window offering both kinds asks one question and gets an answer for whichever it is
    /// holding, rather than having to know first.
    /// </summary>
    [Fact]
    public void One_plan_answers_for_either_kind_of_entry()
    {
        using var world = new LibraryWorld();
        LibraryEntry slot = world.Library.StoreSlot(LocalThree, "whole slot", null);
        LibraryEntry campaign = world.Library.StoreCampaign(LocalThree, "White", "one campaign", null);

        LibraryLoadPlan wholeSlot = world.Library.PlanAnyLoad(slot, LocalTwo);
        Assert.True(wholeSlot.CanLoad);
        Assert.Equal("", wholeSlot.Summary);

        LibraryLoadPlan justOne = world.Library.PlanAnyLoad(campaign, LocalTwo);
        Assert.True(justOne.CanLoad);
        Assert.Contains("Replaces the campaign in sav2", justOne.Summary, StringComparison.Ordinal);
        Assert.Equal("sav2", justOne.Target.FileName);
    }

    [Fact]
    public void One_load_writes_either_kind_of_entry()
    {
        using var world = new LibraryWorld();
        world.Seed("sav3", "Gourmand", cycle: 55);
        LibraryEntry campaign = world.Library.StoreCampaign(LocalThree, "Gourmand", "one campaign", null);

        Assert.True(world.Library.LoadAny(campaign, LocalTwo).Success);
        Assert.Equal(
            new[] { "White", "Gourmand" },
            SaveMetadataExtractor.Extract(world.Live.Resolve("sav2"), 2).Campaigns.Select(c => c.SlugcatId));

        LibraryEntry slot = world.Library.StoreSlot(LocalThree, "whole slot", null);

        Assert.True(world.Library.LoadAny(slot, LocalTwo).Success);
        Assert.Equal(
            new[] { "Gourmand" },
            SaveMetadataExtractor.Extract(world.Live.Resolve("sav2"), 2).Campaigns.Select(c => c.SlugcatId));
    }

    // ---- carrying one to another machine ----

    [Fact]
    public void A_campaign_goes_out_as_a_campaign_file_and_comes_back_as_one()
    {
        using var world = new LibraryWorld();
        LibraryEntry entry = world.Library.StoreCampaign(LocalTwo, "White", "Survivor run", "halfway");

        Assert.Equal(".rwcampaign", SaveLibrary.ExportExtensionFor(entry));

        string exported = Path.Combine(world.BackupRoot.Path, "run.rwcampaign");
        world.Library.ExportEntry(entry, exported);
        world.Library.DeleteEntry(entry);

        LibraryImportResult imported = world.Library.ImportFile(exported);

        Assert.True(imported.Success, string.Join("; ", imported.Errors));
        Assert.True(imported.Entry!.IsCampaign);
        Assert.Equal("Survivor run", imported.Entry.Name);
        Assert.Equal("halfway", imported.Entry.Manifest!.Note);
        Assert.Equal("White", imported.Entry.Manifest.CampaignSlugcatId);
        Assert.Equal(entry.Manifest!.Sha256, imported.Entry.Manifest.Sha256);
    }

    [Fact]
    public void A_whole_slot_still_goes_out_as_a_save_file()
    {
        using var world = new LibraryWorld();
        LibraryEntry entry = world.Library.StoreSlot(LocalTwo, "whole slot", null);

        Assert.Equal(".rwsave", SaveLibrary.ExportExtensionFor(entry));

        string exported = Path.Combine(world.BackupRoot.Path, "slot.rwsave");
        world.Library.ExportEntry(entry, exported);

        LibraryImportResult imported = world.Library.ImportFile(exported);

        Assert.True(imported.Success, string.Join("; ", imported.Errors));
        Assert.False(imported.Entry!.IsCampaign);
        Assert.True(File.Exists(imported.Entry.SavePath));
    }

    /// <summary>
    /// Both are zips, so the first bytes say only that it is one of the two. What is inside decides,
    /// which also means a file renamed on the way keeps working.
    /// </summary>
    [Fact]
    public void What_is_inside_the_file_decides_which_kind_it_is_not_the_name()
    {
        using var world = new LibraryWorld();
        LibraryEntry entry = world.Library.StoreCampaign(LocalTwo, "White", "run", null);

        string misnamed = Path.Combine(world.BackupRoot.Path, "looks-like-a-slot.rwsave");
        world.Library.ExportEntry(entry, misnamed);

        LibraryImportResult imported = world.Library.ImportFile(misnamed);

        Assert.True(imported.Success, string.Join("; ", imported.Errors));
        Assert.True(imported.Entry!.IsCampaign);
    }

    [Fact]
    public void A_campaign_file_damaged_on_the_way_is_refused()
    {
        using var world = new LibraryWorld();
        LibraryEntry entry = world.Library.StoreCampaign(LocalTwo, "White", "run", null);

        string exported = Path.Combine(world.BackupRoot.Path, "run.rwcampaign");
        world.Library.ExportEntry(entry, exported);
        Rewrite(exported, "not a campaign at all"u8.ToArray());

        LibraryImportResult imported = world.Library.ImportFile(exported);

        Assert.False(imported.Success);
        Assert.Contains(imported.Errors, e => e.Contains("checksum", StringComparison.Ordinal));
    }

    [Fact]
    public void A_campaign_file_on_its_own_can_be_imported()
    {
        using var world = new LibraryWorld();
        LibraryEntry entry = world.Library.StoreCampaign(LocalTwo, "White", "run", null);

        string loose = Path.Combine(world.BackupRoot.Path, "Survivor.bin");
        File.Copy(entry.CampaignPath, loose);

        LibraryImportResult imported = world.Library.ImportFile(loose);

        Assert.True(imported.Success, string.Join("; ", imported.Errors));
        Assert.True(imported.Entry!.IsCampaign);
        Assert.Equal("White", imported.Entry.Manifest!.CampaignSlugcatId);
        Assert.Equal("Survivor", imported.Entry.Name);
        Assert.Single(imported.Entry.Manifest.Metadata!.Campaigns);
    }

    [Fact]
    public void Something_that_is_neither_says_which_three_it_could_have_been()
    {
        using var world = new LibraryWorld();
        string nonsense = Path.Combine(world.BackupRoot.Path, "notes.txt");
        File.WriteAllText(nonsense, "this is not a save");

        LibraryImportResult imported = world.Library.ImportFile(nonsense);

        Assert.False(imported.Success);
        Assert.Contains(imported.Errors, e => e.Contains(".rwcampaign", StringComparison.Ordinal));
    }

    // ---- pulling one campaign out of something that is not a live slot ----

    /// <summary>
    /// A campaign can be in a live slot, in a backup, in a whole slot kept in the library, or in a
    /// campaign file on its own. Only the last is not a save container, and one call reads all four.
    /// </summary>
    [Fact]
    public void A_campaign_is_read_out_of_a_save_or_out_of_a_campaign_file_the_same_way()
    {
        using var world = new LibraryWorld();
        LibraryEntry stored = world.Library.StoreCampaign(LocalTwo, "White", "run", null);

        CampaignSlice fromSlot = CampaignFile.ReadFrom(world.Live.Resolve("sav2"), "White")!;
        CampaignSlice fromCampaign = CampaignFile.ReadFrom(stored.CampaignPath)!;

        Assert.Equal(fromSlot.SaveStateRecord, fromCampaign.SaveStateRecord);
        Assert.Equal(fromSlot.MapRecords, fromCampaign.MapRecords);

        Assert.False(CampaignFile.IsOne(world.Live.Resolve("sav2")));
        Assert.True(CampaignFile.IsOne(stored.CampaignPath));
    }

    [Fact]
    public void Reading_a_campaign_that_is_not_there_gives_nothing_rather_than_failing()
    {
        using var world = new LibraryWorld();

        Assert.Null(CampaignFile.ReadFrom(world.Live.Resolve("sav2"), "Saint"));
        Assert.Null(CampaignFile.ReadFrom(world.Live.Resolve("no-such-file"), "White"));
        Assert.Null(CampaignFile.ReadFrom("", "White"));

        // A save container with no slugcat named has no way to say which campaign was wanted.
        Assert.Null(CampaignFile.ReadFrom(world.Live.Resolve("sav2")));
    }

    /// <summary>
    /// The same refusal a live save gets. A campaign taken out of a damaged file and written into a
    /// slot would carry the damage in with it under a fresh, correct digest.
    /// </summary>
    [Fact]
    public void A_campaign_is_not_read_out_of_a_file_whose_checksum_is_already_wrong()
    {
        using var world = new LibraryWorld();
        string path = world.Live.Resolve("sav2");

        File.WriteAllBytes(path, SyntheticSave.Bytes(new[]
        {
            SyntheticSave.Entry("save", SyntheticSave.WrapWithBadChecksum(SyntheticSave.SavePayload())),
        }));

        SaveContainerException error = Assert.Throws<SaveContainerException>(
            () => CampaignFile.ReadFrom(path, "White"));

        Assert.Contains("checksum", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_campaign_comes_out_of_a_backup_and_into_a_live_slot()
    {
        using var world = new LibraryWorld();
        int? backedUpCycle = SaveMetadataExtractor.Extract(world.Live.Resolve("sav3"), 3).Campaigns[0].CycleNum;

        BackupSnapshot snapshot = world.Backups.CreateBackup("before the change", null);

        // The live slot moves on afterwards, so what comes back is provably the backup's own.
        world.PlayASlot(LocalThree, "CYCLENUM", "888");

        CampaignSlice slice = CampaignFile.ReadFrom(Path.Combine(snapshot.DirectoryPath, "sav3"), "White")!;
        SaveWriteResult result = world.Backups.SlotWriter.Write(
            world.Backups.SlotWriter.PlanPutCampaign(LocalTwo, slice));

        Assert.True(result.Success, string.Join("; ", result.Errors));

        SlotMetadata landed = SaveMetadataExtractor.Extract(world.Live.Resolve("sav2"), 2);
        Assert.True(landed.ChecksumValid);
        Assert.Equal(backedUpCycle, Assert.Single(landed.Campaigns).CycleNum);

        // Slot three still holds what it was played to, so nothing was rolled back by reading.
        Assert.Equal(888, SaveMetadataExtractor.Extract(world.Live.Resolve("sav3"), 3).Campaigns[0].CycleNum);
    }

    [Fact]
    public void A_campaign_out_of_a_backup_can_be_kept_in_the_library()
    {
        using var world = new LibraryWorld();
        BackupSnapshot snapshot = world.Backups.CreateBackup("before the change", null);

        CampaignSlice slice = CampaignFile.ReadFrom(Path.Combine(snapshot.DirectoryPath, "sav2"), "White")!;

        LibraryEntry entry = world.Library.StoreCampaignFrom(
            slice, "sav2", SaveRealm.Local, 2, "out of a backup", "kept just in case");

        Assert.True(entry.IsCampaign);
        Assert.Equal("White", entry.Manifest!.CampaignSlugcatId);
        Assert.Equal("sav2", entry.Manifest.SourceFileName);
        Assert.Equal(2, entry.Manifest.SourceSlot);
        Assert.True(world.Library.VerifyEntry(entry).Ok);
        Assert.Equal(slice.SaveStateRecord, world.Library.ReadStoredCampaign(entry)!.SaveStateRecord);
    }

    /// <summary>
    /// A whole slot in the library holds several campaigns, and until now the only way to get one
    /// out was to write the whole slot over a live one.
    /// </summary>
    [Fact]
    public void One_campaign_is_pulled_out_of_a_whole_slot_kept_in_the_library()
    {
        using var world = new LibraryWorld();
        world.Seed("sav3", "Gourmand", cycle: 55);
        LibraryEntry wholeSlot = world.Library.StoreSlot(LocalThree, "everything", null);

        CampaignSlice slice = CampaignFile.ReadFrom(wholeSlot.SavePath, "Gourmand")!;

        Assert.True(world.Backups.SlotWriter.Write(
            world.Backups.SlotWriter.PlanPutCampaign(LocalTwo, slice)).Success);

        SlotMetadata landed = SaveMetadataExtractor.Extract(world.Live.Resolve("sav2"), 2);
        Assert.Equal(new[] { "White", "Gourmand" }, landed.Campaigns.Select(c => c.SlugcatId));
        Assert.Equal(55, landed.Campaigns.First(c => c.SlugcatId == "Gourmand").CycleNum);
    }

    [Fact]
    public void Storing_a_campaign_from_somewhere_else_still_needs_a_name()
    {
        using var world = new LibraryWorld();
        CampaignSlice slice = CampaignFile.ReadFrom(world.Live.Resolve("sav2"), "White")!;

        Assert.Throws<ArgumentException>(
            () => world.Library.StoreCampaignFrom(slice, "sav2", SaveRealm.Local, 2, "  ", null));
    }

    // ---- putting an hour of play back into one ----

    [Fact]
    public void A_campaign_entry_can_be_brought_level_with_the_slot_again()
    {
        using var world = new LibraryWorld();
        LibraryEntry entry = world.Library.StoreCampaign(LocalTwo, "White", "run", null);
        string storedFirst = entry.Manifest!.Sha256;

        // The slot is played, which here is one field moving.
        world.PlayASlot(LocalTwo, "CYCLENUM", "999");

        LibraryEntry updated = world.Library.UpdateEntry(entry, LocalTwo);

        Assert.True(updated.IsCampaign);
        Assert.Equal(999, updated.Manifest!.Metadata!.Campaigns[0].CycleNum);
        Assert.Equal(storedFirst, updated.Manifest.PreviousSha256);
        Assert.True(updated.HasPrevious);
        Assert.True(world.Library.VerifyEntry(updated).Ok);
    }

    [Fact]
    public void Undoing_that_puts_the_campaign_it_replaced_back()
    {
        using var world = new LibraryWorld();
        LibraryEntry entry = world.Library.StoreCampaign(LocalTwo, "White", "run", null);
        int? storedCycle = entry.Manifest!.Metadata!.Campaigns[0].CycleNum;

        world.PlayASlot(LocalTwo, "CYCLENUM", "999");
        LibraryEntry updated = world.Library.UpdateEntry(entry, LocalTwo);

        LibraryEntry undone = world.Library.UndoUpdate(updated);

        Assert.Equal(storedCycle, undone.Manifest!.Metadata!.Campaigns[0].CycleNum);
        Assert.False(undone.HasPrevious);
        Assert.True(world.Library.VerifyEntry(undone).Ok);
    }

    // ---- the file itself ----

    [Fact]
    public void A_campaign_file_is_the_records_the_game_writes_and_nothing_else()
    {
        using var world = new LibraryWorld();
        LibraryEntry entry = world.Library.StoreCampaign(LocalTwo, "White", "run", null);

        byte[] bytes = File.ReadAllBytes(entry.CampaignPath);

        // No mark on the front, so it is never mistaken for a save container.
        Assert.NotEqual(0xEF, bytes[0]);

        string text = Encoding.UTF8.GetString(bytes);
        Assert.StartsWith(CampaignFile.Prefix, text, StringComparison.Ordinal);
        Assert.EndsWith(SavePayloadReader.RecordSeparator, text, StringComparison.Ordinal);

        CampaignSlice slice = CampaignFile.Read(bytes)!;
        Assert.Equal("White", slice.SlugcatId);
        Assert.Equal(7, slice.MapRecords.Count);
        Assert.Equal(text, CampaignFile.ToPayload(slice));
    }

    [Fact]
    public void Something_that_is_not_a_campaign_reads_back_as_nothing()
    {
        Assert.Null(CampaignFile.Read(null));
        Assert.Null(CampaignFile.Read(Array.Empty<byte>()));
        Assert.Null(CampaignFile.FromPayload("MISCPROG<progDivB>CYCLES<misA>1<progDivA>"));
        Assert.False(CampaignFile.LooksLikeOne(Encoding.UTF8.GetBytes("MISCPROG<progDivB>")));
        Assert.True(CampaignFile.LooksLikeOne(Encoding.UTF8.GetBytes(CampaignFile.Prefix + "SAV STATE NUMBER")));
    }

    // ---- helpers ----

    /// <summary>Rewrites the campaign inside a bundle, which is what damage in transit looks like.</summary>
    private static void Rewrite(string bundlePath, byte[] content)
    {
        using var archive = ZipFile.Open(bundlePath, ZipArchiveMode.Update);
        archive.GetEntry(LibraryEntry.CampaignFileName)?.Delete();

        ZipArchiveEntry replacement = archive.CreateEntry(LibraryEntry.CampaignFileName);
        using Stream stream = replacement.Open();
        stream.Write(content, 0, content.Length);
    }

    private static string VersionOneManifest(string sha256, long sizeBytes) => $$"""
        {
          "schemaVersion": 1,
          "name": "as it was",
          "createdUtc": "2026-08-01T09:00:00Z",
          "appVersion": "0.9.0",
          "sourceFileName": "sav2",
          "sourceSlot": 2,
          "sizeBytes": {{sizeBytes}},
          "sha256": "{{sha256}}"
        }
        """;
}
