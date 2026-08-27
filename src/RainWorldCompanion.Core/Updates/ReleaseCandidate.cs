namespace RainWorldCompanion.Core.Updates;

/// <param name="DownloadUrl">Checked against the host allowlist before use.</param>
/// <param name="SizeBytes">
/// An asset reporting zero is not offered: without a length there is no way to tell a truncated
/// installer from a complete one.
/// </param>
public sealed record ReleaseAsset(string Name, string DownloadUrl, long SizeBytes);

/// <param name="TagName">Normally with a leading "v". The authority for the version.</param>
/// <param name="IsPrereleaseFlag">
/// Taken together with the tag rather than trusted alone: a person publishing by hand can forget it.
/// </param>
/// <param name="Assets">Empty while an upload is still in progress.</param>
/// <param name="Notes">Optional: a missing or empty body must not drop the release.</param>
public sealed record ReleaseCandidate(
    string TagName,
    string HtmlUrl,
    bool IsDraft,
    bool IsPrereleaseFlag,
    DateTimeOffset? PublishedUtc,
    IReadOnlyList<ReleaseAsset> Assets,
    string Notes = "");

/// <summary>
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
    DateTimeOffset? PublishedUtc,
    string Notes = "")
{
    public string VersionText => Version.ToString();

    public bool HasNotes => Notes.Length != 0;
}
