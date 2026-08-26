namespace RainWorldCompanion.Core.Updates;

public enum ReleaseAction
{
    /// <summary>The running version is unknown, so the row cannot be placed against it.</summary>
    Install,

    Update,

    Downgrade,

    Reinstall,
}

public static class ReleaseActions
{
    /// <summary>
    /// A null <paramref name="running"/> means the running copy could not say what version it is,
    /// and those rows read Install rather than claiming a direction nobody knows.
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

    public static string Verb(this ReleaseAction action) => action switch
    {
        ReleaseAction.Update => "Update",
        ReleaseAction.Downgrade => "Downgrade",
        ReleaseAction.Reinstall => "Reinstall",
        _ => "Install",
    };

    public static bool NeedsConfirmation(this ReleaseAction action)
        => action == ReleaseAction.Downgrade;

    public static string ConfirmationText(string versionText) =>
        $"Going back to {versionText} replaces the newer build you are running. Your backups and "
        + "your library are untouched, and your settings stay as they are, which an older build "
        + "may not read in full. Press Downgrade again to go ahead.";
}
