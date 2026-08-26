using RainWorldCompanion.Core.Editing;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.Core.Saves.Models;

namespace RainWorldCompanion.Tests;

/// <summary>
/// An edit must change only what it was asked to change. These work on a copy of a real slot, so
/// it is proved against a payload holding map records, region states, and every field this app
/// does not model, which is where a rebuilt payload would lose data.
/// </summary>
public class SaveEditSessionTests
{
    private const string Cycle = "CYCLENUM";
    private const string DenPos = "DENPOS";
    private const string DeathPersistent = "DEATHPERSISTENTSAVEDATA";

    private static (TempDirectory Directory, string Path) LiveSlot(string fixture = FixtureFiles.Sav2)
    {
        var directory = new TempDirectory();
        return (directory, FixtureFiles.CopyTo(directory, fixture, "sav2"));
    }

    [Fact]
    public void A_session_with_no_edits_writes_the_file_it_opened()
    {
        var (directory, path) = LiveSlot();
        using (directory)
        {
            var session = SaveEditSession.Open(path);

            var plan = session.BuildWritePlan();

            Assert.False(session.IsDirty);
            Assert.True(plan.IsNoOp);
            Assert.Empty(plan.Problems);
            Assert.Equal(File.ReadAllBytes(path), plan.NewBytes);
        }
    }

    [Fact]
    public void Editing_a_value_and_putting_it_back_writes_the_file_it_opened()
    {
        var (directory, path) = LiveSlot();
        using (directory)
        {
            var session = SaveEditSession.Open(path);
            var campaign = session.Campaigns[0];
            var before = session.GetFieldValue(campaign, Cycle)!;

            session.SetField(campaign, Cycle, "999");
            session.SetField(campaign, Cycle, before);

            Assert.False(session.IsDirty);
            Assert.Equal(File.ReadAllBytes(path), session.BuildWritePlan().NewBytes);
        }
    }

    [Fact]
    public void Setting_a_cycle_changes_that_campaign_and_leaves_the_others_alone()
    {
        var (directory, path) = LiveSlot();
        using (directory)
        {
            var session = SaveEditSession.Open(path);
            var before = SavePayloadReader.SplitRecords(session.Payload);
            var campaign = session.Campaigns[0];

            session.SetField(campaign, Cycle, "1234");

            var after = SavePayloadReader.SplitRecords(session.Payload);
            Assert.Equal(before.Count, after.Count);

            for (var i = 0; i < before.Count; i++)
            {
                Assert.Equal(before[i].Header, after[i].Header);

                if (i != campaign.RecordIndex)
                {
                    Assert.Equal(before[i].Body, after[i].Body);
                }
            }

            Assert.Equal("1234", session.GetFieldValue(campaign, Cycle));
            Assert.Empty(session.BuildWritePlan().Problems);
        }
    }

    /// <summary>Writes the edit, reads it back with the production reader, and compares every field to what it was.</summary>
    [Fact]
    public void An_edited_save_reads_back_with_only_the_edited_field_changed()
    {
        var (directory, path) = LiveSlot();
        using (directory)
        {
            var before = SaveMetadataExtractor.Extract(path, 1);

            var session = SaveEditSession.Open(path);
            session.SetField(session.Campaigns[0], Cycle, "1234");
            var plan = session.BuildWritePlan();
            Assert.Empty(plan.Problems);

            var edited = directory.WriteBytes("edited", plan.NewBytes);
            var after = SaveMetadataExtractor.Extract(edited, 1);

            Assert.Null(after.ParseError);
            Assert.True(after.ChecksumValid);
            Assert.Equal(before.RecordCount, after.RecordCount);
            Assert.Equal(before.Campaigns.Count, after.Campaigns.Count);

            var first = after.Campaigns[0];
            Assert.Equal(1234, first.CycleNum);

            // Everything else about that campaign, and every other campaign, is what it was. The
            // two derived properties come along because both are computed from the cycle number.
            AssertSameExcept(
                before.Campaigns[0],
                first,
                nameof(CampaignSummary.CycleNum),
                nameof(CampaignSummary.DisplayCycleNum),
                nameof(CampaignSummary.EffectiveRedsDeath));

            for (var i = 1; i < before.Campaigns.Count; i++)
            {
                AssertSameExcept(before.Campaigns[i], after.Campaigns[i]);
            }
        }
    }

    [Fact]
    public void A_shelter_can_be_overwritten()
    {
        var (directory, path) = LiveSlot();
        using (directory)
        {
            var session = SaveEditSession.Open(path);
            var campaign = session.Campaigns[0];

            session.SetField(campaign, DenPos, "SB_S06");

            Assert.Equal("SB_S06", session.GetFieldValue(campaign, DenPos));
            Assert.Empty(session.BuildWritePlan().Problems);
        }
    }

