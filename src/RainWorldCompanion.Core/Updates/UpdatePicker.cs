namespace RainWorldCompanion.Core.Updates;

public static class UpdatePicker
{
    /// <summary>
    /// A null <paramref name="current"/> produces no offer at all rather than an offer of
    /// everything: a copy that cannot identify itself must not be talked into replacing itself.
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
    /// Includes versions older than the running one, because this backs the list someone scrolls
    /// through to go back to an earlier build.
    /// </summary>
    public static IReadOnlyList<UpdateOffer> ForChannel(
        IEnumerable<ReleaseCandidate> releases,
        UpdateChannel channel)
    {
        var offers = Installable(releases, channel).ToList();
        offers.Sort((left, right) => right.Version.CompareTo(left.Version));
        return offers;
    }

    private static IEnumerable<UpdateOffer> Installable(
        IEnumerable<ReleaseCandidate> releases,
        UpdateChannel channel)
    {
        // Alpha builds do not come from releases at all, and this yields nothing rather than
        // quietly falling back to stable.
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

            // GitHub's flag is set by hand, and a tag with a "-beta.1" tail is a pre-release
            // whether or not anyone ticked it.
            var isPrerelease = release.IsPrereleaseFlag || version.IsPreRelease;
            if (isPrerelease && channel == UpdateChannel.Stable)
            {
                continue;
            }

            var asset = FindInstaller(release.Assets);
            if (asset is null)
            {
                // A tag exists from the moment the workflow starts and the asset appears minutes
                // later, so a release can be real and still have nothing on it yet.
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
                release.PublishedUtc,
                ReleaseNotes.ForDisplay(release.Notes));
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

            if (asset.SizeBytes <= 0 || !UpdateUrls.IsAllowedDownload(asset.DownloadUrl))
            {
                continue;
            }

            return asset;
        }

        return null;
    }
}
