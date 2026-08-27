using RainWorldCompanion.Core.Updates;

namespace RainWorldCompanion.Core.Tests.Updates;

public class ReleaseActionTests
{
    private static SemVer V(string text)
    {
        Assert.True(SemVer.TryParse(text, out var version), $"{text} should parse");
        return version;
    }

    [Theory]
    [InlineData("1.1.0", "1.0.0", ReleaseAction.Update)]
    [InlineData("1.0.1", "1.0.0", ReleaseAction.Update)]
    [InlineData("1.0.0", "1.1.0", ReleaseAction.Downgrade)]
    [InlineData("1.0.0", "1.0.0", ReleaseAction.Reinstall)]
    public void Each_row_is_named_against_the_running_version(
        string candidate, string running, ReleaseAction expected)
    {
        Assert.Equal(expected, ReleaseActions.For(V(candidate), V(running)));
    }

    [Fact]
    public void A_prerelease_of_the_next_version_is_still_an_update()
    {
        Assert.Equal(ReleaseAction.Update, ReleaseActions.For(V("1.1.0-beta.1"), V("1.0.0")));
    }

    [Fact]
    public void A_prerelease_is_a_downgrade_from_the_release_it_preceded()
    {
        Assert.Equal(ReleaseAction.Downgrade, ReleaseActions.For(V("1.1.0-beta.1"), V("1.1.0")));
    }

    /// <summary>
    /// Build metadata takes no part in semver ordering, so the same version at a different commit
    /// is the version already installed, not an update to it.
    /// </summary>
    [Fact]
    public void The_same_version_from_another_commit_is_a_reinstall()
    {
        Assert.Equal(ReleaseAction.Reinstall, ReleaseActions.For(V("1.0.0+abc1234"), V("1.0.0+def5678")));
    }

    /// <summary>
    /// A copy that cannot say what version it is has nothing to be measured against, so no row
    /// claims a direction. UpdatePicker refuses to make an automatic offer in the same case.
    /// </summary>
    [Fact]
    public void Nothing_is_an_update_when_the_running_version_is_unknown()
    {
        Assert.Equal(ReleaseAction.Install, ReleaseActions.For(V("9.9.9"), null));
        Assert.Equal("Install", ReleaseActions.For(V("9.9.9"), null).Verb());
    }

    [Fact]
    public void Only_a_downgrade_is_asked_about_twice()
    {
        Assert.True(ReleaseAction.Downgrade.NeedsConfirmation());
        Assert.False(ReleaseAction.Update.NeedsConfirmation());
        Assert.False(ReleaseAction.Reinstall.NeedsConfirmation());
        Assert.False(ReleaseAction.Install.NeedsConfirmation());
    }

    [Fact]
    public void The_confirmation_names_the_version_being_gone_back_to()
    {
        Assert.Contains("1.0.0", ReleaseActions.ConfirmationText("1.0.0"));
    }

    [Fact]
    public void Every_channel_has_a_name_and_a_description()
    {
        Assert.Equal(3, UpdateChannels.All.Count);

        foreach (var channel in UpdateChannels.All)
        {
            Assert.NotEmpty(channel.Title());
            Assert.NotEmpty(channel.Description());
        }

        // Distinct, or the picker shows three rows nobody can tell apart.
        Assert.Equal(3, UpdateChannels.All.Select(c => c.Title()).Distinct().Count());
    }
}
