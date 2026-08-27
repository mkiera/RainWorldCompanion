using RainWorldCompanion.Core.Backups;
using RainWorldCompanion.Core.Mods;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.App.Tests;

/// <summary>
/// The panel's read-only account of which mods' settings a save carries. The wording is most of
/// what this is, because an empty list means three different things and only one of them is
/// "no mod had settings".
/// </summary>
public class ModConfigPanelTests
{
    private static ModConfigFile File(string relativePath, string modId, long sizeBytes = 64)
        => new() { RelativePath = relativePath, ModId = modId, SizeBytes = sizeBytes };

    private static ModConfigSet Set(params ModConfigFile[] files)
        => new() { ReadTheFolder = true, Files = files.ToList() };

    private static ManifestFileEntry Backed(string relativePath, long sizeBytes = 64)
        => new(relativePath, sizeBytes, new string('a', 64), DateTime.UtcNow);

    // ---- the three empty answers ----

    [Fact]
    public void A_save_stored_before_settings_were_kept_says_so()
    {
        var section = ModConfigSectionViewModel.ForRecorded(null, fromABackup: false);

        Assert.Equal("No mod settings were recorded when this save was stored.", section.EmptyText);
        Assert.False(section.HasCount);
    }

    [Fact]
    public void A_backup_taken_before_settings_were_kept_says_it_differently()
    {
        var section = ModConfigSectionViewModel.ForRecorded(null, fromABackup: true);

        Assert.Contains("backup", section.EmptyText);
    }

    /// <summary>A folder nobody could read is not a save with no settings, and the two must never
    /// be shown the same way.</summary>
    [Fact]
    public void A_folder_that_could_not_be_read_says_that_rather_than_no_settings()
    {
        var section = ModConfigSectionViewModel.ForRecorded(
            new ModConfigSet { ReadTheFolder = false, Note = "The save folder is not known." },
            fromABackup: false);

        Assert.Equal("The save folder is not known.", section.EmptyText);
        Assert.False(section.HasCount);
        Assert.False(section.HasRows);
    }

    [Fact]
    public void A_save_whose_folder_was_read_and_held_nothing_says_no_mod_settings()
    {
        var section = ModConfigSectionViewModel.ForRecorded(Set(), fromABackup: false);

        Assert.Equal("No mod settings", section.CountText);
        Assert.False(section.HasEmptyText);
    }

    // ---- rows ----

    [Fact]
    public void A_mod_that_owns_several_files_is_one_row()
    {
        var section = ModConfigSectionViewModel.ForRecorded(
            Set(File(@"ModConfigs\devourment.txt", "devourment", 2048),
                File(@"ModConfigs\DvrmentConfs\current.json", "devourment", 1024)),
            fromABackup: false);

        Assert.Equal("1 mod", section.CountText);
        ModConfigRowSummary row = Assert.Single(section.Rows);
        Assert.Equal("devourment", row.Name);
        Assert.Contains("2 files", row.DetailText);
        Assert.Contains("3.0 KB", row.DetailText);
    }

    [Fact]
    public void The_live_folder_is_described_the_same_way()
    {
        var section = ModConfigSectionViewModel.ForCurrent(Set(File(@"ModConfigs\devourment.txt", "devourment")));

        Assert.Equal("1 mod", section.CountText);
        Assert.Single(section.Rows);
    }

    // ---- a backup, derived from its manifest ----

    /// <summary>
    /// A backup holds the files and lists them in its manifest, so its section is derived from that
    /// list through the same rule the reader uses. A second index could only disagree with the first.
    /// </summary>
    [Fact]
    public void A_backup_is_described_from_the_files_it_actually_took()
    {
        var section = ModConfigSectionViewModel.ForBackup(new[]
        {
            Backed("sav"),
            Backed(@"ModConfigs\devourment.txt", 2048),
            Backed(@"ModConfigs\DvrmentConfs\current.json", 1024),
            Backed(@"dvrmentSaveStates\contents_0_White_story.txt"),
        });

        Assert.Equal("1 mod", section.CountText);
        Assert.Contains("2 files", Assert.Single(section.Rows).DetailText);
    }

    /// <summary>What a backup takes is wider than what travels, so the section shows the travelling
    /// half rather than everything under ModConfigs.</summary>
    [Fact]
    public void A_backup_leaves_out_what_it_took_but_does_not_carry()
    {
        var section = ModConfigSectionViewModel.ForBackup(new[]
        {
            Backed(@"ModConfigs\devourment.txt"),
            Backed(@"ModConfigs\MapOptions\cache.json"),
            Backed(@"ModConfigs\steam_autocloud.vdf"),
        });

        Assert.Equal("devourment", Assert.Single(section.Rows).Name);
    }

    [Fact]
    public void A_backup_of_a_folder_with_no_mod_settings_says_no_mod_settings()
    {
        var section = ModConfigSectionViewModel.ForBackup(new[] { Backed("sav") });

        Assert.Equal("No mod settings", section.CountText);
        Assert.False(section.HasEmptyText);
    }

    /// <summary>
    /// A backup taken long before this app tracked mod settings still shows what it holds, because
    /// ModConfigs has been in backup scope since the first version: nothing was added to it, this is
    /// just the first time anything read what was always there.
    /// </summary>
    [Fact]
    public void A_backup_older_than_the_feature_still_shows_what_it_holds()
    {
        var section = ModConfigSectionViewModel.ForBackup(new[]
        {
            Backed("sav"),
            Backed(@"ModConfigs\devourment.txt"),
        });

        Assert.Equal("1 mod", section.CountText);
    }

    [Fact]
    public void A_backup_with_no_manifest_says_it_recorded_nothing()
    {
        var section = ModConfigSectionViewModel.ForBackup(null);

        Assert.Contains("no manifest", section.EmptyText);
        Assert.False(section.HasCount);
    }
}
