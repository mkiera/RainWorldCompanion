using RainWorldCompanion.Core.Settings;
using RainWorldCompanion.Core.Updates;
using RainWorldCompanion.Services;

namespace RainWorldCompanion.App.Tests;

/// <summary>
/// Two ways of being wrong, both quiet. Showing it when nothing changed means a banner that
/// will not go away, and consuming it without showing it means the one launch that was owed
/// the notes is the launch that swallows them. The version recorded in settings is what
/// separates the two, so every case here is about when that gets written.
/// </summary>
public class WhatsNewTests
{
    private const string Section = "- Slots can be deleted.";

    private static AppSettings Seen(string version) =>
        new() { LastSeenChangelogVersion = version };

    [Fact]
    public async Task Running_a_new_version_shows_what_it_brought()
    {
        var world = new UpdateWorld();
        world.Source.Releases.Add(UpdateWorld.Release("v1.1.0", notes: UpdateWorld.Body(Section)));
        var updates = world.Build(runningVersion: "1.1.0");
        updates.Adopt(Seen("1.0.0"));

        await updates.CheckForWhatsNewAsync(CancellationToken.None);

        Assert.True(updates.HasWhatsNew);
        Assert.Equal(Section, updates.WhatsNewNotes);
        Assert.Equal("What's new in 1.1.0", updates.WhatsNewTitle);
    }

    [Fact]
    public async Task The_install_blurb_does_not_reach_the_banner()
    {
        // Whoever is reading this already has the app installed.
        var world = new UpdateWorld();
        world.Source.Releases.Add(UpdateWorld.Release("v1.1.0", notes: UpdateWorld.Body(Section)));
        var updates = world.Build(runningVersion: "1.1.0");
        updates.Adopt(Seen("1.0.0"));

        await updates.CheckForWhatsNewAsync(CancellationToken.None);

        Assert.DoesNotContain("Installation", updates.WhatsNewNotes);
    }

    [Fact]
    public async Task A_first_run_records_the_version_and_says_nothing()
    {
        // Nothing recorded means a fresh install, and somebody who has never run this app is not
        // owed a list of what changed since a version they never had.
        var world = new UpdateWorld();
        world.Source.Releases.Add(UpdateWorld.Release("v1.1.0", notes: UpdateWorld.Body(Section)));
        var updates = world.Build(runningVersion: "1.1.0");
        updates.Adopt(Seen(""));

        await updates.CheckForWhatsNewAsync(CancellationToken.None);

        Assert.False(updates.HasWhatsNew);
        Assert.Equal("1.1.0", world.Saved.LastSeenChangelogVersion);
    }

    [Fact]
    public async Task The_same_version_shows_nothing_and_asks_GitHub_nothing()
    {
        // The ordinary launch. This runs every time the app starts, so it has to cost no request.
        var world = new UpdateWorld();
        world.Source.Releases.Add(UpdateWorld.Release("v1.1.0", notes: UpdateWorld.Body(Section)));
        var updates = world.Build(runningVersion: "1.1.0");
        updates.Adopt(Seen("1.1.0"));

        await updates.CheckForWhatsNewAsync(CancellationToken.None);

        Assert.False(updates.HasWhatsNew);
        Assert.Equal(0, world.Source.Calls);
    }

    [Fact]
    public async Task Putting_it_away_records_the_version_so_it_stays_away()
    {
        var world = new UpdateWorld();
        world.Source.Releases.Add(UpdateWorld.Release("v1.1.0", notes: UpdateWorld.Body(Section)));
        var updates = world.Build(runningVersion: "1.1.0");
        updates.Adopt(Seen("1.0.0"));
        await updates.CheckForWhatsNewAsync(CancellationToken.None);

        updates.DismissWhatsNewCommand.Execute(null);

        Assert.False(updates.HasWhatsNew);
        Assert.Equal("1.1.0", world.Saved.LastSeenChangelogVersion);

        var callsBefore = world.Source.Calls;
        await updates.CheckForWhatsNewAsync(CancellationToken.None);
        Assert.False(updates.HasWhatsNew);
        Assert.Equal(callsBefore, world.Source.Calls);
    }

    [Fact]
    public async Task A_release_with_no_notes_is_settled_rather_than_asked_about_again()
    {
        var world = new UpdateWorld();
        world.Source.Releases.Add(UpdateWorld.Release("v1.1.0"));
        var updates = world.Build(runningVersion: "1.1.0");
        updates.Adopt(Seen("1.0.0"));

        await updates.CheckForWhatsNewAsync(CancellationToken.None);

        Assert.False(updates.HasWhatsNew);
        Assert.Equal("1.1.0", world.Saved.LastSeenChangelogVersion);
    }

