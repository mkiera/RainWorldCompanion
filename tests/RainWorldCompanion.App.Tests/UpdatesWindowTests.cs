using RainWorldCompanion.Core.Updates;
using RainWorldCompanion.Services;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.App.Tests;

/// <summary>Builds no Window and no Dispatcher, the same rule the rest of this suite keeps.</summary>
public class UpdatesWindowTests
{
    private static UpdatesViewModel Window(UpdateWorld world, UpdateViewModel updates) =>
        new(updates, () => world.Now);

    private static UpdateWorld WorldWithReleases()
    {
        var world = new UpdateWorld();
        world.Source.Releases.Add(UpdateWorld.Release("v0.9.0"));
        world.Source.Releases.Add(UpdateWorld.Release("v1.0.0"));
        world.Source.Releases.Add(UpdateWorld.Release("v1.1.0-beta.1", prerelease: true));
        world.Source.Releases.Add(UpdateWorld.Release("v1.1.0"));
        return world;
    }

    [Fact]
    public async Task Stable_hides_the_prereleases()
    {
        var world = WorldWithReleases();
        var window = Window(world, world.Build("1.0.0"));

        await window.InitializeAsync(CancellationToken.None);

        Assert.Equal(["1.1.0", "1.0.0", "0.9.0"], window.Releases.Select(r => r.VersionText));
    }

    [Fact]
    public async Task Prerelease_shows_them_alongside_the_finished_ones()
    {
        var world = WorldWithReleases();
        var updates = world.Build("1.0.0");
        updates.Channel = UpdateChannel.Prerelease;
        var window = Window(world, updates);

        await window.InitializeAsync(CancellationToken.None);

        Assert.Equal(
            ["1.1.0", "1.1.0-beta.1", "1.0.0", "0.9.0"],
            window.Releases.Select(r => r.VersionText));
    }

    [Fact]
    public async Task Every_row_says_what_its_button_would_do()
    {
        var world = WorldWithReleases();
        var window = Window(world, world.Build("1.0.0"));

        await window.InitializeAsync(CancellationToken.None);

        Assert.Equal("Update", window.Releases.Single(r => r.VersionText == "1.1.0").ActionVerb);
        Assert.Equal("Reinstall", window.Releases.Single(r => r.VersionText == "1.0.0").ActionVerb);
        Assert.Equal("Downgrade", window.Releases.Single(r => r.VersionText == "0.9.0").ActionVerb);
    }

    [Fact]
    public async Task The_running_version_is_the_row_marked_as_such()
    {
        var world = WorldWithReleases();
        var window = Window(world, world.Build("1.0.0"));

        await window.InitializeAsync(CancellationToken.None);

        Assert.Equal("1.0.0", window.Releases.Single(r => r.IsRunning).VersionText);
    }

    /// <summary>
    /// Going back to an older build is easy to choose by accident in a list where every other
    /// row is an update.
    /// </summary>
    [Fact]
    public async Task A_downgrade_takes_two_presses()
    {
        var world = WorldWithReleases();
        var window = Window(world, world.Build("1.0.0"));
        await window.InitializeAsync(CancellationToken.None);

        var older = window.Releases.Single(r => r.VersionText == "0.9.0");

        await window.InstallReleaseCommand.ExecuteAsync(older);
        Assert.True(older.IsArmed);
        Assert.Contains("0.9.0", older.ConfirmationText);
        Assert.Empty(world.Launcher.Started);

        await window.InstallReleaseCommand.ExecuteAsync(older);
        Assert.Single(world.Launcher.Started);
    }

    [Fact]
    public async Task An_update_runs_on_the_first_press()
    {
        var world = WorldWithReleases();
        var window = Window(world, world.Build("1.0.0"));
        await window.InitializeAsync(CancellationToken.None);

        await window.InstallReleaseCommand.ExecuteAsync(
            window.Releases.Single(r => r.VersionText == "1.1.0"));

        Assert.Single(world.Launcher.Started);
        Assert.Equal(1, world.ShutdownRequests);
    }

    /// <summary>
    /// Arming one row and then pressing another must not install the armed one, and must not leave
    /// it armed behind the row that was actually pressed.
    /// </summary>
    [Fact]
    public async Task Pressing_a_different_row_disarms_the_first()
    {
        var world = WorldWithReleases();
        var window = Window(world, world.Build("1.0.0"));
        await window.InitializeAsync(CancellationToken.None);

        var older = window.Releases.Single(r => r.VersionText == "0.9.0");
        var newer = window.Releases.Single(r => r.VersionText == "1.1.0");

        await window.InstallReleaseCommand.ExecuteAsync(older);
        await window.InstallReleaseCommand.ExecuteAsync(newer);

        Assert.False(older.IsArmed);
        Assert.Equal(["C:\\updates\\RainWorldCompanion-Setup.exe"], world.Launcher.Started);
    }

    [Fact]
    public async Task A_save_being_written_stops_an_install_from_the_window()
    {
        var world = WorldWithReleases();
        world.Busy.Reason = "A backup is running.";
        var window = Window(world, world.Build("1.0.0"));
        await window.InitializeAsync(CancellationToken.None);

        await window.InstallReleaseCommand.ExecuteAsync(
            window.Releases.Single(r => r.VersionText == "1.1.0"));

        Assert.Empty(world.Launcher.Started);
        Assert.Equal(0, world.ShutdownRequests);
        Assert.Equal("A backup is running.", window.Updates.StatusMessage);
    }

