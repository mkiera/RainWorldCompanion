namespace RainWorldCompanion.Core.Updates;

/// <summary>
/// A release body ends with install instructions written for somebody who has not got the app yet.
/// The release workflow marks where the changelog ends, and this cuts there.
/// </summary>
public static class ReleaseNotes
{
    public const string EndMarker = "<!-- app-notes-end -->";

    /// <summary>A body with no marker is shown whole rather than dropped.</summary>
    public static string ForDisplay(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "";
        }

        var text = body;

        var cut = text.IndexOf(EndMarker, StringComparison.Ordinal);
        if (cut >= 0)
        {
            text = text[..cut];
        }

        // GitHub returns the body with CRLF endings. WPF renders a stray CR as a box, and the
        // trim below cannot see a line as blank while it still holds one.
        return text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
    }
}