    [Fact]
    public void A_flag_can_be_added_and_taken_away_again()
    {
        var (directory, path) = LiveSlot();
        using (directory)
        {
            var session = SaveEditSession.Open(path);
            var campaign = session.Campaigns[0];
            var originalPayload = session.Payload;

            session.SetFlag(campaign, "HASTHEGLOW", true);
            Assert.True(session.HasField(campaign, "HASTHEGLOW"));
            Assert.Empty(session.BuildWritePlan().Problems);

            session.SetFlag(campaign, "HASTHEGLOW", false);
            Assert.False(session.HasField(campaign, "HASTHEGLOW"));
            Assert.Equal(originalPayload, session.Payload);
        }
    }

    [Fact]
    public void Adding_a_field_the_campaign_never_had_leaves_the_record_readable()
    {
        var (directory, path) = LiveSlot();
        using (directory)
        {
            var session = SaveEditSession.Open(path);
            var campaign = session.Campaigns[0];

            session.SetField(campaign, "TOTFOOD", "42");

            var plan = session.BuildWritePlan();
            Assert.Empty(plan.Problems);

            var edited = directory.WriteBytes("edited", plan.NewBytes);
            Assert.Equal(42, SaveMetadataExtractor.Extract(edited, 1).Campaigns[0].TotalFoodEaten);
        }
    }

    [Fact]
    public void Every_field_of_a_campaign_can_be_listed_with_repeats_numbered()
    {
        var payload = SyntheticSave.SavePayload(devourmentStates: 3);
        var (directory, path) = SyntheticSlot(payload);
        using (directory)
        {
            var session = SaveEditSession.Open(path);

            var fields = session.EnumerateFields(session.Campaigns[0]);

            var devourment = fields.Where(f => f.Key == "DEVOURMENTSTATE").ToList();
            Assert.Equal(3, devourment.Count);
            Assert.Equal(new[] { 0, 1, 2 }, devourment.Select(f => f.Occurrence));
            Assert.Contains(fields, f => f.Key == "SAV STATE NUMBER" && f.Value == "White");
        }
    }

    [Fact]
    public void One_occurrence_of_a_repeated_field_can_be_replaced_without_touching_the_others()
    {
        var payload = SyntheticSave.SavePayload(devourmentStates: 3);
        var (directory, path) = SyntheticSlot(payload);
        using (directory)
        {
            var session = SaveEditSession.Open(path);
            var campaign = session.Campaigns[0];

            session.ReplaceFieldOccurrence(campaign, "DEVOURMENTSTATE", 1, "PredX<dvD>PreyX<dvD>Held<dvD>9");

            var devourment = session.EnumerateFields(campaign).Where(f => f.Key == "DEVOURMENTSTATE").ToList();
            Assert.Equal(3, devourment.Count);
            Assert.StartsWith("Pred0", devourment[0].Value);
            Assert.Equal("PredX<dvD>PreyX<dvD>Held<dvD>9", devourment[1].Value);
            Assert.StartsWith("Pred2", devourment[2].Value);
            Assert.Empty(session.BuildWritePlan().Problems);
        }
    }

    [Fact]
    public void Removing_one_occurrence_leaves_the_rest_of_the_record_intact()
    {
        var payload = SyntheticSave.SavePayload(devourmentStates: 3);
        var (directory, path) = SyntheticSlot(payload);
        using (directory)
        {
            var session = SaveEditSession.Open(path);
            var campaign = session.Campaigns[0];

            session.RemoveField(campaign, "DEVOURMENTSTATE", 1);

            var fields = session.EnumerateFields(campaign);
            var devourment = fields.Where(f => f.Key == "DEVOURMENTSTATE").ToList();
            Assert.Equal(2, devourment.Count);
            Assert.StartsWith("Pred0", devourment[0].Value);
            Assert.StartsWith("Pred2", devourment[1].Value);

            Assert.Contains(fields, f => f.Key == "FOOD");
            Assert.Contains(fields, f => f.Key == "SAV STATE NUMBER");
            Assert.Empty(session.BuildWritePlan().Problems);
        }
    }

    [Fact]
    public void The_last_field_of_a_record_can_be_removed()
    {
        var payload = SyntheticSave.SavePayload(hasGlow: true);
        var (directory, path) = SyntheticSlot(payload);
        using (directory)
        {
            var session = SaveEditSession.Open(path);
            var campaign = session.Campaigns[0];

            session.SetFlag(campaign, "HASTHEGLOW", false);

            var plan = session.BuildWritePlan();
            Assert.Empty(plan.Problems);

            var edited = directory.WriteBytes("edited", plan.NewBytes);
            var campaigns = SaveMetadataExtractor.Extract(edited, 1).Campaigns;
            Assert.False(campaigns[0].HasGlow);
            Assert.Equal(3, campaigns[0].Food);
        }
    }

