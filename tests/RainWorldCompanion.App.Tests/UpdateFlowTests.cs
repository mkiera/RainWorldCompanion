using RainWorldCompanion.Core.Settings;
using RainWorldCompanion.Core.Updates;
using RainWorldCompanion.Services;

namespace RainWorldCompanion.App.Tests;

/// <summary>
/// The decisions the updater makes between finding a release and ending the process.
///
/// The dangerous one is the last of those. An update closes the app, and closing it partway through
/// a restore leaves the live save folder half overwritten, so the guard that refuses is checked
/// twice and both checks are pinned here.
/// </summary>
public class UpdateFlowTests
{
    [Fact]
    public async Task A_newer_release_becomes_an_offer()
    {
        var world = new UpdateWorld();
        world.Source.Releases.Add(UpdateWorld.Release("v1.1.0"));
        var updates = world.Build(runningVersion: "1.0.0");

        await updates.CheckAsync(userAsked: false, CancellationToken.None);

        Assert.True(updates.HasOffer);
        Assert.Equal("1.1.0", updates.Offer!.VersionText);
        Assert.Equal("Version 1.1.0 is available.", updates.BannerText);
    }

    [Fact]
    public async Task Nothing_newer_produces_no_offer_and_says_nothing_unprompted()
    {
        // An automatic check that finds nothing must be silent. Reporting "you are up to date" at
        // an hourly beat is noise about something nobody asked.
        var world = new UpdateWorld();
        world.Source.Releases.Add(UpdateWorld.Release("v1.0.0"));
        var updates = world.Build(runningVersion: "1.0.0");

        await updates.CheckAsync(userAsked: false, CancellationToken.None);

        Assert.False(updates.HasOffer);
        Assert.False(updates.HasStatus);
    }

    [Fact]
    public async Task A_check_the_user_asked_for_says_so_when_there_is_nothing()
    {
        var world = new UpdateWorld();
        world.Source.Releases.Add(UpdateWorld.Release("v1.0.0"));
        var updates = world.Build(runningVersion: "1.0.0");

        await updates.CheckAsync(userAsked: true, CancellationToken.None);

        Assert.Equal("This is the newest version.", updates.StatusMessage);
        Assert.False(updates.IsProblem);
    }

    [Fact]
    public async Task A_dismissed_version_stops_being_offered_but_only_until_asked_for()
    {
        // Dismissal is per version and in memory. Persisting it is how somebody ends up stranded on
        // a broken build, because the release that fixes it is the one they silenced.
        var world = new UpdateWorld();
        world.Source.Releases.Add(UpdateWorld.Release("v1.1.0"));
        var updates = world.Build(runningVersion: "1.0.0");

        await updates.CheckAsync(userAsked: false, CancellationToken.None);
        updates.DismissOfferCommand.Execute(null);
        Assert.False(updates.HasOffer);

        await updates.CheckAsync(userAsked: false, CancellationToken.None);
        Assert.False(updates.HasOffer);

        await updates.CheckAsync(userAsked: true, CancellationToken.None);
        Assert.True(updates.HasOffer);

        // And nothing about the dismissal reached the settings file.
        Assert.Equal(new AppSettings().UpdateChannel, world.Saved.UpdateChannel);
    }

    [Fact]
    public async Task A_newer_release_than_the_dismissed_one_is_still_offered()
    {
        var world = new UpdateWorld();
        world.Source.Releases.Add(UpdateWorld.Release("v1.1.0"));
        var updates = world.Build(runningVersion: "1.0.0");

        await updates.CheckAsync(userAsked: false, CancellationToken.None);
        updates.DismissOfferCommand.Execute(null);

        world.Source.Releases.Add(UpdateWorld.Release("v1.2.0"));
        await updates.CheckAsync(userAsked: false, CancellationToken.None);

        Assert.True(updates.HasOffer);
        Assert.Equal("1.2.0", updates.Offer!.VersionText);
    }

    [Fact]
    public async Task A_failed_check_puts_the_cooldown_stamp_back()
    {
        // A check that never reached GitHub has not used its turn. Without this, one offline launch
        // costs the whole hour before the next attempt, and a machine that is always offline at
        // launch never checks at all.
        var world = new UpdateWorld();
        var updates = world.Build();
        updates.Adopt(new AppSettings { LastUpdateCheckUtc = null });

        world.Source.Throws = new UpdateCheckException("Could not reach GitHub to check for updates.");
        await updates.CheckAsync(userAsked: true, CancellationToken.None);

        Assert.Null(updates.LastCheckedUtc);
        Assert.True(updates.IsProblem);
        Assert.Equal("Could not reach GitHub to check for updates.", updates.StatusMessage);
    }

