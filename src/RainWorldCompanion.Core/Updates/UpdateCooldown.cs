namespace RainWorldCompanion.Core.Updates;

/// <summary>
/// Unauthenticated GitHub requests are budgeted at sixty an hour per address, and this app has no
/// account to sign in with, so the budget is shared with everything else on the machine.
/// </summary>
public static class UpdateCooldown
{
    /// <summary>Between automatic checks. A check the user asked for ignores this.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Phrased as "not inside the window" rather than "long enough ago" on purpose. A stamp from
    /// the future, which a clock change produces, would otherwise block every check until real
    /// time caught up with it.
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

    public static string Describe(DateTimeOffset? lastCheck, DateTimeOffset now)
    {
        if (lastCheck is not { } last)
        {
            return "Not checked yet";
        }

        var elapsed = now - last;
        if (elapsed < TimeSpan.Zero)
        {
            // The same stamp from the future that IsDue tolerates.
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
