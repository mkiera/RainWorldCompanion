namespace RainWorldSaveManager.Core.Updates;

/// <summary>
/// One run of the branch-build workflow, as the actions API describes it.
/// </summary>
/// <param name="Id">The run id. Also what the download address is built from.</param>
/// <param name="WorkflowName">Checked, because the runs endpoint can be asked for more than one.</param>
/// <param name="HeadBranch">The branch. May hold a slash, so it never reaches a URL.</param>
/// <param name="HeadSha">The commit, full length.</param>
/// <param name="RunNumber">The workflow's own counter, which is what the version tail carries.</param>
/// <param name="Conclusion">"success" for a run that produced an artifact.</param>
/// <param name="CreatedUtc">When it started.</param>
public sealed record WorkflowRun(
    long Id,
    string WorkflowName,
    string HeadBranch,
    string HeadSha,
    int RunNumber,
    string Conclusion,
    DateTimeOffset? CreatedUtc);

/// <summary>
/// A branch build someone could install: one successful run, with somewhere to fetch it from.
/// </summary>
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

    /// <summary>"feature/updater #47", the line that names a row.</summary>
    public string Label => $"{Branch} #{RunNumber}";
}

/// <summary>
/// Turns workflow runs into the list of branch builds shown in the updates window.
///
/// These builds are never offered on their own. They exist so a branch can be handed to somebody
/// to try, which means the list is only ever reached by someone who went looking for it.
/// </summary>
public static class AlphaBuilds
{
    /// <summary>
    /// How many branches to list. The runs endpoint answers with far more than anyone wants to
    /// read, and every row was going to cost nothing extra only because the download address is
    /// derived rather than looked up. Eight is enough to cover what is in flight and keeps the
    /// list short enough to scan.
    /// </summary>
    public const int MaxBranches = 8;

    /// <summary>
    /// The newest successful run of each branch, newest branch first, capped at
    /// <see cref="MaxBranches"/>.
    ///
    /// Only successful runs: a failed or cancelled run uploaded no artifact, so its row would be
    /// a download that answers 404. Only this workflow's runs, because the endpoint can be handed
    /// a wider scope than intended.
    /// </summary>
    public static IReadOnlyList<AlphaBuild> FromRuns(
        IEnumerable<WorkflowRun> runs,
        BuildStamp running)
    {
        var newestPerBranch = new Dictionary<string, WorkflowRun>(StringComparer.OrdinalIgnoreCase);

        foreach (var run in runs)
        {
            if (!IsUsable(run))
            {
                continue;
            }

            // A branch gets rebuilt, so the same branch appears many times. The newest run is the
            // one worth showing; the older ones are the same branch at an earlier commit and
            // their artifacts expire first anyway.
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

    /// <summary>
    /// Whether a row is the copy the user is running.
    ///
    /// Run id first: a branch can be rebuilt at the same commit, and the run is what the
    /// installed copy actually came out of. The commit is the fallback, for a build stamped
    /// before it had a run to name. When neither is known nothing is marked, and the window says
    /// so rather than letting an unmarked list read as "you are on none of these".
    /// </summary>
    private static AlphaBuild ToBuild(WorkflowRun run, BuildStamp running)
    {
        bool isRunning;
        if (long.TryParse(running.RunId, out var stampedRun))
        {
            // Once there is a run id it is the whole answer, and the commit is not consulted at
            // all. A branch rebuilt at the same commit produces two runs carrying the same sha,
            // and falling through to the commit would mark both of them as the copy on disk.
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
