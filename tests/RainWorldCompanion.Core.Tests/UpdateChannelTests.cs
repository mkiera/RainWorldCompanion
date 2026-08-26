using RainWorldCompanion.Core.Updates;

namespace RainWorldCompanion.Tests;

/// <summary>
/// Stored as text so inserting a channel later cannot change what a saved settings file means,
/// and falls back to stable so a file naming a channel this build lacks still loads.
/// </summary>
public class UpdateChannelTests
{
    [Theory]
    [InlineData("stable", UpdateChannel.Stable)]
    [InlineData("prerelease", UpdateChannel.Prerelease)]
    [InlineData("alpha", UpdateChannel.Alpha)]
    [InlineData("Prerelease", UpdateChannel.Prerelease)]
    [InlineData(" ALPHA ", UpdateChannel.Alpha)]
    public void A_stored_channel_reads_back_as_itself(string text, UpdateChannel expected)
    {
        Assert.Equal(expected, UpdateChannels.Parse(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nightly")]
    [InlineData("a channel from a later version")]
    public void An_unrecognised_channel_reads_as_stable(string? text)
    {
        Assert.Equal(UpdateChannel.Stable, UpdateChannels.Parse(text));
    }

    [Theory]
    [InlineData(UpdateChannel.Stable)]
    [InlineData(UpdateChannel.Prerelease)]
    [InlineData(UpdateChannel.Alpha)]
    public void Every_channel_round_trips_through_storage(UpdateChannel channel)
    {
        Assert.Equal(channel, UpdateChannels.Parse(channel.ToStorageString()));
    }

    [Fact]
    public void Only_stable_and_prerelease_can_produce_an_offer_the_app_makes_on_its_own()
    {
        Assert.True(UpdateChannel.Stable.CanBeOfferedAutomatically());
        Assert.True(UpdateChannel.Prerelease.CanBeOfferedAutomatically());
        Assert.False(UpdateChannel.Alpha.CanBeOfferedAutomatically());
    }
}
