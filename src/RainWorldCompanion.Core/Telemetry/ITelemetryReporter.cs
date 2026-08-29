namespace RainWorldCompanion.Core.Telemetry;

public interface ITelemetryReporter
{
    // Never faults, whatever the network does.
    Task SendAsync(string url, CancellationToken cancellationToken);
}
