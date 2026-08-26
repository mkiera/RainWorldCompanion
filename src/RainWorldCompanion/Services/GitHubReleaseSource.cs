using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using RainWorldCompanion.Core.Updates;

namespace RainWorldCompanion.Services;

/// <summary>
/// The only thing in this project that makes a network request. Every error becomes a sentence
/// meant to be shown as it is, because the caller knows no more about what went wrong than this.
/// </summary>
public sealed class GitHubReleaseSource : IReleaseSource, IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _client;

    public GitHubReleaseSource(string appVersion)
    {
        _client = new HttpClient(new SocketsHttpHandler
        {
            // The list is small, so a whole-request timeout is right here, unlike the download.
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        _client.DefaultRequestHeaders.Add("User-Agent", UpdateUrls.UserAgent(appVersion));
        _client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        _client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<IReadOnlyList<ReleaseCandidate>> GetReleasesAsync(CancellationToken cancellationToken)
    {
        // per_page, because a release that falls off the default page of thirty is one nobody can
        // go back to.
        var payload = await GetAsync<List<GhRelease>>(
            UpdateUrls.Releases + "?per_page=100", cancellationToken);

        return payload is null
            ? []
            : payload.Where(r => r is not null).Select(r => r.ToCandidate()).ToList();
    }

    public async Task<IReadOnlyList<WorkflowRun>> GetBranchBuildRunsAsync(CancellationToken cancellationToken)
    {
        // Filtered server side, so the failed runs of one branch cannot crowd the successful runs
        // of others out of the page.
        var payload = await GetAsync<GhRunsPage>(
            UpdateUrls.BranchBuildRuns + "?status=success&per_page=50", cancellationToken);

        return payload?.WorkflowRuns is null
            ? []
            : payload.WorkflowRuns.Where(r => r is not null).Select(r => r.ToRun()).ToList();
    }

    private async Task<T?> GetAsync<T>(string url, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _client.GetAsync(url, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            throw new UpdateCheckException("Could not reach GitHub to check for updates.", e);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateCheckException(DescribeStatus(response.StatusCode));
            }

            try
            {
                var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                return await JsonSerializer.DeserializeAsync<T>(stream, Json, cancellationToken);
            }
            catch (Exception e) when (e is JsonException or HttpRequestException or InvalidOperationException)
            {
                throw new UpdateCheckException("GitHub's answer could not be read.", e);
            }
        }
    }

    private static string DescribeStatus(HttpStatusCode status) => (int)status switch
    {
        403 or 429 => "GitHub is rate limiting update checks right now. Try again in a little while.",
        404 => "The releases could not be found on GitHub.",
        >= 500 => "GitHub is having trouble right now. Try again in a little while.",
        _ => $"GitHub answered the update check with HTTP {(int)status}.",
    };

    public void Dispose() => _client.Dispose();

    private sealed class GhAsset
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? DownloadUrl { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }

        public ReleaseAsset ToAsset() => new(Name ?? "", DownloadUrl ?? "", Size);
    }

    private sealed class GhRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; set; }
        [JsonPropertyName("assets")] public List<GhAsset>? Assets { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }

        public ReleaseCandidate ToCandidate() => new(
            TagName ?? "",
            HtmlUrl ?? UpdateUrls.ReleasesPage,
            Draft,
            Prerelease,
            PublishedAt,
            Assets?.Where(a => a is not null).Select(a => a.ToAsset()).ToList() ?? [],
            Body ?? "");
    }

    private sealed class GhRun
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("head_branch")] public string? HeadBranch { get; set; }
        [JsonPropertyName("head_sha")] public string? HeadSha { get; set; }
        [JsonPropertyName("run_number")] public int RunNumber { get; set; }
        [JsonPropertyName("conclusion")] public string? Conclusion { get; set; }
        [JsonPropertyName("created_at")] public DateTimeOffset? CreatedAt { get; set; }

        public WorkflowRun ToRun() => new(
            Id, Name ?? "", HeadBranch ?? "", HeadSha ?? "", RunNumber, Conclusion ?? "", CreatedAt);
    }

    private sealed class GhRunsPage
    {
        [JsonPropertyName("workflow_runs")] public List<GhRun>? WorkflowRuns { get; set; }
    }
}

/// <summary>The message is a whole sentence, written to be shown to the user as it is.</summary>
public sealed class UpdateCheckException(string message, Exception? inner = null)
    : Exception(message, inner);