    [Fact]
    public async Task A_failed_automatic_check_stays_quiet()
    {
        // Nobody asked, so an unreachable network is not news. The banner would be reporting a
        // problem the user has no reason to care about and cannot act on.
        var world = new UpdateWorld();
        var updates = world.Build();
        world.Source.Throws = new UpdateCheckException("Could not reach GitHub to check for updates.");

        await updates.CheckAsync(userAsked: false, CancellationToken.None);

        Assert.False(updates.IsProblem);
        Assert.False(updates.HasStatus);
    }

    [Fact]
    public async Task A_successful_check_records_when_it_happened()
    {
        var world = new UpdateWorld();
        var updates = world.Build();

        await updates.CheckAsync(userAsked: true, CancellationToken.None);

        Assert.Equal(world.Now, updates.LastCheckedUtc);
        Assert.Equal(world.Now, world.Saved.LastUpdateCheckUtc);
    }

    [Fact]
    public void The_automatic_check_is_gated_on_the_setting_and_the_clock()
    {
        var world = new UpdateWorld();
        var updates = world.Build();

        updates.Adopt(new AppSettings { AutoCheckUpdates = true, LastUpdateCheckUtc = null });
        Assert.True(updates.IsAutomaticCheckDue());

        updates.Adopt(new AppSettings { AutoCheckUpdates = true, LastUpdateCheckUtc = world.Now.AddMinutes(-5) });
        Assert.False(updates.IsAutomaticCheckDue());

        updates.Adopt(new AppSettings { AutoCheckUpdates = false, LastUpdateCheckUtc = null });
        Assert.False(updates.IsAutomaticCheckDue());
    }

    [Fact]
    public async Task Turning_the_automatic_check_off_still_leaves_the_button_working()
    {
        // The bug FlipperClipper shipped and then fixed: fold the setting into the check itself and
        // the Check button reports "this is the newest build" without having looked.
        var world = new UpdateWorld();
        world.Source.Releases.Add(UpdateWorld.Release("v1.1.0"));
        var updates = world.Build(runningVersion: "1.0.0");
        updates.SetAutoCheck(false);

        Assert.False(updates.IsAutomaticCheckDue());

        await updates.CheckAsync(userAsked: true, CancellationToken.None);

        Assert.Equal(1, world.Source.Calls);
        Assert.True(updates.HasOffer);
        Assert.False(world.Saved.AutoCheckUpdates);
    }

    [Fact]
    public async Task An_update_downloads_and_hands_the_installer_over_then_closes_the_app()
    {
        var world = new UpdateWorld();
        world.Source.Releases.Add(UpdateWorld.Release("v1.1.0"));
        var updates = world.Build(runningVersion: "1.0.0");

        await updates.CheckAsync(userAsked: false, CancellationToken.None);
        await updates.InstallAsync(updates.Offer, CancellationToken.None);

        Assert.Equal(1, world.Downloader.Calls);
        Assert.Single(world.Launcher.Started);
        Assert.Equal(1, world.ShutdownRequests);
        Assert.Equal(100, updates.DownloadPercent);
    }

    [Fact]
    public async Task An_update_is_refused_before_the_download_while_a_save_is_being_written()
    {
        // Nothing is gained by fetching 43 MB first, and the message has to name what to do.
        var world = new UpdateWorld();
        world.Busy.Reason = "RainWorld Companion is in the middle of something that writes to your saves.";
        world.Source.Releases.Add(UpdateWorld.Release("v1.1.0"));
        var updates = world.Build(runningVersion: "1.0.0");

        await updates.CheckAsync(userAsked: false, CancellationToken.None);
        await updates.InstallAsync(updates.Offer, CancellationToken.None);

        Assert.Equal(0, world.Downloader.Calls);
        Assert.Empty(world.Launcher.Started);
        Assert.Equal(0, world.ShutdownRequests);
        Assert.True(updates.IsProblem);
    }

