using RainWorldCompanion.Core.Mods;

namespace RainWorldCompanion.Tests;

/// <summary>
/// Whether taking a mod's settings would change anything. Three states have to stay apart: the
/// same as yours, different from yours, and nothing to compare against. Showing the third as
/// either of the first two would be a claim nobody checked.
/// </summary>
public class ModConfigMatchingTests
{
    private const string Devourment = "devourment";

    private static ModConfigFile File(string path, string hash, string modId = Devourment) =>
        new() { RelativePath = path, ModId = modId, Sha256 = hash, SizeBytes = 10 };

    private static ModConfigSet Live(params ModConfigFile[] files)
    {
        var set = new ModConfigSet { ReadTheFolder = true };
        set.Files.AddRange(files);
        return set;
    }

    private static ModConfigGroup Recorded(params ModConfigFile[] files) => new(Devourment, files);

    [Fact]
    public void The_same_bytes_are_the_same_settings()
    {
        var match = ModConfigMatching.For(
            Recorded(File(@"ModConfigs\devourment.txt", "aaa")),
            Live(File(@"ModConfigs\devourment.txt", "aaa")));

        Assert.Equal(ModConfigMatch.Same, match);
        Assert.Equal("Same as yours", ModConfigMatching.Describe(match));
    }

    [Fact]
    public void A_digest_is_compared_without_regard_to_case()
    {
        // One side may have been written by a build that upper-cased its hex.
        var match = ModConfigMatching.For(
            Recorded(File(@"ModConfigs\devourment.txt", "abc123")),
            Live(File(@"ModConfigs\devourment.txt", "ABC123")));

        Assert.Equal(ModConfigMatch.Same, match);
    }

    [Fact]
    public void Different_bytes_are_different_settings()
    {
        var match = ModConfigMatching.For(
            Recorded(File(@"ModConfigs\devourment.txt", "aaa")),
            Live(File(@"ModConfigs\devourment.txt", "bbb")));

        Assert.Equal(ModConfigMatch.Different, match);
        Assert.Equal("Different from yours", ModConfigMatching.Describe(match));
    }

    [Fact]
    public void Nothing_here_for_that_mod_is_new_to_you()
    {
        var match = ModConfigMatching.For(
            Recorded(File(@"ModConfigs\devourment.txt", "aaa")),
            Live(File(@"ModConfigs\karmacontrol.txt", "bbb", "karmacontrol")));

        Assert.Equal(ModConfigMatch.New, match);
        Assert.Equal("New to you", ModConfigMatching.Describe(match));
    }

    /// <summary>
    /// The three-state rule these share with the mod list: an empty list has to be able to mean
    /// "there were none" or "we could not tell".
    /// </summary>
    [Fact]
    public void A_folder_nobody_could_read_makes_no_claim()
    {
        var recorded = Recorded(File(@"ModConfigs\devourment.txt", "aaa"));

        Assert.Equal(ModConfigMatch.Unknown, ModConfigMatching.For(recorded, null));
        Assert.Equal(ModConfigMatch.Unknown, ModConfigMatching.For(recorded, new ModConfigSet()));
        Assert.Equal("", ModConfigMatching.Describe(ModConfigMatch.Unknown));
    }

    [Fact]
    public void A_side_with_no_digest_makes_no_claim()
    {
        // What a snapshot written before digests were recorded reads back as.
        Assert.Equal(
            ModConfigMatch.Unknown,
            ModConfigMatching.For(
                Recorded(File(@"ModConfigs\devourment.txt", "")),
                Live(File(@"ModConfigs\devourment.txt", "aaa"))));

        Assert.Equal(
            ModConfigMatch.Unknown,
            ModConfigMatching.For(
                Recorded(File(@"ModConfigs\devourment.txt", "aaa")),
                Live(File(@"ModConfigs\devourment.txt", ""))));
    }

    /// <summary>Devourment owns its .txt and its DvrmentConfs tree, and both have to agree.</summary>
    [Fact]
    public void Every_file_the_mod_owns_has_to_match()
    {
        var recorded = Recorded(
            File(@"ModConfigs\devourment.txt", "aaa"),
            File(@"ModConfigs\DvrmentConfs\current.json", "bbb"));

        Assert.Equal(
            ModConfigMatch.Same,
            ModConfigMatching.For(recorded, Live(
                File(@"ModConfigs\devourment.txt", "aaa"),
                File(@"ModConfigs\DvrmentConfs\current.json", "bbb"))));

        Assert.Equal(
            ModConfigMatch.Different,
            ModConfigMatching.For(recorded, Live(
                File(@"ModConfigs\devourment.txt", "aaa"),
                File(@"ModConfigs\DvrmentConfs\current.json", "zzz"))));
    }

    [Fact]
    public void A_file_on_one_side_only_is_a_difference()
    {
        var recorded = Recorded(
            File(@"ModConfigs\devourment.txt", "aaa"),
            File(@"ModConfigs\DvrmentConfs\current.json", "bbb"));

        Assert.Equal(
            ModConfigMatch.Different,
            ModConfigMatching.For(recorded, Live(File(@"ModConfigs\devourment.txt", "aaa"))));

        Assert.Equal(
            ModConfigMatch.Different,
            ModConfigMatching.For(
                Recorded(File(@"ModConfigs\devourment.txt", "aaa")),
                Live(
                    File(@"ModConfigs\devourment.txt", "aaa"),
                    File(@"ModConfigs\DvrmentConfs\current.json", "bbb"))));
    }

    [Fact]
    public void A_file_that_moved_is_a_difference_even_at_the_same_digest()
    {
        Assert.Equal(
            ModConfigMatch.Different,
            ModConfigMatching.For(
                Recorded(File(@"ModConfigs\devourment.txt", "aaa")),
                Live(File(@"ModConfigs\DvrmentConfs\devourment.txt", "aaa"))));
    }

    [Fact]
    public void A_mod_owning_nothing_here_is_new_even_when_others_are_read()
    {
        Assert.Equal(
            ModConfigMatch.New,
            ModConfigMatching.For(Recorded(File(@"ModConfigs\devourment.txt", "aaa")), Live()));
    }
}
