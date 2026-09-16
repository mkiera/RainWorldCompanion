using RainWorldCompanion.LiveProtocol;
using RWCompanion.Mod;

namespace RainWorldCompanion.Core.Tests;

public sealed class MeadowDeepTraceAdvertisementTests
{
    private const string HostId = "76561198176210419";
    private const string ClientId = "76561198175665721";
    private const string OtherId = "76561198170000000";

    [Theory]
    [InlineData(false, true, true, true, false)]
    [InlineData(false, false, false, false, false)]
    [InlineData(true, false, true, true, true)]
    [InlineData(true, true, true, true, true)]
    public void Existing_wire_flag_is_selected_for_each_destination(bool manual, bool targeted,
        bool expectedHost, bool expectedClient, bool expectedOther)
    {
        string[] targets = targeted ? [HostId, ClientId] : [];
        Assert.Equal(expectedHost, ReadDeepFlag(manual, targets, HostId));
        Assert.Equal(expectedClient, ReadDeepFlag(manual, targets, ClientId));
        Assert.Equal(expectedOther, ReadDeepFlag(manual, targets, OtherId));
        Assert.Equal(expectedOther ? 11 : 3, LogAdvertisement.Flags(true, true, false, manual, targets, OtherId));
    }

    [Fact]
    public void Unavailable_receiver_cannot_request_targeted_trace()
    {
        Assert.False(LogAdvertisement.IsValid(new() { DeepTracePeerIds = [ClientId] }));
        Assert.True(LogAdvertisement.IsValid(new()));
        Assert.True(LogAdvertisement.IsValid(new()
        {
            Available = true, CaptureActive = true, CaptureId = "capture", CaptureToken = "token",
            DeepTracePeerIds = [ClientId]
        }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("76561198175665721x")]
    [InlineData("１２３")]
    [InlineData("123456789012345678901234567890123")]
    [InlineData(null)]
    public void Invalid_target_ids_are_rejected(string? id)
    {
        Assert.False(LogAdvertisement.ValidDeepTracePeerIds([id!]));
        Assert.False(LogAdvertisement.IsValid(new()
        {
            Available = true, CaptureActive = true, CaptureId = "capture", CaptureToken = "token",
            DeepTracePeerIds = [id!]
        }));
    }

    [Fact]
    public void Target_list_has_a_fixed_bound()
    {
        Assert.False(LogAdvertisement.ValidDeepTracePeerIds(Enumerable.Repeat(ClientId, 33).ToArray()));
        Assert.True(LogAdvertisement.ValidDeepTracePeerIds(Enumerable.Repeat(ClientId, 32).ToArray()));
        Assert.False(LogAdvertisement.ValidDeepTracePeerIds(null));
    }

    [Fact]
    public void Bridge_returns_an_independent_target_list()
    {
        var original = new LogReceiverAdvertisement
        {
            Available = true, CaptureActive = true, CaptureId = "capture", CaptureToken = "token",
            DeepTracePeerIds = [ClientId]
        };
        var advertisement = LogAdvertisement.Clone(original);
        advertisement.DeepTracePeerIds[0] = OtherId;
        Assert.Equal([ClientId], original.DeepTracePeerIds);
    }

    [Fact]
    public void Older_bridge_advertisements_default_to_no_targeted_requests()
    {
        var advertisement = LiveJson.Deserialize<LogReceiverAdvertisement>("{\"Available\":true,\"DeepTraceEnabled\":true}");
        Assert.NotNull(advertisement);
        Assert.Empty(advertisement.DeepTracePeerIds);
        var roundTrip = LiveJson.Deserialize<LogReceiverAdvertisement>(LiveJson.Serialize(new LogReceiverAdvertisement
        {
            Available = true, DeepTracePeerIds = [ClientId]
        }));
        Assert.Equal([ClientId], roundTrip!.DeepTracePeerIds);
    }

    private static bool ReadDeepFlag(bool manual, string[] targets, string destination) =>
        (LogAdvertisement.Flags(true, true, false, manual, targets, destination) & 8) != 0;
}
