namespace RainWorldCompanion.Core.Telemetry;

public static class TelemetryPing
{
    public const string Endpoint = "https://rwc-telemetry.kiera.pet";

    public static bool Enabled => IsEnabled(Endpoint);

    public static bool IsEnabled(string endpoint) => !string.IsNullOrWhiteSpace(endpoint);

    // "N" format: the receiving end accepts exactly 32 hex characters.
    public static string NewInstallId() => Guid.NewGuid().ToString("N");

    public static string PingUrl(string endpoint, string installId, string version) =>
        endpoint.TrimEnd('/')
        + "/ping?id=" + Uri.EscapeDataString(installId)
        + "&v=" + Uri.EscapeDataString(version);
}
