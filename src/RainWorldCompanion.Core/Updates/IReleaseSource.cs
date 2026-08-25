namespace RainWorldCompanion.Core.Updates;

/// <summary>
/// Where the list of builds comes from.
///
/// The interface lives here rather than beside its one implementation for the same reason
/// IGameProcessDetector does: it is what the decision layer talks to, and keeping it in Core is
/// what lets a test hand over a canned list without a network. The implementation stays in the app
/// project, so Core carries no HttpClient and no obligation to know what an HTTP status is.
/// </summary>
public interface IReleaseSource
{
    /// <summary>
    /// Every published release, in whatever order the server gave them. Ordering is the picker's
    /// job, because nothing promises the server sorts by version.
    /// </summary>
    Task<IReadOnlyList<ReleaseCandidate>> GetReleasesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Runs of the branch-build workflow, newest first. Only reached when someone opens the
    /// branch-builds list, so it never costs a request during an ordinary update check.
    /// </summary>
    Task<IReadOnlyList<WorkflowRun>> GetBranchBuildRunsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A source with nothing in it, for a test or a view model that must not reach the network.
/// Mirrors NullGameProcessDetector, which exists for the same reason.
/// </summary>
public sealed class NullReleaseSource : IReleaseSource
{
    public Task<IReadOnlyList<ReleaseCandidate>> GetReleasesAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<ReleaseCandidate>>([]);

    public Task<IReadOnlyList<WorkflowRun>> GetBranchBuildRunsAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<WorkflowRun>>([]);
}
