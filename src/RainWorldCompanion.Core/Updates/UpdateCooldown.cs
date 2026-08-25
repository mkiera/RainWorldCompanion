namespace RainWorldCompanion.Core.Updates;

/// <summary>
/// How often the app is allowed to ask GitHub on its own.
///
/// Unauthenticated requests are budgeted at sixty an hour per address, and this app has no
/// account to sign in with, so an over-eager check spends a budget shared with everything else on
/// the machine.
/// </summary>
public static class UpdateCooldown
{
    /// <summary>Between automatic checks. A check the user asked for ignores this.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>
    /// How long after startup the first automatic check waits. Long enough that opening the
    /// window, reading the settings and listing the backups all happen first: an update is never
    /// the reason someone launched this.
    /// </summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Whether an automatic check is due.
    ///
    /// Phrased as "not inside the window" rather than "long enough ago" on purpose. A stamp from
    /// the future, which a clock change or a settings file copied from another machine produces,
    /// would otherwise sit there blocking every check until real time caught up with it. Reading
    /// an unusable stamp as due costs one request; reading it as not due costs every request.
    /// </summary>
    public static bool IsDue(DateTimeOffset? lastCheck, DateTimeOffset now)
    {
        if (lastCheck is not { } last)
        {
            return true;
        }

        var elapsed = now - last;
        return !(elapsed >= TimeSpan.Zero && elapsed < Interval);
    }

    /// <summary>
    /// "checked 12 minutes ago", or a note that it never has been. Shown beside the refresh
    /// button so the absence of an offer reads as "nothing new" rather than "nothing happened".
    /// </summary>
    public static string Describe(DateTimeOffset? lastCheck, DateTimeOffset now)
    {
        if (lastCheck is not { } last)
        {
            return "Not checked yet";
        }

        var elapsed = now - last;
        if (elapsed < TimeSpan.Zero)
        {
            // Same unusable stamp IsDue tolerates. Saying nothing sensible beats saying it was
            // checked in the future.
            return "Checked recently";
        }

        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return "Checked just now";
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            var minutes = (int)elapsed.TotalMinutes;
            return $"Checked {minutes} minute{(minutes == 1 ? "" : "s")} ago";
        }

        if (elapsed < TimeSpan.FromDays(1))
        {
            var hours = (int)elapsed.TotalHours;
            return $"Checked {hours} hour{(hours == 1 ? "" : "s")} ago";
        }

        var days = (int)elapsed.TotalDays;
        return $"Checked {days} day{(days == 1 ? "" : "s")} ago";
    }
}
