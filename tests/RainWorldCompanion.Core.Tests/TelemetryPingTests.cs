using RainWorldCompanion.Core.Telemetry;

namespace RainWorldCompanion.Tests;

public class TelemetryPingTests
{
    [Fact]
    public void The_endpoint_is_an_https_url()
    {
        Assert.StartsWith("https://", TelemetryPing.Endpoint);
        Assert.True(TelemetryPing.Enabled);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_endpoint_turns_the_feature_off(string endpoint)
        => Assert.False(TelemetryPing.IsEnabled(endpoint));

    [Fact]
    public void A_new_install_id_is_32_hex_characters()
    {
        var id = TelemetryPing.NewInstallId();

        Assert.Matches("^[0-9a-f]{32}$", id);
        Assert.NotEqual(id, TelemetryPing.NewInstallId());
    }

    [Fact]
    public void The_ping_url_carries_the_id_and_the_version()
    {
        var url = TelemetryPing.PingUrl(
            "https://telemetry.example.com",
            "0123456789abcdef0123456789abcdef",
            "1.2.0-beta.3");

        Assert.Equal(
            "https://telemetry.example.com/ping?id=0123456789abcdef0123456789abcdef&v=1.2.0-beta.3",
            url);
    }

    [Fact]
    public void The_version_is_escaped_on_the_way_into_the_query()
    {
        // "+" in a query string reads back as a space, and an alpha build stamp carries one.
        var url = TelemetryPing.PingUrl("https://t.example", "0123456789abcdef0123456789abcdef", "1.2.0+abc");

        Assert.EndsWith("&v=1.2.0%2Babc", url);
    }

    [Fact]
    public void A_trailing_slash_on_the_endpoint_does_not_double_up()
    {
        var url = TelemetryPing.PingUrl("https://t.example/", "0123456789abcdef0123456789abcdef", "1.2.0");

        Assert.StartsWith("https://t.example/ping?", url);
    }
}
