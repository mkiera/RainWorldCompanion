using RainWorldCompanion.Core.Updates;

namespace RainWorldCompanion.Tests;

/// <summary>
/// Reading the installer's own log is how the app decides whether it is safe to close itself.
/// Getting it wrong means exiting into a dialog the user cannot connect to anything, leaving them
/// with a question on screen and no application left to have asked it.
/// </summary>
public class SetupLogTests
{
    private const string Stamp = "2026-08-25 14:23:01.123";

    [Fact]
    public void A_message_box_nobody_answered_reads_as_a_pending_dialog()
    {
        // Inno writes the box as it puts it on screen and writes the answer when one arrives, so
        // an unanswered box is the last one with nothing after it. The file stops growing while
        // the box is up, which is what makes this readable from another process at all.
        var lines = new[]
        {
            Stamp + "   Starting the installation process.",
            Stamp + "   Message box (Yes/No):",
            "Setup could not replace a file that is in use. Try again?",
        };

        var pending = SetupLog.PendingDialog(SetupLog.ReadMessages(lines));

        Assert.Equal("Setup could not replace a file that is in use. Try again?", pending);
    }

    [Fact]
    public void An_answered_message_box_is_not_pending()
    {
        var lines = new[]
        {
            Stamp + "   Message box (Yes/No):",
            "Try again?",
            Stamp + "   User chose Yes.",
            Stamp + "   Copying files.",
        };

        Assert.Null(SetupLog.PendingDialog(SetupLog.ReadMessages(lines)));
    }

    [Fact]
    public void A_second_box_after_an_answered_one_is_pending_again()
    {
        var lines = new[]
        {
            Stamp + "   Message box (Yes/No):",
            "First question?",
            Stamp + "   User chose Yes.",
            Stamp + "   Message box (OK):",
            "Second question?",
        };

        Assert.Equal("Second question?", SetupLog.PendingDialog(SetupLog.ReadMessages(lines)));
    }

    [Fact]
    public void A_log_with_no_boxes_in_it_is_not_pending()
    {
        var lines = new[]
        {
            Stamp + "   Setup version: Inno Setup version 6.5.4",
            Stamp + "   Starting the installation process.",
        };

        Assert.Null(SetupLog.PendingDialog(SetupLog.ReadMessages(lines)));
    }

    [Fact]
    public void Continuation_lines_fold_into_the_message_above_them()
    {
        // The text inside a box is written without a timestamp, across as many lines as it takes.
        // Read as messages of their own, each line would look like a separate log entry and the
        // question would be split across several of them.
        var lines = new[]
        {
            Stamp + "   Message box (Yes/No):",
            "The file is in use.",
            "Close the program and try again?",
        };

        var messages = SetupLog.ReadMessages(lines);

        Assert.Single(messages);
        Assert.Equal(
            "The file is in use. Close the program and try again?",
            SetupLog.PendingDialog(messages));
    }

    [Fact]
    public void A_byte_order_mark_on_the_first_line_does_not_hide_its_timestamp()
    {
        // The log is UTF-8 with a mark. Left in place it sits in front of the timestamp, where it
        // would stop the first line looking like one and turn it into a continuation of nothing.
        var lines = new[] { "﻿" + Stamp + "   Log opened." };

        Assert.Equal(["Log opened."], SetupLog.ReadMessages(lines));
    }

    [Fact]
    public void An_empty_or_missing_log_reads_as_nothing_rather_than_failing()
    {
        // The app reads this 1.5 seconds after starting Setup, which is often before Setup has
        // written a word. Nothing to say is the normal case, not a fault.
        Assert.Empty(SetupLog.ReadMessages(null));
        Assert.Empty(SetupLog.ReadMessages([]));
        Assert.Empty(SetupLog.ReadMessages(["", "   ", "\t"]));
        Assert.Null(SetupLog.PendingDialog(null));
        Assert.Null(SetupLog.PendingDialog([]));
    }

