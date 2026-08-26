namespace RainWorldCompanion.Core.Updates;

public interface IReleaseSource
{
    /// <summary>
    /// In whatever order the server gave them. Ordering is the picker's job, because nothing
    /// promises the server sorts by version.
    /// </summary>
    Task<IReadOnlyList<ReleaseCandidate>> GetReleasesAsync(CancellationToken cancellationToken);

    /// <summary>Runs of the branch-build workflow, newest first.</summary>
    Task<IReadOnlyList<WorkflowRun>> GetBranchBuildRunsAsync(CancellationToken cancellationToken);
}

public sealed class NullReleaseSource : IReleaseSource
{
    public Task<IReadOnlyList<ReleaseCandidate>> GetReleasesAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<ReleaseCandidate>>([]);

    public Task<IReadOnlyList<WorkflowRun>> GetBranchBuildRunsAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<WorkflowRun>>([]);
}
