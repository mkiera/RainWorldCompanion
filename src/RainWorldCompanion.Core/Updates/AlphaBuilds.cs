namespace RainWorldCompanion.Core.Updates;

/// <param name="HeadBranch">May hold a slash, so it never reaches a URL.</param>
public sealed record WorkflowRun(
    long Id,
    string WorkflowName,
    string HeadBranch,
    string HeadSha,
    int RunNumber,
    string Conclusion,
    DateTimeOffset? CreatedUtc);

public sealed record AlphaBuild(
    long RunId,
    string Branch,
    string Sha,
    int RunNumber,
    DateTimeOffset? CreatedUtc,
    string DownloadUrl,
    string RunUrl,
    bool IsRunningCopy)
{
    public string ShortSha => Sha.Length <= 7 ? Sha : Sha[..7];

    public string Label => $"{Branch} #{RunNumber}";
}

public static class AlphaBuilds
{
    public const int MaxBranches = 8;

    /// <summary>
    /// The newest successful run of each branch, newest first, capped at
    /// <see cref="MaxBranches"/>. A failed or cancelled run uploaded no artifact, so its row would
    /// be a download that answers 404.
    /// </summary>
    /// <param name="liveBranches">
    /// Branches still on the remote. A run outlives the branch it came from, so without this the
    /// list keeps offering branches merged and deleted long ago. Empty means the branches could
    /// not be fetched, and every run is kept rather than the list emptying itself over a failure.
    /// </param>
    public static IReadOnlyList<AlphaBuild> FromRuns(
        IEnumerable<WorkflowRun> runs,
        BuildStamp running,
        IEnumerable<string>? liveBranches = null)
    {
        var live = liveBranches is null
            ? null
            : new HashSet<string>(
                liveBranches.Where(b => !string.IsNullOrWhiteSpace(b)),
                StringComparer.OrdinalIgnoreCase);

        if (live is { Count: 0 })
        {
            live = null;
        }

        var newestPerBranch = new Dictionary<string, WorkflowRun>(StringComparer.OrdinalIgnoreCase);

        foreach (var run in runs)
        {
            if (!IsUsable(run))
            {
                continue;
            }

            if (live is not null && !live.Contains(run.HeadBranch))
            {
                continue;
            }

            // A branch gets rebuilt, so the same branch appears many times.
            if (!newestPerBranch.TryGetValue(run.HeadBranch, out var seen) || IsNewer(run, seen))
            {
                newestPerBranch[run.HeadBranch] = run;
            }
        }

        return newestPerBranch.Values
            .OrderByDescending(run => run.RunNumber)
            .Take(MaxBranches)
            .Select(run => ToBuild(run, running))
            .ToList();
    }

    private static bool IsUsable(WorkflowRun? run)
    {
        if (run is null || run.Id <= 0)
        {
            return false;
        }

        if (!string.Equals(run.Conclusion, "success", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(run.HeadBranch);
    }

    private static bool IsNewer(WorkflowRun candidate, WorkflowRun seen)
        => candidate.RunNumber != seen.RunNumber
            ? candidate.RunNumber > seen.RunNumber
            : candidate.Id > seen.Id;

    private static AlphaBuild ToBuild(WorkflowRun run, BuildStamp running)
    {
        bool isRunning;
        if (long.TryParse(running.RunId, out var stampedRun))
        {
            // A branch rebuilt at the same commit produces two runs carrying the same sha, so
            // falling through to the commit would mark both of them as the copy on disk.
            isRunning = stampedRun == run.Id;
        }
        else
        {
            isRunning = running.CommitSha.Length != 0
                && run.HeadSha.Length != 0
                && string.Equals(running.CommitSha, run.HeadSha, StringComparison.OrdinalIgnoreCase);
        }

        return new AlphaBuild(
            run.Id,
            run.HeadBranch,
            run.HeadSha,
            run.RunNumber,
            run.CreatedUtc,
            UpdateUrls.BranchBuildZip(run.Id),
            UpdateUrls.BranchBuildPage(run.Id),
            isRunning);
    }
}