    [Fact]
    public async Task A_branch_build_with_no_release_of_its_own_is_settled()
    {
        // An alpha build is named for a CI run, so no published release will ever match it.
        var world = new UpdateWorld();
        world.Source.Releases.Add(UpdateWorld.Release("v1.1.0", notes: UpdateWorld.Body(Section)));
        var updates = world.Build(runningVersion: "1.2.0-alpha.42");
        updates.Adopt(Seen("1.1.0"));

        await updates.CheckForWhatsNewAsync(CancellationToken.None);

        Assert.False(updates.HasWhatsNew);
        Assert.Equal("1.2.0-alpha.42", world.Saved.LastSeenChangelogVersion);
    }

    [Fact]
    public async Task A_pre_release_finds_its_own_notes()
    {
        // The stable list would not hold it, so the lookup has to run against the wide one.
        var world = new UpdateWorld();
        world.Source.Releases.Add(
            UpdateWorld.Release("v1.2.0-beta.1", prerelease: true, notes: UpdateWorld.Body(Section)));
        var updates = world.Build(runningVersion: "1.2.0-beta.1");
        updates.Adopt(Seen("1.1.0"));

        await updates.CheckForWhatsNewAsync(CancellationToken.None);

        Assert.True(updates.HasWhatsNew);
        Assert.Equal(Section, updates.WhatsNewNotes);
    }

    [Fact]
    public async Task Being_offline_leaves_the_setting_alone_so_the_next_launch_tries_again()
    {
        var world = new UpdateWorld();
        world.Source.Releases.Add(UpdateWorld.Release("v1.1.0", notes: UpdateWorld.Body(Section)));
        world.Source.Throws = new UpdateCheckException("Could not reach GitHub to check for updates.");
        var updates = world.Build(runningVersion: "1.1.0");
        updates.Adopt(Seen("1.0.0"));

        await updates.CheckForWhatsNewAsync(CancellationToken.None);

        Assert.False(updates.HasWhatsNew);
        Assert.Equal("", world.Saved.LastSeenChangelogVersion);

        // Nothing was said about it either. Nobody asked for this check.
        Assert.False(updates.HasStatus);
        Assert.False(updates.IsProblem);
    }

    [Fact]
    public async Task A_failure_here_does_not_disturb_the_update_offer()
    {
        // The two banners are independent, and a what's-new that could not load must not leave the
        // offer looking like it failed.
        var world = new UpdateWorld();
        world.Source.Releases.Add(UpdateWorld.Release("v1.2.0"));
        var updates = world.Build(runningVersion: "1.1.0");
        updates.Adopt(Seen("1.0.0"));
        await updates.CheckAsync(userAsked: false, CancellationToken.None);

        world.Source.Throws = new UpdateCheckException("Could not reach GitHub to check for updates.");
        await updates.CheckForWhatsNewAsync(CancellationToken.None);

        Assert.True(updates.HasOffer);
        Assert.Equal("1.2.0", updates.Offer!.VersionText);
    }

    /// <summary>
    /// The banner shows the count and nothing else, so the notes themselves cannot push the app
    /// off the screen. A release with twenty entries in it is what made that matter.
    /// </summary>
    [Theory]
    [InlineData("- One thing.", "One change.")]
    [InlineData("- One thing.\r\n- Another.", "2 changes.")]
    [InlineData("* One thing.\r\n* Another.\r\n* A third.", "3 changes.")]
    public async Task The_banner_says_how_many_changes_there_were(string section, string expected)
    {
        var world = new UpdateWorld();
        world.Source.Releases.Add(UpdateWorld.Release("v1.1.0", notes: UpdateWorld.Body(section)));
        var updates = world.Build(runningVersion: "1.1.0");
        updates.Adopt(Seen("1.0.0"));

        await updates.CheckForWhatsNewAsync(CancellationToken.None);

        Assert.Equal(expected, updates.WhatsNewSummary);
    }

    [Fact]
    public async Task An_entry_that_wraps_is_still_one_change()
    {
        var world = new UpdateWorld();
        var section = "- One thing, said over\r\n  two lines.\r\n- Another.";
        world.Source.Releases.Add(UpdateWorld.Release("v1.1.0", notes: UpdateWorld.Body(section)));
        var updates = world.Build(runningVersion: "1.1.0");
        updates.Adopt(Seen("1.0.0"));

        await updates.CheckForWhatsNewAsync(CancellationToken.None);

        Assert.Equal("2 changes.", updates.WhatsNewSummary);
    }

    [Fact]
    public async Task A_body_that_is_not_a_list_still_asks_to_be_read()
    {
        var world = new UpdateWorld();
        world.Source.Releases.Add(
            UpdateWorld.Release("v1.1.0", notes: UpdateWorld.Body("Slots can be deleted now.")));
        var updates = world.Build(runningVersion: "1.1.0");
        updates.Adopt(Seen("1.0.0"));

        await updates.CheckForWhatsNewAsync(CancellationToken.None);

        Assert.True(updates.HasWhatsNew);
        Assert.Equal("Read what this version changed.", updates.WhatsNewSummary);
    }
}
