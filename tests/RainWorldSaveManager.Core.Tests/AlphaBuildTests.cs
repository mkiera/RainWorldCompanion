using RainWorldSaveManager.Core.Updates;

namespace RainWorldSaveManager.Tests;

/// <summary>
/// The list of branch builds. These are never offered on their own, so the risks are different
/// from a release: a row that cannot be downloaded, a row that claims to be the copy you are
/// running when it is not, and a branch name reaching a URL it was never safe in.
/// </summary>
public class AlphaBuildTests
{
    private static WorkflowRun Run(
        long id,
        string branch,
        int runNumber,
        string sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        string conclusion = "success") =>
        new(id, "Build Test", branch, sha, runNumber, conclusion, DateTimeOffset.UnixEpoch);

    private static BuildStamp Nothing => BuildStamp.ForVersion("1.0.0");

    [Fact]
    public void Only_the_newest_run_of_each_branch_is_listed()
    {
        // A branch gets rebuilt on every push, so the same branch arrives many times over. The
        // older runs are the same branch at an earlier commit and their artifacts expire first.
        var runs = new[]
        {
            Run(10, "feature/updater", 3),
            Run(11, "feature/updater", 5),
            Run(12, "feature/updater", 4),
            Run(13, "bugfix/paths", 6),
        };

        var builds = AlphaBuilds.FromRuns(runs, Nothing);

        Assert.Equal(2, builds.Count);
        Assert.Equal(13, builds[0].RunId);
        Assert.Equal(11, builds.Single(b => b.Branch == "feature/updater").RunId);
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("cancelled")]
    [InlineData("skipped")]
    [InlineData("")]
    public void A_run_that_did_not_succeed_is_not_listed(string conclusion)
    {
        // A failed run uploaded no artifact, so its row would be a download that answers 404.
        var builds = AlphaBuilds.FromRuns([Run(10, "feature/x", 1, conclusion: conclusion)], Nothing);

        Assert.Empty(builds);
    }

    [Fact]
    public void The_list_is_capped_so_one_look_cannot_spend_the_request_budget()
    {
        var runs = Enumerable.Range(1, 20).Select(i => Run(i, "feature/branch-" + i, i)).ToArray();

        Assert.Equal(AlphaBuilds.MaxBranches, AlphaBuilds.FromRuns(runs, Nothing).Count);
    }

    [Fact]
    public void A_branch_name_holding_a_slash_never_reaches_the_download_address()
    {
        // The address is built from the run id and a fixed artifact name, so a branch called
        // feature/crop-handles cannot put a path segment anywhere.
        var builds = AlphaBuilds.FromRuns([Run(4242, "feature/crop-handles", 7)], Nothing);

        Assert.Equal(
            "https://nightly.link/mkiera/RainWorldCompanion/actions/runs/4242/RainWorldCompanion-Setup.zip",
            builds[0].DownloadUrl);
        Assert.True(UpdateUrls.IsAllowedDownload(builds[0].DownloadUrl));
        Assert.Equal("feature/crop-handles #7", builds[0].Label);
    }

    [Fact]
    public void The_running_copy_is_marked_by_its_run_before_its_commit()
    {
        // A branch can be rebuilt at the same commit, and the run is what the installed copy came
        // out of, so it is the more specific answer of the two.
        var runs = new[]
        {
            Run(100, "feature/a", 1, sha: "1111111111111111111111111111111111111111"),
            Run(200, "feature/b", 2, sha: "1111111111111111111111111111111111111111"),
        };
        var running = new BuildStamp("1.0.0-alpha.2", "1111111111111111111111111111111111111111", "feature/b", "200");

        var builds = AlphaBuilds.FromRuns(runs, running);

        Assert.True(builds.Single(b => b.RunId == 200).IsRunningCopy);
        Assert.False(builds.Single(b => b.RunId == 100).IsRunningCopy);
    }

    [Fact]
    public void The_commit_marks_the_running_copy_when_there_is_no_run_to_match()
    {
        var runs = new[] { Run(100, "feature/a", 1, sha: "abcdef0123456789abcdef0123456789abcdef01") };
        var running = new BuildStamp("1.0.0-alpha.1", "ABCDEF0123456789ABCDEF0123456789ABCDEF01", "", "");

        Assert.True(AlphaBuilds.FromRuns(runs, running)[0].IsRunningCopy);
    }

    [Fact]
    public void A_copy_with_no_stamp_at_all_marks_nothing()
    {
        // Marking nothing has to be distinguishable from "you are on none of these", which is the
        // window's job to say. Guessing a row here would be worse than saying nothing.
        var builds = AlphaBuilds.FromRuns([Run(100, "feature/a", 1)], Nothing);

        Assert.False(builds[0].IsRunningCopy);
        Assert.Equal("aaaaaaa", builds[0].ShortSha);
    }

    [Fact]
    public void An_empty_run_list_produces_an_empty_build_list()
    {
        Assert.Empty(AlphaBuilds.FromRuns([], Nothing));
    }
}

/// <summary>
/// The addresses the app is willing to fetch from, and the names it is willing to write to disk.
/// Both arrive inside a document downloaded over the network, and the file at the end of them is
/// executed, so neither is taken on trust.
/// </summary>
public class UpdateUrlTests
{
    [Theory]
    [InlineData("https://github.com/mkiera/RainWorldCompanion/releases/download/v1/x-setup.exe")]
    [InlineData("https://objects.githubusercontent.com/x")]
    [InlineData("https://release-assets.githubusercontent.com/x")]
    [InlineData("https://nightly.link/mkiera/RainWorldCompanion/actions/runs/1/x.zip")]
    [InlineData("https://API.GITHUB.COM/repos/x")]
    public void An_address_on_the_allowlist_is_accepted(string url)
    {
        Assert.True(UpdateUrls.IsAllowedDownload(url));
    }

    [Theory]
    [InlineData("http://github.com/x")]
    [InlineData("https://githubusercontent.com/x")]
    [InlineData("https://github.com.example.invalid/x")]
    [InlineData("https://example.invalid/x")]
    [InlineData("ftp://github.com/x")]
    [InlineData("file:///C:/x.exe")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_refused(string? url)
    {
        Assert.False(UpdateUrls.IsAllowedDownload(url));
    }

    [Theory]
    [InlineData("RainWorldSaveManager-Setup.exe", true)]
    [InlineData("rainworldsavemanager-setup.exe", true)]
    [InlineData("RainWorldSaveManager.exe", false)]
    [InlineData("setup.exe", false)]
    [InlineData("Setup-installer.exe", false)]
    [InlineData("../evil-setup.exe", false)]
    [InlineData("a/b-setup.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_a_plainly_named_installer_counts_as_one(string? name, bool expected)
    {
        Assert.Equal(expected, UpdateUrls.IsInstallerAsset(name));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("...")]
    [InlineData("a b.exe")]
    [InlineData("a:b.exe")]
    [InlineData("a*.exe")]
    public void A_name_that_is_not_plainly_a_file_name_is_refused(string name)
    {
        Assert.False(UpdateUrls.IsSafeAssetName(name));
    }

    [Fact]
    public void The_user_agent_names_the_app_because_github_refuses_a_request_without_one()
    {
        var agent = UpdateUrls.UserAgent("1.1.0");

        Assert.StartsWith("RainWorldCompanion/1.1.0", agent);
        Assert.Contains("github.com/mkiera/RainWorldCompanion", agent);
    }
}
