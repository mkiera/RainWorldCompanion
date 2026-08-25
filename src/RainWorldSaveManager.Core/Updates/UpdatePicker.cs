namespace RainWorldSaveManager.Core.Updates;

/// <summary>
/// Decides which releases this copy is allowed to see, and which one it should be offered.
///
/// Pure on purpose: no network, no clock, no settings file. Getting this wrong is quiet in both
/// directions. Offering nothing means a fix never reaches anyone, and offering the wrong thing
/// walks somebody backwards onto an older build without telling them, so it is the piece worth
/// being able to test exhaustively.
/// </summary>
public static class UpdatePicker
{
    /// <summary>
    /// The one release to offer, or null when there is nothing newer.
    ///
    /// A null <paramref name="current"/> means the running copy could not say what version it is.
    /// That produces no offer at all rather than an offer of everything: a copy that cannot
    /// identify itself must not be talked into replacing itself.
    /// </summary>
    public static UpdateOffer? Pick(
        IEnumerable<ReleaseCandidate> releases,
        SemVer? current,
        UpdateChannel channel)
    {
        if (current is not { } running)
        {
            return null;
        }

        UpdateOffer? best = null;

        foreach (var offer in Installable(releases, channel))
        {
            if (offer.Version <= running)
            {
                continue;
            }

            // The newest, not the first. The API lists releases in its own order, and nothing
            // promises that order is by version.
            if (best is null || offer.Version > best.Version)
            {
                best = offer;
            }
        }

        return best;
    }

    /// <summary>
    /// Every release on this channel that could be installed, newest first.
    ///
    /// Includes versions older than the running one, because this is what backs the list someone
    /// scrolls through to go back to an earlier build. Deciding whether a row is an update, a
    /// downgrade or a reinstall is the caller's job.
    /// </summary>
    public static IReadOnlyList<UpdateOffer> ForChannel(
        IEnumerable<ReleaseCandidate> releases,
        UpdateChannel channel)
    {
        var offers = Installable(releases, channel).ToList();
        offers.Sort((left, right) => right.Version.CompareTo(left.Version));
        return offers;
    }

    /// <summary>
    /// The filters, in order: a draft is not published, a tag that is not a version cannot be
    /// placed against anything, a pre-release is hidden from the stable channel, and a release
    /// has to carry an installer that can actually be fetched.
    /// </summary>
    private static IEnumerable<UpdateOffer> Installable(
        IEnumerable<ReleaseCandidate> releases,
        UpdateChannel channel)
    {
        // Alpha builds do not come from releases at all. Asked for that channel, the release list
        // is empty rather than quietly falling back to stable, so a caller cannot mistake one for
        // the other.
        if (channel == UpdateChannel.Alpha)
        {
            yield break;
        }

        foreach (var release in releases)
        {
            if (release is null || release.IsDraft)
            {
                continue;
            }

            if (!SemVer.TryParse(release.TagName, out var version))
            {
                continue;
            }

            // Either signal counts. GitHub's flag is set by hand for a release published by hand,
            // and a tag with a "-beta.1" tail is a pre-release whether or not anyone ticked it.
            var isPrerelease = release.IsPrereleaseFlag || version.IsPreRelease;
            if (isPrerelease && channel == UpdateChannel.Stable)
            {
                continue;
            }

            var asset = FindInstaller(release.Assets);
            if (asset is null)
            {
                // A tag exists from the moment the workflow starts and the asset appears minutes
                // later, so a release can be real and still have nothing on it yet. Skipping it
                // rather than stopping is what keeps a half-published release from hiding an
                // older one that works.
                continue;
            }

            yield return new UpdateOffer(
                version,
                release.TagName,
                release.HtmlUrl,
                asset.DownloadUrl,
                asset.Name,
                asset.SizeBytes,
                isPrerelease,
                release.PublishedUtc);
        }
    }

    private static ReleaseAsset? FindInstaller(IReadOnlyList<ReleaseAsset>? assets)
    {
        if (assets is null)
        {
            return null;
        }

        foreach (var asset in assets)
        {
            if (asset is null || !UpdateUrls.IsInstallerAsset(asset.Name))
            {
                continue;
            }

            // Both come off the network. A size of zero leaves the download with nothing to check
            // itself against, and a host outside the allowlist is not somewhere this app fetches
            // anything from, so neither is a release worth listing.
            if (asset.SizeBytes <= 0 || !UpdateUrls.IsAllowedDownload(asset.DownloadUrl))
            {
                continue;
            }

            return asset;
        }

        return null;
    }
}
