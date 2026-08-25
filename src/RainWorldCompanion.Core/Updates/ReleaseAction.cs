namespace RainWorldCompanion.Core.Updates;

/// <summary>
/// What pressing the button on a row in the updates window would do to the copy on disk.
///
/// The list holds every version on the channel, older ones included, so the same button means
/// three different things depending on the row it sits on. Naming each one is what keeps somebody
/// from walking backwards onto an older build while reading the word "Update".
/// </summary>
public enum ReleaseAction
{
    /// <summary>The running version is unknown, so the row cannot be placed against it.</summary>
    Install,

    /// <summary>Newer than the running copy.</summary>
    Update,

    /// <summary>Older than the running copy.</summary>
    Downgrade,

    /// <summary>The version already running.</summary>
    Reinstall,
}

public static class ReleaseActions
{
    /// <summary>
    /// Which of the four a row is, measured against the running version.
    ///
    /// A null <paramref name="running"/> means the running copy could not say what version it is,
    /// which is the one case where no comparison is honest. Those rows read Install: it says what
    /// the button does without claiming a direction nobody knows.
    ///
    /// Build metadata takes no part in the comparison, because SemVer excludes it from ordering.
    /// A release rebuilt at a different commit under the same version is therefore a reinstall,
    /// which is what it is.
    /// </summary>
    public static ReleaseAction For(SemVer candidate, SemVer? running)
    {
        if (running is not { } current)
        {
            return ReleaseAction.Install;
        }

        return candidate.CompareTo(current) switch
        {
            > 0 => ReleaseAction.Update,
            < 0 => ReleaseAction.Downgrade,
            _ => ReleaseAction.Reinstall,
        };
    }

    /// <summary>The word on the button.</summary>
    public static string Verb(this ReleaseAction action) => action switch
    {
        ReleaseAction.Update => "Update",
        ReleaseAction.Downgrade => "Downgrade",
        ReleaseAction.Reinstall => "Reinstall",
        _ => "Install",
    };

    /// <summary>
    /// Whether the app asks a second time before running it.
    ///
    /// Only a downgrade does. Going back replaces a newer build with an older one, and settings
    /// written by the newer copy stay where they are, so it is the one direction that can leave
    /// the app reading a file it does not understand.
    /// </summary>
    public static bool NeedsConfirmation(this ReleaseAction action)
        => action == ReleaseAction.Downgrade;

    /// <summary>The sentence shown once a downgrade has been armed and is waiting on a second press.</summary>
    public static string ConfirmationText(string versionText) =>
        $"Going back to {versionText} replaces the newer build you are running. Your backups and "
        + "your library are untouched, and your settings stay as they are, which an older build "
        + "may not read in full. Press Downgrade again to go ahead.";
}
