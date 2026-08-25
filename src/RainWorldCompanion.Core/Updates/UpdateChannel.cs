namespace RainWorldCompanion.Core.Updates;

/// <summary>
/// Which builds the app is willing to show.
///
/// Only <see cref="Stable"/> and <see cref="Prerelease"/> are ever saved, and only those two can
/// produce an offer the app makes on its own. <see cref="Alpha"/> is a view of the branch builds
/// coming out of CI: browsable and installable by hand, never offered.
/// </summary>
public enum UpdateChannel
{
    Stable,
    Prerelease,
    Alpha,
}

public static class UpdateChannels
{
    private const string StableText = "stable";
    private const string PrereleaseText = "prerelease";
    private const string AlphaText = "alpha";

    /// <summary>
    /// Reads the value stored in settings.json.
    ///
    /// Anything unrecognised reads as <see cref="UpdateChannel.Stable"/>, which is also the right
    /// answer for a file written by a newer build naming a channel this one has never heard of:
    /// the conservative channel is the safe place to land.
    /// </summary>
    public static UpdateChannel Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        PrereleaseText => UpdateChannel.Prerelease,
        AlphaText => UpdateChannel.Alpha,
        _ => UpdateChannel.Stable,
    };

    /// <summary>
    /// The settings.json form. Stored as text rather than as the enum's number, because
    /// System.Text.Json writes an enum as its ordinal and inserting a channel between two
    /// existing ones would then silently change what every saved file means.
    /// </summary>
    public static string ToStorageString(this UpdateChannel channel) => channel switch
    {
        UpdateChannel.Prerelease => PrereleaseText,
        UpdateChannel.Alpha => AlphaText,
        _ => StableText,
    };

    /// <summary>
    /// Whether an offer the app makes without being asked may come from this channel. Alpha
    /// builds are branch CI output and are never pushed at anyone.
    /// </summary>
    public static bool CanBeOfferedAutomatically(this UpdateChannel channel)
        => channel is UpdateChannel.Stable or UpdateChannel.Prerelease;

    /// <summary>The three channels in the order the window lists them, steadiest first.</summary>
    public static IReadOnlyList<UpdateChannel> All { get; } =
        [UpdateChannel.Stable, UpdateChannel.Prerelease, UpdateChannel.Alpha];

    /// <summary>The name on the row.</summary>
    public static string Title(this UpdateChannel channel) => channel switch
    {
        UpdateChannel.Prerelease => "Pre-release",
        UpdateChannel.Alpha => "Branch builds",
        _ => "Stable",
    };

    /// <summary>
    /// What picking it means, said in terms of where the build came from rather than how risky it
    /// is. Where a build comes from is checkable, and how good it is is not.
    /// </summary>
    public static string Description(this UpdateChannel channel) => channel switch
    {
        UpdateChannel.Prerelease =>
            "Tagged pre-releases, plus everything on stable. Offered automatically.",
        UpdateChannel.Alpha =>
            "The latest build of each branch, straight from CI. Never offered, install by hand.",
        _ => "Finished releases only. Offered automatically.",
    };
}
