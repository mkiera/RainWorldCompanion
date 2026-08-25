namespace RainWorldCompanion.Core.Updates;

/// <summary>
/// Reads the log Inno Setup writes when it is handed /LOG.
///
/// The app launches the installer and then has to decide whether to close itself. Getting that
/// wrong in one direction leaves the user staring at a dialog with no application left to explain
/// where it came from, so the log is the one place to find out whether Setup is working or
/// waiting. Everything here is pure: the caller reads the file and hands the lines over.
/// </summary>
public static class SetupLog
{
    private const string MessageBoxPrefix = "Message box (";
    private const string UserChosePrefix = "User chose ";

    /// <summary>
    /// The log folded into one string per message.
    ///
    /// A line Setup writes starts with a timestamp and two or more spaces. Anything else is the
    /// continuation of the message above it, which is how the text inside a message box is
    /// recorded, so those are joined onto the message they belong to rather than being read as
    /// messages of their own.
    /// </summary>
    public static IReadOnlyList<string> ReadMessages(IEnumerable<string>? lines)
    {
        var messages = new List<string>();
        if (lines is null)
        {
            return messages;
        }

        foreach (var raw in lines)
        {
            // The file is UTF-8 with a byte order mark. A reader that does not strip it leaves the
            // character on the front of the first line, where it would stop that line looking like
            // a timestamp.
            var line = raw.TrimStart('﻿').TrimEnd();
            if (line.Length == 0)
            {
                continue;
            }

            var text = StripTimestamp(line);
            if (text is not null)
            {
                messages.Add(text);
            }
            else if (messages.Count != 0)
            {
                messages[^1] = messages[^1] + " " + line.Trim();
            }
        }

        return messages;
    }

    /// <summary>
    /// What Setup is asking, when it is sitting on a message box nobody has answered, or null
    /// when it is not.
    ///
    /// Inno writes the box and its text to the log as it puts it on screen, and writes the answer
    /// when one arrives, so an unanswered box is the last "Message box (" with no "User chose "
    /// after it. The file stops growing while a box is up, which is what makes this readable at
    /// all from outside the process.
    /// </summary>
    public static string? PendingDialog(IReadOnlyList<string>? messages)
    {
        if (messages is null)
        {
            return null;
        }

        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].StartsWith(UserChosePrefix, StringComparison.Ordinal))
            {
                // Answered, and everything before it is older still.
                return null;
            }

            if (messages[i].StartsWith(MessageBoxPrefix, StringComparison.Ordinal))
            {
                return Tidy(messages[i]);
            }
        }

        return null;
    }

    /// <summary>
    /// Drops the "Message box (Yes/No):" preamble, leaving the sentence a person would read.
    /// </summary>
    private static string Tidy(string message)
    {
        var colon = message.IndexOf(':');
        var text = colon >= 0 && colon + 1 < message.Length
            ? message[(colon + 1)..].Trim()
            : message.Trim();

        return text.Length == 0 ? message.Trim() : text;
    }

    /// <summary>
    /// The message on a timestamped line, or null when the line carries no timestamp and is
    /// therefore a continuation.
    ///
    /// Matched by shape rather than by parsing a date: the log is written in the machine's own
    /// locale, so anything that tried to read the timestamp would have to agree with whatever
    /// that machine does with dates. All that is needed here is the boundary.
    /// </summary>
    private static string? StripTimestamp(string line)
    {
        // Setup writes "yyyy-mm-dd hh:mm:ss.mmm" and then at least three spaces.
        var separator = line.IndexOf("   ", StringComparison.Ordinal);
        if (separator <= 0)
        {
            return null;
        }

        var stamp = line[..separator];
        if (stamp.Length is < 8 or > 40 || !char.IsAsciiDigit(stamp[0]))
        {
            return null;
        }

        foreach (var c in stamp)
        {
            if (!char.IsAsciiDigit(c) && c is not (':' or '-' or '.' or ' ' or '/'))
            {
                return null;
            }
        }

        return line[separator..].Trim();
    }
}
