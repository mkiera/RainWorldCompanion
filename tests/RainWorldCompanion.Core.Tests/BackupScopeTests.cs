using RainWorldCompanion.Core.Backups;

namespace RainWorldCompanion.Tests;

/// <summary>
/// Scope decides what a restore is allowed to overwrite. The live folder holds files named
/// "sav - Copy" and "sav.bak" sitting next to "sav", so a name match that is anything looser
/// than exact would put a stale copy back over a real save.
/// </summary>
public class BackupScopeTests
{
    [Fact]
    public void Enumerate_returns_exactly_the_in_scope_files()
    {
        using var live = new TempDirectory("live");
        SaveTree.Populate(live);

        var found = new BackupScope(live.Path).Enumerate().Select(e => e.RelativePath);

        Assert.Equal(SaveTree.Sorted(SaveTree.InScope), SaveTree.Sorted(found));
    }

    [Theory]
    [InlineData("sav - Copy")]
    [InlineData("sav - Copy (2)")]
    [InlineData("sav.bak")]
    public void The_stray_copies_sitting_beside_sav_are_excluded(string decoy)
    {
        using var live = new TempDirectory("live");
        SaveTree.Populate(live);

        var found = SaveTree.Sorted(new BackupScope(live.Path).Enumerate().Select(e => e.RelativePath));

        Assert.DoesNotContain(decoy, found);
    }

    [Fact]
    public void The_options_file_is_excluded()
    {
        using var live = new TempDirectory("live");
        SaveTree.Populate(live);

        var found = SaveTree.Sorted(new BackupScope(live.Path).Enumerate().Select(e => e.RelativePath));

        Assert.DoesNotContain("options", found);
    }

    [Fact]
    public void Steam_cloud_state_is_excluded_wherever_it_sits()
    {
        using var live = new TempDirectory("live");
        SaveTree.Populate(live);

        var found = SaveTree.Sorted(new BackupScope(live.Path).Enumerate().Select(e => e.RelativePath));

        Assert.DoesNotContain("steam_autocloud.vdf", found);
        Assert.DoesNotContain(@"ModConfigs\steam_autocloud.vdf", found);
    }

    [Fact]
    public void Every_mod_config_under_ModConfigs_is_included()
    {
        // The rule is every .txt in ModConfigs, not devourment.txt alone. A player who has to
        // restore a save wants the mod settings that save was played with back as well, and the
        // files are a few kilobytes each.
        using var live = new TempDirectory("live");
        SaveTree.Populate(live);

        var found = SaveTree.Sorted(new BackupScope(live.Path).Enumerate().Select(e => e.RelativePath));

        Assert.Contains(@"ModConfigs\devourment.txt", found);
        Assert.Contains(@"ModConfigs\moreslugcats.txt", found);
    }

    [Fact]
    public void Nothing_under_a_backup_folder_inside_the_save_root_is_included()
    {
        using var live = new TempDirectory("live");
        SaveTree.Populate(live);

        var found = SaveTree.Sorted(new BackupScope(live.Path).Enumerate().Select(e => e.RelativePath));

        Assert.DoesNotContain(found, p => p.StartsWith(@"backup\", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_zero_length_devourment_story_file_is_still_included()
    {
        using var live = new TempDirectory("live");
        SaveTree.Populate(live);

        var entries = new BackupScope(live.Path).Enumerate();

        var empty = Assert.Single(entries, e => SaveTree.Normalize(e.RelativePath)
            .Equals(SaveTree.EmptyStoryFile, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0L, empty.Length);
    }

    [Fact]
    public void Every_entry_carries_a_full_path_that_exists_and_a_matching_length()
    {
        using var live = new TempDirectory("live");
        SaveTree.Populate(live);

        foreach (var entry in new BackupScope(live.Path).Enumerate())
        {
            Assert.True(File.Exists(entry.FullPath), $"{entry.RelativePath} has a full path that does not exist");
            Assert.Equal(new FileInfo(entry.FullPath).Length, entry.Length);
            Assert.False(System.IO.Path.IsPathRooted(entry.RelativePath), $"{entry.RelativePath} is not relative");
            Assert.NotEqual(default(DateTime), entry.LastWriteUtc);
        }
    }

    [Fact]
    public void Only_files_that_exist_are_enumerated()
    {
        using var live = new TempDirectory("live");
        SaveTree.Populate(live);
        File.Delete(live.Resolve("sav3"));

        var found = SaveTree.Sorted(new BackupScope(live.Path).Enumerate().Select(e => e.RelativePath));

        Assert.DoesNotContain("sav3", found);
        Assert.Contains("sav2", found);
    }

    [Fact]
    public void An_empty_save_root_enumerates_to_nothing()
    {
        using var live = new TempDirectory("live");

        Assert.Empty(new BackupScope(live.Path).Enumerate());
    }

    [Fact]
    public void A_missing_save_root_enumerates_to_nothing_rather_than_throwing()
    {
        using var parent = new TempDirectory();

        Assert.Empty(new BackupScope(parent.Resolve("no-such-save-folder")).Enumerate());
    }

    [Fact]
    public void IsInScope_agrees_with_Enumerate_for_every_file_in_the_tree()
    {
        using var live = new TempDirectory("live");
        SaveTree.Populate(live);
        var scope = new BackupScope(live.Path);
        var enumerated = new HashSet<string>(
            scope.Enumerate().Select(e => SaveTree.Normalize(e.RelativePath)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var relativePath in SaveTree.InScope.Concat(SaveTree.OutOfScope))
        {
            var expected = enumerated.Contains(SaveTree.Normalize(relativePath));

            Assert.Equal(expected, scope.IsInScope(relativePath));
        }
    }

    [Fact]
    public void IsInScope_gives_the_same_answer_for_both_separator_styles()
    {
        using var live = new TempDirectory("live");
        SaveTree.Populate(live);
        var scope = new BackupScope(live.Path);

        foreach (var relativePath in SaveTree.InScope.Concat(SaveTree.OutOfScope))
        {
            var backslash = relativePath.Replace('/', '\\');
            var forwardSlash = relativePath.Replace('\\', '/');

            Assert.Equal(scope.IsInScope(backslash), scope.IsInScope(forwardSlash));
        }
    }

    [Fact]
    public void IsInScope_ignores_case()
    {
        using var live = new TempDirectory("live");
        SaveTree.Populate(live);
        var scope = new BackupScope(live.Path);

        foreach (var relativePath in SaveTree.InScope.Concat(SaveTree.OutOfScope))
        {
            Assert.Equal(scope.IsInScope(relativePath), scope.IsInScope(relativePath.ToUpperInvariant()));
            Assert.Equal(scope.IsInScope(relativePath), scope.IsInScope(relativePath.ToLowerInvariant()));
        }
    }

    [Fact]
    public void SaveRoot_is_the_path_the_scope_was_built_for()
    {
        using var live = new TempDirectory("live");

        Assert.Equal(live.Path, new BackupScope(live.Path).SaveRoot);
    }

    [Fact]
    public void DescribeRules_returns_something_the_settings_screen_can_show()
    {
        var rules = BackupScope.DescribeRules();

        Assert.NotEmpty(rules);
        Assert.All(rules, rule => Assert.False(string.IsNullOrWhiteSpace(rule)));
    }
}
