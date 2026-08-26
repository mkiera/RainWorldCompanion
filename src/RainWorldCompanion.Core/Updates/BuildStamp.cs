namespace RainWorldCompanion.Core.Updates;

/// <summary>
/// <paramref name="Version"/> is the only field that is always present. The rest come from the
/// build that made the exe and are blank in a copy built any other way.
/// </summary>
/// <param name="Version">
/// Read from AssemblyInformationalVersion, the only version attribute that can carry a "-beta.1" tail.
/// </param>
/// <param name="Branch">Set only by the branch-build workflow: a release is built from a tag.</param>
public sealed record BuildStamp(string Version, string CommitSha, string Branch, string RunId)
{
    public static BuildStamp ForVersion(string version) => new(version, "", "", "");

    public string ShortSha => CommitSha.Length <= 7 ? CommitSha : CommitSha[..7];

    /// <summary>
    /// Null when the version cannot be read, which callers treat as a reason to offer no update
    /// rather than every update.
    /// </summary>
    public SemVer? ParsedVersion => SemVer.TryParse(Version, out var parsed) ? parsed : null;
}
