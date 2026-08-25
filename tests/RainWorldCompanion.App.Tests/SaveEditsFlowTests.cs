using System.IO;
using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Editing;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.Core.Saves.Models;
using RainWorldCompanion.Core.System;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.App.Tests;

/// <summary>
/// The whole way through, the way the window goes: a campaign is opened, edited in the panel, the
/// plan is built and checked, and the writer puts it over the slot behind a backup.
///
/// The panel tests beside this one stop at the session. These carry on to the file, because that is
/// where an edit either becomes a save the game reads or does not.
/// </summary>
public class SaveEditsFlowTests : IDisposable
{
    private readonly TempDirectory _live = new("live");
    private readonly TempDirectory _backups = new("backups");
    private readonly FakeDetector _detector = new();
    private readonly BackupService _service;
    private readonly string _slotPath;

    private static readonly SaveSlotRef LocalTwo = new(SaveRealm.Local, 2);

    public SaveEditsFlowTests()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sav2.bin");
        _slotPath = Path.Combine(_live.Path, "sav2");
        File.Copy(fixture, _slotPath);

        _service = new BackupService(_live.Path, _backups.Path, _detector, "1.0.0-test");
    }

    public void Dispose()
    {
        _live.Dispose();
        _backups.Dispose();
    }

    private (SaveEditSession Session, CampaignEditViewModel Editor) Open()
    {
        var session = SaveEditSession.Open(_slotPath);
        var campaign = session.Campaigns[0];

        var summary = new CampaignSummary
        {
            SlugcatId = campaign.SlugcatId,
            CycleNum = int.TryParse(session.GetFieldValue(campaign, "CYCLENUM"), out var cycle) ? cycle : null,
        };

        return (session, new CampaignEditViewModel(session, campaign, summary));
    }

    private CampaignSummary Reread()
        => SaveMetadataExtractor.Extract(_slotPath, 2).Campaigns[0];

    [Fact]
    public void Edits_made_in_the_panel_end_up_in_the_file()
    {
        var (_, editor) = Open();

        editor.Cycle = "1234";
        editor.DenPos = "HI_S03";
        editor.Echoes.First(e => e.RegionCode == "SH").TalkedTo = true;
        editor.Gates.First(g => g.Name == "GATE_SU_HI").UnlockedField = true;

        var result = _service.SlotWriter.Write(editor.BuildWritePlan(), LocalTwo);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        var after = Reread();
        Assert.Equal(1234, after.CycleNum);
        Assert.Equal("HI_S03", after.DenPos);
        Assert.Contains(after.Echoes, e => e.RegionCode == "SH" && e.State == EchoRecord.TalkedTo);
        Assert.Contains("GATE_SU_HI", after.UnlockedGates);
    }

    [Fact]
    public void The_written_file_is_one_the_game_would_accept()
    {
        var (_, editor) = Open();
        editor.Karma = "3";

        _service.SlotWriter.Write(editor.BuildWritePlan(), LocalTwo);

        var metadata = SaveMetadataExtractor.Extract(_slotPath, 2);
        Assert.Null(metadata.ParseError);
        Assert.True(metadata.ChecksumValid);
    }

    [Fact]
    public void Saving_leaves_a_backup_holding_the_save_as_it_was()
    {
        var before = File.ReadAllBytes(_slotPath);
        var (_, editor) = Open();
        editor.Cycle = "77";

        var result = _service.SlotWriter.Write(editor.BuildWritePlan(), LocalTwo);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        var safety = result.SafetySnapshot!;
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(safety.DirectoryPath, "sav2")));

        // And it is listed, so it can be found and restored from the window.
        Assert.Contains(_service.ListBackups(), b => b.Id == safety.Id);
    }

    /// <summary>
    /// The undo the backup promises has to actually work, so this puts the save back and checks it
    /// is the file it started as.
    /// </summary>
    [Fact]
    public void Restoring_that_backup_puts_the_edit_back()
    {
        var before = File.ReadAllBytes(_slotPath);
        var (_, editor) = Open();
        editor.Cycle = "77";

        var written = _service.SlotWriter.Write(editor.BuildWritePlan(), LocalTwo);
        Assert.Equal(77, Reread().CycleNum);

        var restore = _service.RestoreBackup(written.SafetySnapshot!);

        Assert.True(restore.Success, string.Join("; ", restore.Errors));
        Assert.Equal(before, File.ReadAllBytes(_slotPath));
    }

    [Fact]
    public void Everything_the_panel_did_not_touch_survives_the_write()
    {
        var before = SaveMetadataExtractor.Extract(_slotPath, 2);

        var (_, editor) = Open();
        editor.Cycle = "1234";
        _service.SlotWriter.Write(editor.BuildWritePlan(), LocalTwo);

        var after = SaveMetadataExtractor.Extract(_slotPath, 2);

        Assert.Equal(before.RecordCount, after.RecordCount);
        Assert.Equal(before.Campaigns.Count, after.Campaigns.Count);
        Assert.Equal(before.Campaigns[0].Seed, after.Campaigns[0].Seed);
        Assert.Equal(before.Campaigns[0].TotalFoodEaten, after.Campaigns[0].TotalFoodEaten);
        Assert.Equal(before.Campaigns[0].Kills.Count, after.Campaigns[0].Kills.Count);
    }

    [Fact]
    public void A_second_edit_on_top_of_the_first_works_from_a_fresh_session()
    {
        var (_, first) = Open();
        first.Cycle = "100";
        Assert.True(_service.SlotWriter.Write(first.BuildWritePlan(), LocalTwo).Success);

        var (_, second) = Open();
        second.Cycle = "200";
        var result = _service.SlotWriter.Write(second.BuildWritePlan(), LocalTwo);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal(200, Reread().CycleNum);
    }

    /// <summary>
    /// The editor was opened against bytes that are no longer there, which is what happens when the
    /// game plays a cycle while the panel sits open. The write refuses rather than rolling the slot
    /// back to an edited copy of an older save.
    /// </summary>
    [Fact]
    public void An_editor_left_open_while_the_save_changed_refuses_to_write()
    {
        var (_, stale) = Open();
        stale.Cycle = "500";

        var (_, other) = Open();
        other.Cycle = "600";
        Assert.True(_service.SlotWriter.Write(other.BuildWritePlan(), LocalTwo).Success);

        var result = _service.SlotWriter.Write(stale.BuildWritePlan(), LocalTwo);

        Assert.False(result.Success);
        Assert.False(result.LiveFolderModified);
        Assert.Equal(600, Reread().CycleNum);
    }

    [Fact]
    public void Nothing_is_written_while_the_game_is_running()
    {
        var (_, editor) = Open();
        editor.Cycle = "42";
        var plan = editor.BuildWritePlan();

        _detector.RunningProcessName = "RainWorld";

        Assert.Throws<GameRunningException>(() => _service.SlotWriter.Write(plan, LocalTwo));
    }

    [Fact]
    public void An_editor_with_no_changes_has_nothing_to_write()
    {
        var (_, editor) = Open();

        var plan = editor.BuildWritePlan();

        Assert.True(plan.IsNoOp);
        Assert.False(_service.SlotWriter.Write(plan, LocalTwo).Success);
    }

    private sealed class FakeDetector : IGameProcessDetector
    {
        public string? RunningProcessName { get; set; }

        public bool IsGameRunning(out string? processName)
        {
            processName = RunningProcessName;
            return RunningProcessName is not null;
        }
    }
}