    [Fact]
    public void Editing_the_save_entry_never_touches_the_backup_entry()
    {
        var (directory, path) = LiveSlot();
        using (directory)
        {
            var backupBefore = SaveContainer.Read(path).Entries["save__Backup"];

            var session = SaveEditSession.Open(path);
            session.SetField(session.Campaigns[0], Cycle, "77");
            var edited = directory.WriteBytes("edited", session.BuildWritePlan().NewBytes);

            Assert.Equal(backupBefore, SaveContainer.Read(edited).Entries["save__Backup"]);
        }
    }

    [Fact]
    public void Changes_are_described_one_line_per_edit()
    {
        var (directory, path) = LiveSlot();
        using (directory)
        {
            var session = SaveEditSession.Open(path);
            var campaign = session.Campaigns[0];
            var before = session.GetFieldValue(campaign, Cycle);

            session.SetField(campaign, Cycle, "20");

            Assert.Equal($"Survivor: CYCLENUM {before} to 20", Assert.Single(session.Changes));
        }
    }

    /// <summary>
    /// A text box bound to a field writes it on every keystroke. All those writes are the same
    /// field moving from start to end, so the change log says so once.
    /// </summary>
    [Fact]
    public void A_field_written_over_and_over_is_one_change()
    {
        var (directory, path) = LiveSlot();
        using (directory)
        {
            var session = SaveEditSession.Open(path);
            var campaign = session.Campaigns[0];
            var before = session.GetFieldValue(campaign, Cycle);

            foreach (var typed in new[] { "1", "12", "123", "1234" })
            {
                session.SetField(campaign, Cycle, typed);
            }

            Assert.Equal($"Survivor: CYCLENUM {before} to 1234", Assert.Single(session.Changes));
        }
    }

    [Fact]
    public void A_field_typed_back_to_where_it_started_is_no_longer_a_change()
    {
        var (directory, path) = LiveSlot();
        using (directory)
        {
            var session = SaveEditSession.Open(path);
            var campaign = session.Campaigns[0];
            var before = session.GetFieldValue(campaign, Cycle)!;

            session.SetField(campaign, Cycle, "99");
            Assert.Single(session.Changes);

            session.SetField(campaign, Cycle, before);

            Assert.Empty(session.Changes);
            Assert.False(session.IsDirty);
        }
    }

