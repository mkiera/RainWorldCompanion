using RainWorldCompanion.LiveProtocol;

namespace RainWorldCompanion.Tests;

public sealed class LogBridgePacingTests
{
    [Theory]
    [InlineData(0, 0, 0, 100)]
    [InlineData(1, 1, 0, 100)]
    [InlineData(6, 0, 0, 25)]
    [InlineData(0, 6, 0, 25)]
    [InlineData(0, 0, 1, 25)]
    public void Pacing_accelerates_full_batches_and_waiting_packets(
        int received, int outgoing, int remaining, int expectedDelay)
        => Assert.Equal(expectedDelay, LogBridgePacing.GetDelayMilliseconds(received, outgoing, remaining));

    [Theory]
    [InlineData(6)]
    [InlineData(10)]
    public void Shared_bridge_keeps_up_with_ten_packets_per_second_per_sender(int senders)
    {
        const int durationMilliseconds = 60_000;
        const int exchangeWorkMilliseconds = 10;
        var pending = new Queue<int>();
        int nextExchange = 0;
        int received = 0;
        int delivered = 0;
        int dropped = 0;
        int maximumLatency = 0;
        for (int now = 0; now < durationMilliseconds + 1000; now++)
        {
            if (now < durationMilliseconds && now % 100 == 0)
            {
                for (int sender = 0; sender < senders; sender++)
                {
                    received++;
                    if (pending.Count >= 128) dropped++;
                    else pending.Enqueue(now);
                }
            }
            if (now < nextExchange) continue;
            int count = Math.Min(ProtocolInfo.MaximumLogPacketsPerBridgeExchange, pending.Count);
            for (int packet = 0; packet < count; packet++)
            {
                maximumLatency = Math.Max(maximumLatency, now - pending.Dequeue());
                delivered++;
            }
            nextExchange = now + exchangeWorkMilliseconds
                + LogBridgePacing.GetDelayMilliseconds(count, count, pending.Count);
        }

        Assert.Equal(0, dropped);
        Assert.Equal(senders * 600, delivered);
        Assert.Equal(received, delivered);
        Assert.Empty(pending);
        Assert.InRange(maximumLatency, 0, 200);
    }
}
