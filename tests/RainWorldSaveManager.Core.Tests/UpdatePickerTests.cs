using RainWorldSaveManager.Core.Updates;

namespace RainWorldSaveManager.Tests;

/// <summary>
/// What the app offers, and what it refuses to offer.
///
/// Every case here is a way of being wrong that says nothing at the time. Offering the running
/// version back to itself looks like a permanent update badge, skipping a whole release because a
/// later one is still uploading looks like there is nothing new, and letting an address off the
/// allowlist through means fetching an executable from somewhere nobody chose.
/// </summary>
public class UpdatePickerTests
{
    private const string Host = "https://objects.githubusercontent.com/rwsm";

    private static ReleaseAsset Installer(long size = 45_000_000) =>
        new("RainWorldCompanion-Setup.exe", Host + "/setup.exe", size);

    private static ReleaseCandidate Release(
        string tag,
        bool prereleaseFlag = false,
        bool draft = false,
        IReadOnlyList<ReleaseAsset>? assets = null) =>
        new(tag, "https://github.com/mkiera/RainWorldCompanion/releases/tag/" + tag,
            draft, prereleaseFlag, DateTimeOffset.UnixEpoch, assets ?? [Installer()]);

    private static SemVer V(string text)
    {
        Assert.True(SemVer.TryParse(text, out var version));
        return version;
    }

    private static UpdateOffer? Pick(
        IEnumerable<ReleaseCandidate> releases,
        string current,
        UpdateChannel channel = UpdateChannel.Stable) =>
        UpdatePicker.Pick(releases, V(current), channel);

    [Fact]
    public void The_running_version_is_not_offered_to_itself()
    {
        // The bug the reference apps both hit: ship a build whose version does not match its tag
        // and it is offered its own release forever, with an update badge that never clears.
        Assert.Null(Pick([Release("v1.1.0")], "1.1.0"));
        Assert.Null(Pick([Release("v1.1.0")], "1.2.0"));
    }

    [Fact]
    public void The_running_version_carrying_a_commit_is_still_not_offered_to_itself()
    {
        // The SDK appends the commit to the version the app reads, so this is what actually
        // reaches the picker in a real build.
        Assert.Null(Pick([Release("v1.1.0")], "1.1.0+a1b2c3d4e5f6"));
    }

    [Fact]
    public void A_release_still_uploading_does_not_hide_an_older_usable_one()
    {
        // A tag exists the moment the workflow starts and the asset appears minutes later, so a
        // release with no files on it is a normal sight rather than a fault. Stopping at it would
        // hide the release below that people can actually install.
        var releases = new[]
        {
            Release("v1.3.0", assets: []),
            Release("v1.2.0"),
        };

        Assert.Equal(V("1.2.0"), Pick(releases, "1.1.0")?.Version);
    }

    [Fact]
    public void The_newest_release_wins_regardless_of_the_order_GitHub_lists_them()
    {
        var releases = new[] { Release("v1.2.0"), Release("v1.9.0"), Release("v1.4.0") };

        Assert.Equal(V("1.9.0"), Pick(releases, "1.1.0")?.Version);
    }

    [Fact]
    public void The_stable_release_of_a_version_updates_its_prerelease()
    {
        // Someone on 1.1.0-beta.1 has to be able to reach 1.1.0, and on the stable channel that
        // is the only route off a beta they were handed.
        Assert.Equal(V("1.1.0"), Pick([Release("v1.1.0")], "1.1.0-beta.1")?.Version);
    }

    [Fact]
    public void The_first_stable_release_reaches_the_prerelease_builds_on_both_channels()
    {
        foreach (var channel in new[] { UpdateChannel.Stable, UpdateChannel.Prerelease })
        {
            Assert.Equal(
                V("1.0.0"),
                Pick([Release("v1.0.0")], "1.0.0-beta.4", channel)?.Version);
        }
    }

    [Fact]
    public void A_prerelease_is_hidden_from_the_stable_channel_and_shown_on_the_other()
    {
        var releases = new[] { Release("v1.2.0-beta.1", prereleaseFlag: true) };

        Assert.Null(Pick(releases, "1.1.0"));
        Assert.Equal(V("1.2.0-beta.1"), Pick(releases, "1.1.0", UpdateChannel.Prerelease)?.Version);
    }

    [Fact]
    public void A_release_the_author_forgot_to_tick_as_prerelease_is_still_one()
    {
        // The flag is set by hand for a release published by hand. The tag is not, so a tail on it
        // counts on its own.
        var releases = new[] { Release("v1.2.0-rc.1", prereleaseFlag: false) };

        Assert.Null(Pick(releases, "1.1.0"));

        var offered = Pick(releases, "1.1.0", UpdateChannel.Prerelease);
        Assert.NotNull(offered);
        Assert.True(offered!.IsPrerelease);
    }

    [Fact]
    public void A_draft_is_never_a_candidate()
    {
        Assert.Null(Pick([Release("v2.0.0", draft: true)], "1.1.0"));
    }

