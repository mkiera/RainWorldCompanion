namespace RainWorldCompanion.LiveProtocol;

public static class LogBridgePacing
{
    public static int GetDelayMilliseconds(int receivedCount, int outgoingCount, int remainingReceivedCount)
        => remainingReceivedCount > 0
            || receivedCount >= ProtocolInfo.MaximumLogPacketsPerBridgeExchange
            || outgoingCount >= ProtocolInfo.MaximumLogPacketsPerBridgeExchange
                ? 25
                : 100;
}
