namespace RainWorldCompanion.Core.Updates;

/// <summary>
/// One file attached to a release.
/// </summary>
/// <param name="Name">The file name as published, for example RainWorldCompanion-Setup.exe.</param>
/// <param name="DownloadUrl">Where to fetch it. Checked against the host allowlist before use.</param>
/// <param name="SizeBytes">
/// What the release says the file weighs. Nothing downstream can tell a truncated installer from
/// a complete one without a length to check against, and a truncated installer that still runs is
/// the worst outcome available here, so an asset that reports zero is not offered.
/// </param>
public sealed record ReleaseAsset(string Name, string DownloadUrl, long SizeBytes);

/// <summary>
/// One release as GitHub describes it, before this app has decided anything about it.
///
/// A plain carrier for the six fields that matter, so the picker can be a pure function over a
/// list of these and the tests never need a network.
/// </summary>
/// <param name="TagName">The tag, normally with a leading "v". The authority for the version.</param>
/// <param name="HtmlUrl">The release page, for the link that opens a browser.</param>
/// <param name="IsDraft">A release that is not published. Never a candidate.</param>
/// <param name="IsPrereleaseFlag">
/// GitHub's own flag. Taken together with the tag rather than trusted alone, because a person
/// publishing by hand can forget to tick it.
/// </param>
/// <param name="PublishedUtc">When it went out, or null for a draft.</param>
/// <param name="Assets">The files on it. Empty while an upload is still in progress.</param>
public sealed record ReleaseCandidate(
    string TagName,
    string HtmlUrl,
    bool IsDraft,
    bool IsPrereleaseFlag,
    DateTimeOffset? PublishedUtc,
    IReadOnlyList<ReleaseAsset> Assets);

/// <summary>
/// A release this app is willing to install, with everything needed to go and get it.
///
/// Reaching this type means the tag parsed, the channel allows it, an installer asset is attached,
/// its name is usable as a file name, its host is on the allowlist, and its size is known.
/// </summary>
public sealed record UpdateOffer(
    SemVer Version,
    string TagName,
    string ReleaseUrl,
    string DownloadUrl,
    string AssetName,
    long SizeBytes,
    bool IsPrerelease,
    DateTimeOffset? PublishedUtc)
{
    /// <summary>How the version reads on screen, without a leading "v".</summary>
    public string VersionText => Version.ToString();
}