    [Fact]
    public void A_tag_that_is_not_a_version_is_passed_over_rather_than_guessed_at()
    {
        var releases = new[] { Release("nightly"), Release("v1.2"), Release("v1.2.0") };

        Assert.Equal(V("1.2.0"), Pick(releases, "1.1.0")?.Version);
    }

    [Fact]
    public void Only_the_setup_exe_is_ever_offered()
    {
        var releases = new[]
        {
            Release("v1.2.0", assets:
            [
                new ReleaseAsset("README.md", Host + "/readme", 400),
                new ReleaseAsset("RainWorldCompanion.exe", Host + "/app.exe", 45_000_000),
                new ReleaseAsset("RainWorldCompanion-Setup.exe", Host + "/setup.exe", 45_000_000),
            ]),
        };

        Assert.Equal("RainWorldCompanion-Setup.exe", Pick(releases, "1.1.0")?.AssetName);
    }

    [Fact]
    public void A_release_whose_only_exe_is_not_the_installer_is_not_offered()
    {
        // Running a bare app exe out of the downloads folder would start that version without
        // replacing anything, which looks exactly like a successful update until the next launch.
        var releases = new[]
        {
            Release("v1.2.0", assets:
                [new ReleaseAsset("RainWorldCompanion.exe", Host + "/app.exe", 45_000_000)]),
        };

        Assert.Null(Pick(releases, "1.1.0"));
    }

    [Fact]
    public void An_asset_with_no_stated_size_is_not_offered()
    {
        // Without a length there is nothing to check a finished download against, and a truncated
        // installer that still runs is the worst outcome available here.
        var releases = new[] { Release("v1.2.0", assets: [Installer(size: 0)]) };

        Assert.Null(Pick(releases, "1.1.0"));
    }

    [Theory]
    [InlineData("http://objects.githubusercontent.com/setup.exe")]
    [InlineData("https://example.invalid/setup.exe")]
    [InlineData("https://github.com.example.invalid/setup.exe")]
    [InlineData("file:///C:/setup.exe")]
    [InlineData("not a url")]
    public void An_asset_hosted_somewhere_off_the_allowlist_is_dropped(string url)
    {
        // The address arrives inside a JSON document fetched over the network, so it is data. The
        // file at the end of it gets executed.
        var releases = new[]
        {
            Release("v1.2.0", assets:
                [new ReleaseAsset("RainWorldCompanion-Setup.exe", url, 45_000_000)]),
        };

        Assert.Null(Pick(releases, "1.1.0"));
    }

    [Theory]
    [InlineData("../../evil-setup.exe")]
    [InlineData("a/b-setup.exe")]
    [InlineData("a\\b-setup.exe")]
    [InlineData("..-setup.exe")]
    [InlineData("C:-setup.exe")]
    public void An_asset_name_that_could_steer_the_download_is_dropped(string name)
    {
        var releases = new[]
        {
            Release("v1.2.0", assets: [new ReleaseAsset(name, Host + "/setup.exe", 45_000_000)]),
        };

        Assert.Null(Pick(releases, "1.1.0"));
    }

    [Fact]
    public void A_copy_that_cannot_say_what_version_it_is_gets_offered_nothing()
    {
        // The other direction, offering everything, turns an unreadable version into an automatic
        // reinstall of whatever happens to be newest.
        Assert.Null(UpdatePicker.Pick([Release("v9.9.9")], null, UpdateChannel.Stable));
    }

    [Fact]
    public void The_channel_list_holds_older_versions_so_there_is_a_way_back()
    {
        var releases = new[]
        {
            Release("v1.0.0"),
            Release("v1.2.0"),
            Release("v1.1.0"),
            Release("v1.3.0-beta.1", prereleaseFlag: true),
        };

        var stable = UpdatePicker.ForChannel(releases, UpdateChannel.Stable);
        Assert.Equal(["1.2.0", "1.1.0", "1.0.0"], stable.Select(o => o.VersionText));

        var prerelease = UpdatePicker.ForChannel(releases, UpdateChannel.Prerelease);
        Assert.Equal(["1.3.0-beta.1", "1.2.0", "1.1.0", "1.0.0"], prerelease.Select(o => o.VersionText));
    }

    [Fact]
    public void The_release_list_is_empty_for_the_branch_build_channel()
    {
        // Branch builds do not come from releases. Falling back to stable here would put release
        // rows under a heading that says otherwise.
        Assert.Empty(UpdatePicker.ForChannel([Release("v1.2.0")], UpdateChannel.Alpha));
        Assert.Null(Pick([Release("v9.9.9")], "1.0.0", UpdateChannel.Alpha));
    }

    [Fact]
    public void An_empty_release_list_offers_nothing_rather_than_failing()
    {
        Assert.Null(Pick([], "1.1.0"));
        Assert.Empty(UpdatePicker.ForChannel([], UpdateChannel.Stable));
    }
}
