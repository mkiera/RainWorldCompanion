using RainWorldCompanion.LiveProtocol;

namespace RWCompanion.Mod;

internal static class LogAdvertisement
{
    internal static bool ValidDeepTracePeerIds(string[]? peerIds) => peerIds is { Length: <= 32 }
        && peerIds.All(id => id is { Length: > 0 and <= 32 } && id.All(character => character is >= '0' and <= '9'));

    internal static byte Flags(bool available, bool active, bool paused, bool manualDeepTrace,
        string[] deepTracePeerIds, string destinationSteamId) =>
        (byte)((available ? 1 : 0) | (active ? 2 : 0) | (paused ? 4 : 0)
            | (manualDeepTrace || deepTracePeerIds.Contains(destinationSteamId, StringComparer.Ordinal) ? 8 : 0));

    internal static bool IsValid(LogReceiverAdvertisement? advertisement) => advertisement is not null
        && advertisement.CaptureId is { Length: <= 64 }
        && advertisement.CaptureToken is { Length: <= 128 }
        && !(advertisement.CaptureActive && advertisement.CapturePaused)
        && ValidDeepTracePeerIds(advertisement.DeepTracePeerIds)
        && (advertisement.Available
            ? advertisement.CaptureId.Length > 0 && advertisement.CaptureToken.Length > 0
            : !advertisement.CaptureActive && !advertisement.CapturePaused && !advertisement.DeepTraceEnabled
                && advertisement.DeepTracePeerIds.Length == 0 && advertisement.CaptureId.Length == 0
                && advertisement.CaptureToken.Length == 0);

    internal static LogReceiverAdvertisement Clone(LogReceiverAdvertisement advertisement) => new()
    {
        Available = advertisement.Available,
        CaptureActive = advertisement.CaptureActive,
        CapturePaused = advertisement.CapturePaused,
        DeepTraceEnabled = advertisement.DeepTraceEnabled,
        DeepTracePeerIds = advertisement.DeepTracePeerIds.ToArray(),
        CaptureId = advertisement.CaptureId ?? "",
        CaptureToken = advertisement.CaptureToken ?? ""
    };
}
