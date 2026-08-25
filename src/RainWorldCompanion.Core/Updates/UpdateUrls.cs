namespace RainWorldCompanion.Core.Updates;

/// <summary>
/// Where the app looks for new builds, and what it will accept a download from.
///
/// Every URL the downloader is ever handed came out of a JSON document fetched over the network,
/// which makes it data rather than instruction. The host allowlist here is what keeps a rewritten
/// or hostile API response from pointing the download at somewhere else entirely, since the file
/// at the end of it is one the app goes on to execute.
/// </summary>
public static class UpdateUrls
{
    public const string Owner = "mkiera";
    public const string Repo = "RainWorldCompanion";

    /// <summary>
    /// The list, not /releases/latest. That endpoint never returns a pre-release, so it cannot
    /// answer for the pre-release channel, and it cannot produce the list of older versions the
    /// updates window offers to go back to.
    /// </summary>
    public const string Releases = $"https://api.github.com/repos/{Owner}/{Repo}/releases";

    /// <summary>
    /// Scoped to the one workflow. The unscoped runs endpoint mixes in every release build, which
    /// buries the branch builds this is looking for and spends the anonymous request budget doing
    /// it. The file name here has to match the workflow file in .github/workflows.
    /// </summary>
    public const string BranchBuildRuns =
        $"https://api.github.com/repos/{Owner}/{Repo}/actions/workflows/build-test.yml/runs";

    /// <summary>
    /// GitHub's own artifact download needs a token even on a public repository, which an app
    /// with no account to sign in to does not have. nightly.link serves the same zip anonymously.
    /// </summary>
    public const string BranchBuildDownload = $"https://nightly.link/{Owner}/{Repo}/actions/runs";

    /// <summary>The releases page, for the link that opens a browser.</summary>
    public const string ReleasesPage = $"https://github.com/{Owner}/{Repo}/releases";

    /// <summary>
    /// What the branch-build workflow calls its upload, which makes the download address of any
    /// run derivable from its id alone. A constant rather than the branch name, because this ends
    /// up in a URL path and half of all branch names hold a slash.
    /// </summary>
    public const string BranchBuildArtifact = "RainWorldCompanion-Setup";

    /// <summary>The zip holding one branch build's installer.</summary>
    public static string BranchBuildZip(long runId) =>
        $"{BranchBuildDownload}/{runId}/{BranchBuildArtifact}.zip";

    /// <summary>The run's page on GitHub, for the link that opens a browser.</summary>
    public static string BranchBuildPage(long runId) =>
        $"https://github.com/{Owner}/{Repo}/actions/runs/{runId}";

    /// <summary>
    /// The suffix every installer asset carries, matched case-insensitively.
    ///
    /// Deliberately the whole tail rather than "contains the word setup". Whatever rule ships in
    /// the first release is frozen in every copy that release ever installs, and a loose rule
    /// cannot be tightened afterwards: those copies keep applying it to releases built years
    /// later. FinFetcher is still publishing a decoy asset named to win a byte-wise sort because
    /// its first rule was "the first .exe".
    /// </summary>
    public const string InstallerSuffix = "-setup.exe";

    /// <summary>
    /// The User-Agent header. GitHub answers 403 to an unauthenticated request that carries none,
    /// so this is not decoration.
    /// </summary>
    public static string UserAgent(string version) =>
        $"{Repo}/{version} (+https://github.com/{Owner}/{Repo})";

    private static readonly string[] AllowedHosts =
    [
        "github.com",
        "api.github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
        "nightly.link",
    ];

    /// <summary>
    /// Whether a download may be fetched from this address. HTTPS is required, and the host must
    /// be one of the handful that serve GitHub release assets or the artifact proxy.
    /// </summary>
    public static bool IsAllowedDownload(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsAllowedDownload(uri);

    public static bool IsAllowedDownload(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        foreach (var host in AllowedHosts)
        {
            if (string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a name from the network is safe to use as a file name and as a URL path segment.
    ///
    /// Refused rather than sanitised. A name that needed cleaning up was not the name of anything
    /// this app published, so there is nothing on the other side of it worth downloading.
    /// </summary>
    public static bool IsSafeAssetName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128)
        {
            return false;
        }

        // Must begin with a letter or a digit. Nothing this app publishes starts with anything
        // else, and both of the alternatives cause trouble further down: a leading dot is how
        // ".." and every other traversal starts, and a leading dash is read as a switch by
        // anything that later passes the name to a command line.
        if (!char.IsAsciiLetterOrDigit(name[0]))
        {
            return false;
        }

        foreach (var c in name)
        {
            var ok = c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')
                or '.' or '_' or '-';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True when this asset is the installer rather than anything else on a release.</summary>
    public static bool IsInstallerAsset(string? name)
        => IsSafeAssetName(name)
           && name!.EndsWith(InstallerSuffix, StringComparison.OrdinalIgnoreCase);
}
