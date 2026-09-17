using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Editing;
using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Tests;

public class DevourmentContentsTransferTests
{
    private const string SourceFirst = "Slugcat<cA>ID.-1.0<cB>0<cA>SU_S04.-1<cA><dvD>PinkLizard<cA>ID.8.9<cB>0<cA>SU_S04.0<cA><dvD>Held<dvD>6";
    private const string SourceSecond = "PinkLizard<cA>ID.8.9<cB>0<cA>SU_S04.0<cA><dvD>Fly<cA>ID.10.11<cB>0<cA>SU_S04.0<cA><dvD>Digesting<dvD>1";
    private const string TargetEntry = "Slugcat<cA>ID.-1.0<cB>0<cA>HI_S01.-1<cA><dvD>GreenLizard<cA>ID.20.21<cB>0<cA>HI_S01.0<cA><dvD>Healing<dvD>4";

    [Fact]
    public void Survivor_contents_replace_only_monks_devourment_fields()
    {
        using var files = new TempDirectory("devourment-contents");
        string targetBody = Body("Yellow", 41, TargetEntry);
        string path = WriteSlot(files, targetBody);
        var session = SaveEditSession.Open(path);
        CampaignRecordRef target = Assert.Single(session.Campaigns);
        IReadOnlyList<RawField> before = session.EnumerateFields(target);

        session.ApplyDevourmentContents(target, DevourmentContents.Capture(Slice("White", Body("White", 9, SourceFirst, SourceSecond))));

        IReadOnlyList<RawField> after = session.EnumerateFields(target);
        Assert.Equal(
            before.Where(field => field.Key != DevourmentEditState.EntryField),
            after.Where(field => field.Key != DevourmentEditState.EntryField));
        Assert.Equal(
            new[] { SourceFirst, SourceSecond },
            after.Where(field => field.Key == DevourmentEditState.EntryField).Select(field => field.Value));
        Assert.Equal("Yellow", session.GetFieldValue(target, "SAV STATE NUMBER"));
        Assert.Equal("target friend", session.GetFieldValue(target, DevourmentEditState.FriendsField));
        Assert.Empty(session.BuildWritePlan().Problems);
    }

    [Fact]
    public void Empty_source_contents_clear_the_target_without_changing_other_fields()
    {
        using var files = new TempDirectory("devourment-contents");
        string path = WriteSlot(files, Body("Yellow", 41, TargetEntry));
        var session = SaveEditSession.Open(path);
        CampaignRecordRef target = Assert.Single(session.Campaigns);

        session.ApplyDevourmentContents(target, DevourmentContents.Capture(Slice("White", Body("White", 9))));

        Assert.DoesNotContain(session.EnumerateFields(target), field => field.Key == DevourmentEditState.EntryField);
        Assert.Equal("41", session.GetFieldValue(target, "CYCLENUM"));
        Assert.Equal("target friend", session.GetFieldValue(target, DevourmentEditState.FriendsField));
    }

    [Fact]
    public void Applying_contents_through_the_slot_writer_takes_a_safety_backup()
    {
        using var live = new TempDirectory("devourment-live");
        using var backups = new TempDirectory("devourment-backups");
        string path = WriteSlot(live, Body("Yellow", 41, TargetEntry));
        byte[] before = File.ReadAllBytes(path);
        var service = new BackupService(live.Path, backups.Path, FakeGameDetector.NotRunning(), "test");
        DevourmentContents contents = DevourmentContents.Capture(Slice("White", Body("White", 9, SourceFirst, SourceSecond)));

        DevourmentContentsPlan plan = service.SlotWriter.PlanApplyDevourmentContents(
            new SaveSlotRef(SaveRealm.Local, 2),
            "Yellow",
            contents);
        SaveWriteResult result = service.SlotWriter.Write(plan);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        BackupSnapshot safety = Assert.IsType<BackupSnapshot>(result.SafetySnapshot);
        SnapshotLayout.AssertBytesEqual(before, File.ReadAllBytes(Path.Combine(safety.DirectoryPath, "sav2")), "safety sav2");

        var written = SaveEditSession.Open(path);
        CampaignRecordRef monk = Assert.Single(written.Campaigns);
        Assert.Equal("Yellow", monk.SlugcatId);
        Assert.Equal(
            new[] { SourceFirst, SourceSecond },
            written.EnumerateFields(monk)
                .Where(field => field.Key == DevourmentEditState.EntryField)
                .Select(field => field.Value));
    }

    private static CampaignSlice Slice(string slugcat, string body) => new(
        slugcat,
        "SAVE STATE" + SyntheticSave.HeaderSeparator + body,
        Array.Empty<string>());

    private static string Body(string slugcat, int cycle, params string[] entries)
    {
        var fields = new List<string>
        {
            "SAV STATE NUMBER" + SyntheticSave.ValueSeparator + slugcat,
            "TIMELINE" + SyntheticSave.ValueSeparator + slugcat,
            "CYCLENUM" + SyntheticSave.ValueSeparator + cycle,
            "FOOD" + SyntheticSave.ValueSeparator + "3",
            DevourmentEditState.FriendsField + SyntheticSave.ValueSeparator + "target friend",
        };

        fields.AddRange(entries.Select(entry =>
            DevourmentEditState.EntryField + SyntheticSave.ValueSeparator + entry));
        return string.Join(SyntheticSave.FieldSeparator, fields);
    }

    private static string WriteSlot(TempDirectory directory, string body)
    {
        string payload = SyntheticSave.Progression(new[]
        {
            ("SAVE STATE", body),
            ("MAP_Yellow", "map stays"),
            ("MISCPROG", "progress stays"),
        });
        return directory.WriteBytes("sav2", SyntheticSave.SaveFile(payload));
    }
}