    [Fact]
    public async Task Switching_channel_saves_it_and_relists()
    {
        var world = WorldWithReleases();
        var window = Window(world, world.Build("1.0.0"));
        await window.InitializeAsync(CancellationToken.None);

        await window.SelectChannelCommand.ExecuteAsync(
            window.Channels.Single(c => c.Channel == UpdateChannel.Prerelease));

        Assert.Equal("prerelease", world.Saved.UpdateChannel);
        Assert.Contains(window.Releases, r => r.VersionText == "1.1.0-beta.1");
        Assert.True(window.Channels.Single(c => c.Channel == UpdateChannel.Prerelease).IsSelected);
        Assert.False(window.Channels.Single(c => c.Channel == UpdateChannel.Stable).IsSelected);
    }

    [Fact]
    public async Task Moving_between_release_channels_costs_no_extra_request()
    {
        var world = WorldWithReleases();
        var window = Window(world, world.Build("1.0.0"));
        await window.InitializeAsync(CancellationToken.None);

        var afterFirst = world.Source.Calls;

        await window.SelectChannelCommand.ExecuteAsync(
            window.Channels.Single(c => c.Channel == UpdateChannel.Prerelease));

        // SetChannelAsync runs its own check, which is one request. The list itself is reused.
        Assert.True(world.Source.Calls - afterFirst <= 1);

        var afterSwitch = world.Source.Calls;
        await window.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(afterSwitch + 1, world.Source.Calls);
    }

    [Fact]
    public async Task A_stale_list_is_fetched_again()
    {
        var world = WorldWithReleases();
        var window = Window(world, world.Build("1.0.0"));
        await window.InitializeAsync(CancellationToken.None);
        var afterFirst = world.Source.Calls;

        world.Now = world.Now.Add(UpdatesViewModel.ListCacheLife).AddMinutes(1);
        await window.SelectChannelCommand.ExecuteAsync(
            window.Channels.Single(c => c.Channel == UpdateChannel.Prerelease));

        Assert.True(world.Source.Calls > afterFirst);
    }

    [Fact]
    public async Task A_failure_reaching_github_is_reported_rather_than_thrown()
    {
        var world = new UpdateWorld();
        world.Source.Throws = new UpdateCheckException("Could not reach GitHub to check for updates.");
        var window = Window(world, world.Build("1.0.0"));

        await window.InitializeAsync(CancellationToken.None);

        Assert.True(window.IsProblem);
        Assert.Equal("Could not reach GitHub to check for updates.", window.Message);
        Assert.False(window.IsLoading);
    }

    [Fact]
    public async Task An_empty_channel_says_so()
    {
        var world = new UpdateWorld();
        var window = Window(world, world.Build("1.0.0"));

        await window.InitializeAsync(CancellationToken.None);

        Assert.True(window.IsEmpty);
        Assert.False(window.IsProblem);
        Assert.NotEmpty(window.Message);
    }

    private static WorkflowRun Run(long id, string branch, int number, string sha = "abc1234def") =>
        new(id, "Build Test", branch, sha, number, "success", DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Branch_builds_are_listed_on_the_alpha_channel()
    {
        var world = new UpdateWorld();
        world.Source.Runs.Add(Run(11, "feature/updater", 47));
        world.Source.Runs.Add(Run(12, "bugfix/uninstall", 48));

        var updates = world.Build("1.0.0");
        updates.Channel = UpdateChannel.Alpha;
        var window = Window(world, updates);

        await window.InitializeAsync(CancellationToken.None);

        Assert.True(window.IsAlpha);
        Assert.Equal(
            ["bugfix/uninstall #48", "feature/updater #47"],
            window.BranchBuilds.Select(b => b.Label));
        Assert.Empty(window.Releases);
    }

    [Fact]
    public async Task Installing_a_branch_build_asks_for_that_run()
    {
        var world = new UpdateWorld();
        world.Source.Runs.Add(Run(99, "feature/updater", 47));

        var updates = world.Build("1.0.0");
        updates.Channel = UpdateChannel.Alpha;
        var window = Window(world, updates);
        await window.InitializeAsync(CancellationToken.None);

        await window.InstallBranchCommand.ExecuteAsync(window.BranchBuilds.Single());

        Assert.Equal([99L], world.Downloader.BranchRuns);
        Assert.Single(world.Launcher.Started);
    }

    [Fact]
    public async Task A_save_being_written_stops_a_branch_install_too()
    {
        var world = new UpdateWorld();
        world.Source.Runs.Add(Run(99, "feature/updater", 47));
        world.Busy.Reason = "A restore is running.";

        var updates = world.Build("1.0.0");
        updates.Channel = UpdateChannel.Alpha;
        var window = Window(world, updates);
        await window.InitializeAsync(CancellationToken.None);

        await window.InstallBranchCommand.ExecuteAsync(window.BranchBuilds.Single());

        Assert.Empty(world.Downloader.BranchRuns);
        Assert.Empty(world.Launcher.Started);
    }

    /// <summary>
    /// The picker is empty for alpha by design, so a check on that channel must not report that
    /// the running copy is the newest thing there is.
    /// </summary>
    [Fact]
    public async Task Checking_on_alpha_claims_nothing_about_being_up_to_date()
    {
        var world = WorldWithReleases();
        var updates = world.Build("1.0.0");

        await updates.SetChannelAsync(UpdateChannel.Alpha, CancellationToken.None);

        Assert.Null(updates.Offer);
        Assert.Equal("", updates.StatusMessage);
    }

    [Fact]
    public async Task Turning_automatic_checks_off_saves_it()
    {
        var world = WorldWithReleases();
        var window = Window(world, world.Build("1.0.0"));
        await window.InitializeAsync(CancellationToken.None);

        window.AutoCheck = false;

        Assert.False(world.Saved.AutoCheckUpdates);
        Assert.False(window.Updates.AutoCheck);
    }
}
