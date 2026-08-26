namespace RainWorldCompanion.Core.Updates;

/// <summary>
/// Every URL the downloader is handed came out of a JSON document fetched over the network. The
/// host allowlist here is what keeps a rewritten API response from pointing the download somewhere
/// else, since the app goes on to execute the file at the end of it.
/// </summary>
public static class UpdateUrls
{
    public const string Owner = "mkiera";
    public const string Repo = "RainWorldCompanion";

    /// <summary>
    /// The list, not /releases/latest: that endpoint never returns a pre-release, and cannot
    /// produce the older versions the updates window offers to go back to.
    /// </summary>
    public const string Releases = $"https://api.github.com/repos/{Owner}/{Repo}/releases";

    /// <summary>The file name here has to match the workflow file in .github/workflows.</summary>
    public const string BranchBuildRuns =
        $"https://api.github.com/repos/{Owner}/{Repo}/actions/workflows/build-test.yml/runs";

    /// <summary>
    /// GitHub's own artifact download needs a token even on a public repository, which an app
    /// with no account to sign in to does not have. nightly.link serves the same zip anonymously.
    /// </summary>
    public const string BranchBuildDownload = $"https://nightly.link/{Owner}/{Repo}/actions/runs";

    public const string ReleasesPage = $"https://github.com/{Owner}/{Repo}/releases";

    /// <summary>A constant rather than the branch name, which would put a slash in a URL path.</summary>
    public const string BranchBuildArtifact = "RainWorldCompanion-Setup";

    public static string BranchBuildZip(long runId) =>
        $"{BranchBuildDownload}/{runId}/{BranchBuildArtifact}.zip";

    public static string BranchBuildPage(long runId) =>
        $"https://github.com/{Owner}/{Repo}/actions/runs/{runId}";

    /// <summary>
    /// The whole tail rather than "contains the word setup". Whatever rule ships in the first
    /// release is frozen in every copy that release installs, so a loose rule cannot be tightened
    /// afterwards.
    /// </summary>
    public const string InstallerSuffix = "-setup.exe";

    /// <summary>GitHub answers 403 to an unauthenticated request that carries no User-Agent.</summary>
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
    /// Refused rather than sanitised: a name that needed cleaning up was not the name of anything
    /// this app published.
    /// </summary>
    public static bool IsSafeAssetName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128)
        {
            return false;
        }

        // A leading dot is how ".." and every other traversal starts, and a leading dash is read
        // as a switch by anything that later passes the name to a command line.
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

    public static bool IsInstallerAsset(string? name)
        => IsSafeAssetName(name)
           && name!.EndsWith(InstallerSuffix, StringComparison.OrdinalIgnoreCase);
}
