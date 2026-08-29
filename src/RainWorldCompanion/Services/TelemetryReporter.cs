using System.Net.Http;
using RainWorldCompanion.Core.Telemetry;
using RainWorldCompanion.Core.Updates;

namespace RainWorldCompanion.Services;

public sealed class TelemetryReporter : ITelemetryReporter, IDisposable
{
    private readonly HttpClient _client;

    public TelemetryReporter(string appVersion)
    {
        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _client.DefaultRequestHeaders.Add("User-Agent", UpdateUrls.UserAgent(appVersion));
    }

    public async Task SendAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _client.GetAsync(url, cancellationToken);
        }
        catch (Exception)
        {
            // Nothing here is worth a word to the user, a retry or a log entry.
        }
    }

    public void Dispose() => _client.Dispose();
}