    [Fact]
    public void Two_fields_are_two_changes_in_the_order_they_were_touched()
    {
        var (directory, path) = LiveSlot();
        using (directory)
        {
            var session = SaveEditSession.Open(path);
            var campaign = session.Campaigns[0];

            session.SetField(campaign, DenPos, "HI_S01");
            session.SetField(campaign, Cycle, "5");
            session.SetField(campaign, DenPos, "HI_S02");

            Assert.Equal(2, session.Changes.Count);
            Assert.StartsWith("Survivor: DENPOS", session.Changes[0], StringComparison.Ordinal);
            Assert.EndsWith("to HI_S02", session.Changes[0], StringComparison.Ordinal);
            Assert.Contains("CYCLENUM", session.Changes[1], StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// DEATHPERSISTENTSAVEDATA is one field carrying karma, the echoes and every gate. Written as
    /// one field it would read as one unpronounceable change however many of them moved.
    /// </summary>
    [Fact]
    public void Parts_of_one_composite_field_are_counted_separately()
    {
        var (directory, path) = LiveSlot();
        using (directory)
        {
            var session = SaveEditSession.Open(path);
            var campaign = session.Campaigns[0];

            var blob = session.GetFieldValue(campaign, DeathPersistent) ?? "";

            blob = DeathPersistentEditor.SetInt(blob, DeathPersistentEditor.KarmaField, 3);
            session.SetFieldPart(campaign, DeathPersistent, blob, "KARMA", "9", "3");

            blob = DeathPersistentEditor.SetEcho(blob, "SH", DeathPersistentEditor.EchoTalkedTo);
            session.SetFieldPart(campaign, DeathPersistent, blob, "Echo SH", "never seen", "talked to");

            Assert.Equal(
                new[] { "Survivor: KARMA 9 to 3", "Survivor: Echo SH never seen to talked to" },
                session.Changes);
        }
    }

    [Fact]
    public void Adding_a_field_reads_as_setting_it_and_removing_one_reads_as_removing_it()
    {
        var (directory, path) = LiveSlot();
        using (directory)
        {
            var session = SaveEditSession.Open(path);
            var campaign = session.Campaigns[0];

            // A key the record does not carry, so it reads as being set rather than moved.
            session.SetField(campaign, "SOMEMODFIELD", "42");
            session.RemoveField(campaign, Cycle);

            Assert.Equal("Survivor: set SOMEMODFIELD to 42", session.Changes[0]);
            Assert.Equal("Survivor: removed CYCLENUM", session.Changes[1]);
        }
    }

    [Fact]
    public void Setting_a_value_it_already_holds_is_not_an_edit()
    {
        var (directory, path) = LiveSlot();
        using (directory)
        {
            var session = SaveEditSession.Open(path);
            var campaign = session.Campaigns[0];

            session.SetField(campaign, Cycle, session.GetFieldValue(campaign, Cycle)!);

            Assert.False(session.IsDirty);
            Assert.Empty(session.Changes);
        }
    }

    [Fact]
    public void Campaigns_are_listed_with_the_slugcat_each_one_belongs_to()
    {
        var (directory, path) = LiveSlot();
        using (directory)
        {
            var session = SaveEditSession.Open(path);

            var expected = SaveMetadataExtractor.Extract(path, 1).Campaigns.Select(c => c.SlugcatId);

            Assert.Equal(expected, session.Campaigns.Select(c => c.SlugcatId));
        }
    }

    [Fact]
    public void A_save_whose_checksum_is_already_wrong_is_left_alone()
    {
        using var directory = new TempDirectory();
        var payload = SyntheticSave.SavePayload();
        var bytes = SyntheticSave.Bytes(new[]
        {
            SyntheticSave.Entry("save", SyntheticSave.WrapWithBadChecksum(payload)),
        });
        var path = directory.WriteBytes("sav2", bytes);

        var error = Assert.Throws<SaveContainerException>(() => SaveEditSession.Open(path));

        // Repairing the digest would turn a save the game refuses into one it accepts, without
        // repairing whatever damaged the payload.
        Assert.Contains("checksum the game will reject", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_with_no_save_entry_is_refused()
    {
        using var directory = new TempDirectory();
        var path = FixtureFiles.CopyTo(directory, FixtureFiles.ExpCore1, "expCore1");

        Assert.Throws<SaveContainerException>(() => SaveEditSession.Open(path));
    }

    [Fact]
    public void A_record_that_is_not_a_campaign_cannot_be_edited_as_one()
    {
        var (directory, path) = LiveSlot();
        using (directory)
        {
            var session = SaveEditSession.Open(path);

            var campaignIndexes = session.Campaigns.Select(c => c.RecordIndex).ToHashSet();
            var mapRecord = Enumerable
                .Range(0, SavePayloadReader.SplitRecords(session.Payload).Count)
                .First(i => !campaignIndexes.Contains(i));

            var notACampaign = new CampaignRecordRef(mapRecord, "White");

            Assert.Throws<SaveContainerException>(() => session.SetField(notACampaign, Cycle, "1"));
        }
    }

    [Fact]
    public void The_file_hash_is_recorded_so_a_later_write_can_tell_the_file_changed()
    {
        var (directory, path) = LiveSlot();
        using (directory)
        {
            var session = SaveEditSession.Open(path);

            Assert.Equal(
                RainWorldCompanion.Core.Backups.Hashing.ComputeFileSha256(path),
                session.FileSha256);
            Assert.Equal(session.FileSha256, session.BuildWritePlan().ExpectedFileSha256);
        }
    }

    [Fact]
    public void Editing_karma_leaves_the_rest_of_the_death_persistent_blob_alone()
    {
        var (directory, path) = LiveSlot();
        using (directory)
        {
            var session = SaveEditSession.Open(path);
            var campaign = session.Campaigns[0];
            var before = DeathPersistentReader.Read(session.GetFieldValue(campaign, DeathPersistent));

            var updated = DeathPersistentEditor.SetInt(
                session.GetFieldValue(campaign, DeathPersistent),
                DeathPersistentEditor.KarmaField,
                7);
            session.SetField(campaign, DeathPersistent, updated);

            var after = DeathPersistentReader.Read(session.GetFieldValue(campaign, DeathPersistent));

            Assert.Equal(7, after.Karma);
            PropertyComparison.AssertSameExcept(before, after, nameof(DeathPersistentData.Karma));
            Assert.Empty(session.BuildWritePlan().Problems);
        }
    }

    private static (TempDirectory Directory, string Path) SyntheticSlot(string payload)
    {
        var directory = new TempDirectory();
        return (directory, directory.WriteBytes("sav2", SyntheticSave.SaveFile(payload)));
    }

    private static void AssertSameExcept(CampaignSummary before, CampaignSummary after, params string[] ignored)
        => PropertyComparison.AssertSameExcept(before, after, ignored);
}
