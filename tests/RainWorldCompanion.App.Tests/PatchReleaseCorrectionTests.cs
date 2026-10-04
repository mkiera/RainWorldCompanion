using RainWorldCompanion.Core.Updates;
using RainWorldCompanion.Services;
using RainWorldCompanion.ViewModels;

namespace RainWorldCompanion.App.Tests;

public class PatchReleaseCorrectionTests
{
    private static UpdateWorld Correction()
    {
        var world = new UpdateWorld();
        world.Source.Releases.Add(UpdateWorld.Release("v1.5.0") with { IsDraft = true });
        world.Source.Releases.Add(UpdateWorld.Release("v1.4.0"));
        world.Source.Releases.Add(UpdateWorld.Release("v1.4.1"));
        return world;
    }

    [Theory]
    [InlineData(UpdateChannel.Stable)]
    [InlineData(UpdateChannel.Prerelease)]
    public async Task A_withdrawn_minor_release_does_not_hide_the_corrected_patch(UpdateChannel channel)
    {
        var world = Correction();
        var updates = world.Build("1.4.0");
        updates.Channel = channel;

        await updates.CheckAsync(userAsked: true, CancellationToken.None);

        Assert.Equal("1.4.1", updates.Offer!.VersionText);
    }

    [Theory]
    [InlineData(UpdateChannel.Stable)]
    [InlineData(UpdateChannel.Prerelease)]
    public async Task The_shipped_minor_release_can_confirm_the_corrected_patch(UpdateChannel channel)
    {
        var world = Correction();
        var updates = world.Build("1.5.0");
        updates.Channel = channel;
        await updates.CheckAsync(userAsked: true, CancellationToken.None);
        Assert.Null(updates.Offer);
        world.Saved.UpdateChannel = channel.ToStorageString();
        var window = new UpdatesViewModel(updates, () => world.Now);

        await window.InitializeAsync(CancellationToken.None);

        Assert.Equal(["1.4.1", "1.4.0"], window.Releases.Select(row => row.VersionText));
        var patch = window.Releases[0];
        Assert.Equal("Downgrade", patch.ActionVerb);
        await window.InstallReleaseCommand.ExecuteAsync(patch);
        Assert.True(patch.IsArmed);
        Assert.Equal(0, world.Downloader.Calls);
        Assert.Equal(0, world.ShutdownRequests);

        await window.InstallReleaseCommand.ExecuteAsync(patch);

        Assert.Equal(1, world.Downloader.Calls);
        Assert.Single(world.Launcher.Started);
        Assert.Equal(1, world.ShutdownRequests);
        Assert.Equal(channel.ToStorageString(), world.Saved.UpdateChannel);
    }

    [Fact]
    public async Task A_corrected_patch_does_not_offer_the_withdrawn_minor_again()
    {
        var world = Correction();
        var updates = world.Build("1.4.1");
        updates.Channel = UpdateChannel.Prerelease;

        await updates.CheckAsync(userAsked: true, CancellationToken.None);

        Assert.Null(updates.Offer);
    }

    [Fact]
    public async Task Marking_the_minor_as_prerelease_still_offers_it_on_that_channel()
    {
        var world = new UpdateWorld();
        world.Source.Releases.Add(UpdateWorld.Release("v1.5.0", prerelease: true));
        world.Source.Releases.Add(UpdateWorld.Release("v1.4.1"));
        var updates = world.Build("1.4.1");
        updates.Channel = UpdateChannel.Prerelease;

        await updates.CheckAsync(userAsked: true, CancellationToken.None);

        Assert.Equal("1.5.0", updates.Offer!.VersionText);
    }

    [Fact]
    public async Task A_failed_correction_download_keeps_the_minor_running()
    {
        var world = Correction();
        world.Downloader.Throws = new UpdateCheckException("The download stopped early.");
        var window = new UpdatesViewModel(world.Build("1.5.0"), () => world.Now);
        await window.InitializeAsync(CancellationToken.None);
        var patch = window.Releases[0];

        await window.InstallReleaseCommand.ExecuteAsync(patch);
        await window.InstallReleaseCommand.ExecuteAsync(patch);

        Assert.Empty(world.Launcher.Started);
        Assert.Equal(0, world.ShutdownRequests);
        Assert.Equal("The download stopped early.", window.Updates.StatusMessage);
    }

    [Fact]
    public void Build_metadata_does_not_make_the_patch_newer_than_the_minor()
    {
        Assert.True(SemVer.TryParse("1.4.1+correction", out var patch));
        Assert.True(SemVer.TryParse("1.5.0", out var minor));

        Assert.True(patch < minor);
    }
}
