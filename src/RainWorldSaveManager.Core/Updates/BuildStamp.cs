namespace RainWorldSaveManager.Core.Updates;

/// <summary>
/// What the running copy can say about itself: which version it is, and which build produced it.
///
/// <paramref name="Version"/> is the only field that is always present. The rest come from the
/// build that made the exe and are blank in a copy built any other way, which is correct rather
/// than a gap: a local build was never uploaded anywhere, so there is no CI run for it to match.
/// </summary>
/// <param name="Version">
/// The semver string, without build metadata. Read from AssemblyInformationalVersion, which is
/// the only version attribute that can carry a "-beta.1" tail.
/// </param>
/// <param name="CommitSha">The commit this was built from, or blank.</param>
/// <param name="Branch">
/// The branch this was built from, or blank. Set only by the branch-build workflow: a release is
/// built from a tag, and a tag is not a branch.
/// </param>
/// <param name="RunId">The CI run that produced this, or blank.</param>
public sealed record BuildStamp(string Version, string CommitSha, string Branch, string RunId)
{
    /// <summary>A stamp for a copy that has nothing to say beyond its version.</summary>
    public static BuildStamp ForVersion(string version) => new(version, "", "", "");

    /// <summary>
    /// The first seven characters of the commit, which is how git abbreviates one and how the
    /// alpha list labels its rows. Blank when there is no commit.
    /// </summary>
    public string ShortSha => CommitSha.Length <= 7 ? CommitSha : CommitSha[..7];

    /// <summary>
    /// The version as something orderable, or null when it cannot be read. Null should not
    /// happen in a shipped build, and the callers that would offer an update treat it as a
    /// reason to offer nothing rather than to offer everything: a copy that cannot say what it
    /// is must not be talked into replacing itself.
    /// </summary>
    public SemVer? ParsedVersion => SemVer.TryParse(Version, out var parsed) ? parsed : null;
}