    /// <summary>
    /// The opening lines of a log this project's own installer actually wrote, copied out of a run
    /// of Inno Setup 6.7.3 with the switches the updater passes. The shape is what everything above
    /// assumes, so it is worth holding one real sample against it: a byte order mark on the first
    /// line, a timestamp and three spaces, and CRLF endings.
    /// </summary>
    [Fact]
    public void A_log_this_installer_really_wrote_reads_as_ordinary_progress()
    {
        string[] lines =
        [
            "﻿2026-08-25 09:07:52.244   Log opened. (Time zone: UTC-06:00)",
            "2026-08-25 09:07:52.244   Setup version: Inno Setup version 6.7.3",
            "2026-08-25 09:07:52.244   Setup command line: /SL5=\"$F50202\" /SILENT /CLOSEAPPLICATIONS /NORESTARTAPPLICATIONS /LOG=update.log",
            "2026-08-25 09:07:52.244   Windows version: 10.0.26100",
            "2026-08-25 09:07:52.710   Starting the installation process.",
            "2026-08-25 09:07:57.480   Type: Exec",
            "2026-08-25 09:07:57.533   Need to restart Windows? No",
            "2026-08-25 09:07:57.596   Log closed.",
        ];

        var messages = SetupLog.ReadMessages(lines);

        Assert.Equal(lines.Length, messages.Count);
        Assert.Equal("Log opened. (Time zone: UTC-06:00)", messages[0]);
        Assert.Equal("Starting the installation process.", messages[4]);
        // Nothing was asked, so the app is clear to close and let Setup replace its files.
        Assert.Null(SetupLog.PendingDialog(messages));
    }

    [Fact]
    public void Every_documented_exit_code_has_a_sentence_and_only_zero_is_success()
    {
        for (var code = 0; code <= 8; code++)
        {
            Assert.False(string.IsNullOrWhiteSpace(InstallerExitCodes.Describe(code)));
            Assert.Equal(code == 0, InstallerExitCodes.IsSuccess(code));
        }

        // An undocumented code still has to produce something the user can read, because the
        // alternative is showing them a bare number.
        Assert.False(string.IsNullOrWhiteSpace(InstallerExitCodes.Describe(9999)));
        Assert.False(InstallerExitCodes.IsSuccess(-1));
    }
}

/// <summary>
/// How often the app is allowed to ask GitHub on its own. Sixty unauthenticated requests an hour
/// are shared with everything else on the machine, and this app has no account to raise that with.
/// </summary>
public class UpdateCooldownTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_check_that_has_never_happened_is_due()
    {
        Assert.True(UpdateCooldown.IsDue(null, Now));
        Assert.Equal("Not checked yet", UpdateCooldown.Describe(null, Now));
    }

    [Fact]
    public void A_check_inside_the_window_is_not_due_and_one_past_it_is()
    {
        Assert.False(UpdateCooldown.IsDue(Now.AddMinutes(-59), Now));
        Assert.True(UpdateCooldown.IsDue(Now.AddMinutes(-61), Now));
    }

    [Fact]
    public void A_cooldown_stamp_in_the_future_reads_as_due_rather_than_blocking_forever()
    {
        // A clock change, or a settings file copied from another machine, produces one of these.
        // Read as "not due" it would sit there refusing every check until real time caught up.
        Assert.True(UpdateCooldown.IsDue(Now.AddYears(5), Now));
        Assert.Equal("Checked recently", UpdateCooldown.Describe(Now.AddYears(5), Now));
    }

    [Theory]
    [InlineData(0, "Checked just now")]
    [InlineData(1, "Checked 1 minute ago")]
    [InlineData(12, "Checked 12 minutes ago")]
    [InlineData(60, "Checked 1 hour ago")]
    [InlineData(200, "Checked 3 hours ago")]
    [InlineData(60 * 24 * 2, "Checked 2 days ago")]
    public void The_last_check_is_described_in_whole_units(int minutesAgo, string expected)
    {
        Assert.Equal(expected, UpdateCooldown.Describe(Now.AddMinutes(-minutesAgo), Now));
    }
}