    [Fact]
    public async Task An_update_is_refused_again_when_a_save_starts_during_the_download()
    {
        // The second check is the one that matters. A download takes long enough for a backup to
        // have been started since the click, and it is the exit at the end, not the click at the
        // start, that would interrupt it.
        var world = new UpdateWorld();
        world.Source.Releases.Add(UpdateWorld.Release("v1.1.0"));
        world.Downloader.WhileDownloading = () =>
            world.Busy.Reason = "A backup was started while the update was downloading.";
        var updates = world.Build(runningVersion: "1.0.0");

        await updates.CheckAsync(userAsked: false, CancellationToken.None);
        await updates.InstallAsync(updates.Offer, CancellationToken.None);

        Assert.Equal(1, world.Downloader.Calls);
        Assert.Empty(world.Launcher.Started);
        Assert.Equal(0, world.ShutdownRequests);
        Assert.True(updates.IsProblem);
    }

    [Fact]
    public async Task An_installer_that_will_not_start_leaves_the_app_running()
    {
        // The property every refusal shares: somebody left with neither the old version nor the new
        // one has no way back, so the app stays up and says what happened.
        var world = new UpdateWorld();
        world.Source.Releases.Add(UpdateWorld.Release("v1.1.0"));
        world.Launcher.Outcome = new LaunchOutcome(false, "The installer has stopped to ask something.");
        var updates = world.Build(runningVersion: "1.0.0");

        await updates.CheckAsync(userAsked: false, CancellationToken.None);
        await updates.InstallAsync(updates.Offer, CancellationToken.None);

        Assert.Equal(0, world.ShutdownRequests);
        Assert.True(updates.IsProblem);
        Assert.Equal("The installer has stopped to ask something.", updates.StatusMessage);
    }

    [Fact]
    public async Task A_failed_download_leaves_the_app_running_and_says_why()
    {
        var world = new UpdateWorld();
        world.Source.Releases.Add(UpdateWorld.Release("v1.1.0"));
        world.Downloader.Throws = new UpdateCheckException("The download stopped early.");
        var updates = world.Build(runningVersion: "1.0.0");

        await updates.CheckAsync(userAsked: false, CancellationToken.None);
        await updates.InstallAsync(updates.Offer, CancellationToken.None);

        Assert.Empty(world.Launcher.Started);
        Assert.Equal(0, world.ShutdownRequests);
        Assert.Equal("The download stopped early.", updates.StatusMessage);
        Assert.False(updates.IsDownloading);
    }

    [Fact]
    public async Task Switching_channel_saves_it_and_looks_again_straight_away()
    {
        // The offer on screen came from the old channel, so it goes. The cooldown does not apply:
        // switching channel is somebody asking, and the hour is about checks nobody asked for.
        var world = new UpdateWorld();
        world.Source.Releases.Add(UpdateWorld.Release("v1.1.0-beta.1", prerelease: true));
        var updates = world.Build(runningVersion: "1.0.0");

        await updates.CheckAsync(userAsked: false, CancellationToken.None);
        Assert.False(updates.HasOffer);

        await updates.SetChannelAsync(UpdateChannel.Prerelease, CancellationToken.None);

        Assert.Equal("prerelease", world.Saved.UpdateChannel);
        Assert.True(updates.HasOffer);
        Assert.Equal("1.1.0-beta.1", updates.Offer!.VersionText);
        Assert.Equal("Version 1.1.0-beta.1 is available as a pre-release.", updates.BannerText);
    }

    [Fact]
    public async Task A_problem_with_an_offer_showing_does_not_get_a_banner_of_its_own()
    {
        // The message goes inside the offer's banner. Two banners would print the same sentence
        // twice, once in each.
        var world = new UpdateWorld();
        world.Source.Releases.Add(UpdateWorld.Release("v1.1.0"));
        world.Busy.Reason = "Busy.";
        var updates = world.Build(runningVersion: "1.0.0");

        await updates.CheckAsync(userAsked: false, CancellationToken.None);
        await updates.InstallAsync(updates.Offer, CancellationToken.None);

        Assert.True(updates.IsProblem);
        Assert.True(updates.HasOffer);
        Assert.False(updates.HasStandaloneProblem);
    }

    [Fact]
    public void The_running_version_line_names_the_commit_when_there_is_one()
    {
        var world = new UpdateWorld();

        Assert.Equal("Running 1.0.0", world.Build().RunningVersionText);
        Assert.Equal(
            "Running 1.0.0, commit a1b2c3d",
            world.Build(sha: "a1b2c3d4e5f60718").RunningVersionText);
    }
}
