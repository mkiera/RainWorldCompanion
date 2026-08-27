using RainWorldCompanion.Core.Updates;

namespace RainWorldCompanion.Tests;

/// <summary>
/// The cut matters in one direction more than the other. Keeping too much puts install
/// instructions in front of somebody already running the build, but cutting too much empties a
/// banner whose whole job is to say what changed.
/// </summary>
public class ReleaseNotesTests
{
    /// <summary>A body shaped the way the release workflow writes one.</summary>
    private static string Body(string section) =>
        section + "\r\n\r\n" + ReleaseNotes.EndMarker + "\r\n### Installation\r\nRun the setup.";

    [Fact]
    public void The_install_blurb_is_cut_off_at_the_marker()
    {
        var notes = ReleaseNotes.ForDisplay(Body("- Slots can be deleted."));

        Assert.Equal("- Slots can be deleted.", notes);
        Assert.DoesNotContain("Installation", notes);
    }

    [Fact]
    public void A_body_with_no_marker_is_kept_whole()
    {
        // Releases published before the changelog existed carry the blurb and nothing else.
        var notes = ReleaseNotes.ForDisplay("### Installation\nRun the setup.");

        Assert.Equal("### Installation\nRun the setup.", notes);
    }

    [Fact]
    public void Carriage_returns_do_not_survive()
    {
        // GitHub answers with CRLF, and WPF draws a stray CR as a box.
        var notes = ReleaseNotes.ForDisplay("- One.\r\n- Two.");

        Assert.Equal("- One.\n- Two.", notes);
        Assert.DoesNotContain("\r", notes);
    }

    [Fact]
    public void Several_lines_before_the_marker_are_all_kept()
    {
        var notes = ReleaseNotes.ForDisplay(Body("- One.\r\n- Two.\r\n- Three."));

        Assert.Equal("- One.\n- Two.\n- Three.", notes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \r\n  ")]
    public void Nothing_worth_showing_reads_as_blank(string? body)
    {
        Assert.Equal("", ReleaseNotes.ForDisplay(body));
    }

    [Fact]
    public void A_body_that_is_only_the_blurb_reads_as_blank()
    {
        Assert.Equal("", ReleaseNotes.ForDisplay(Body("")));
    }
}
