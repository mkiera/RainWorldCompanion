namespace RainWorldCompanion.Core.Updates;

/// <summary>
/// The part of a release body worth showing inside the app.
///
/// A release page is written for somebody who has not got the app yet, so it ends with how to
/// install it, how to update it and how to uninstall it. None of that is worth screen space to
/// somebody who is already running the build and only wants to know what changed, so the release
/// workflow marks where one ends and the other begins and this cuts there.
/// </summary>
public static class ReleaseNotes
{
    /// <summary>
    /// What the release workflow writes between this tag's changelog section and the install
    /// blurb. An HTML comment, so it is invisible on the release page and costs the reader there
    /// nothing.
    /// </summary>
    public const string EndMarker = "<!-- app-notes-end -->";

    /// <summary>
    /// The notes as the app should show them, or blank when the release said nothing.
    ///
    /// A body with no marker is shown whole rather than dropped. Releases published before the
    /// changelog existed carry the blurb alone, and showing a paragraph too many is a smaller
    /// failure than a what's-new banner with nothing in it.
    /// </summary>
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
