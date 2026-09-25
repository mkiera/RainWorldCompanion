using RainWorldCompanion.LiveProtocol;

namespace RainWorldCompanion.Core.Live;

public sealed record LiveDiagnostics(
    LiveConnectionStatus Status, LiveSnapshot? Snapshot, int? Port, string DiscoveryDirectory,
    long Connections, long Messages, long Accepted, long Rejected, long Ignored, long Bytes,
    DateTimeOffset? LastReceived, DateTimeOffset? LastAccepted, string LastEvent,
    string? ObservedModVersion, int? ObservedProtocol);
