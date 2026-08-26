namespace RainWorldCompanion.Core.Updates;

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

    public static UpdateChannel Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        PrereleaseText => UpdateChannel.Prerelease,
        AlphaText => UpdateChannel.Alpha,
        _ => UpdateChannel.Stable,
    };

    /// <summary>
    /// Text rather than the enum's number: System.Text.Json writes an enum as its ordinal, so
    /// inserting a channel between two existing ones would change what every saved file means.
    /// </summary>
    public static string ToStorageString(this UpdateChannel channel) => channel switch
    {
        UpdateChannel.Prerelease => PrereleaseText,
        UpdateChannel.Alpha => AlphaText,
        _ => StableText,
    };

    public static bool CanBeOfferedAutomatically(this UpdateChannel channel)
        => channel is UpdateChannel.Stable or UpdateChannel.Prerelease;

    /// <summary>The three channels in the order the window lists them, steadiest first.</summary>
    public static IReadOnlyList<UpdateChannel> All { get; } =
        [UpdateChannel.Stable, UpdateChannel.Prerelease, UpdateChannel.Alpha];

    public static string Title(this UpdateChannel channel) => channel switch
    {
        UpdateChannel.Prerelease => "Beta",
        UpdateChannel.Alpha => "Alpha",
        _ => "Stable",
    };

    public static string Description(this UpdateChannel channel) => channel switch
    {
        UpdateChannel.Prerelease =>
            "Tagged pre-releases, plus everything on stable. Offered automatically.",
        UpdateChannel.Alpha =>
            "The latest build of each branch, straight from CI. Never offered, install by hand.",
        _ => "Finished releases only. Offered automatically.",
    };
}
