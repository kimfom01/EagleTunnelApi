using EagleTunnelApi.PanelApi.Models;
using EagleTunnelApi.Telegram;
using Xunit;

namespace EagleTunnelApi.Tests.Telegram;

public class SubscriptionStatusTests
{
    [Theory]
    [InlineData(false, 0, 0, 0, SubscriptionStatus.Disabled)]
    [InlineData(true, 0, 0, 0, SubscriptionStatus.Active)]
    [InlineData(true, 1000, 500, 0, SubscriptionStatus.Expired)]
    [InlineData(true, 0, 100, 50, SubscriptionStatus.Active)]
    [InlineData(true, 0, 100, 100, SubscriptionStatus.Limited)]
    public void DeriveStatus_GivenClientState_ReturnsExpected(bool enable, long expiryTime, long totalGbBytes,
        long usedTraffic, SubscriptionStatus expected)
    {
        var client = new PanelClient("uuid", "user@example.com", enable, expiryTime, 123, totalGbBytes, null, 0, 2, "monthly", 1, 0, null, "sub123", null, 1, null);

        var status = SubscriptionFormatter.DeriveStatus(client, usedTraffic);

        Assert.Equal(expected, status);
    }

    [Fact]
    public void GetStatusText_ReturnsExpectedLabels()
    {
        Assert.Equal("🔥 Subscription: Active", SubscriptionFormatter.GetStatusText(SubscriptionStatus.Active));
        Assert.Equal("📦 Subscription: Not Active", SubscriptionFormatter.GetStatusText(SubscriptionStatus.Disabled));
        Assert.Equal("🦥 Subscription: Limited", SubscriptionFormatter.GetStatusText(SubscriptionStatus.Limited));
        Assert.Equal("🧟 Subscription: Expired", SubscriptionFormatter.GetStatusText(SubscriptionStatus.Expired));
    }

    [Fact]
    public void FormatGigabytes_FormatsWithTwoDecimals()
    {
        Assert.Equal("2.00", SubscriptionFormatter.FormatGigabytes(2L * 1024 * 1024 * 1024));
        Assert.Equal("0.50", SubscriptionFormatter.FormatGigabytes(512L * 1024 * 1024));
    }

    [Fact]
    public void GetTrafficLimitStrategy_MapsReset()
    {
        Assert.Equal("RESET_EVERY_30_DAYS", SubscriptionFormatter.GetTrafficLimitStrategy(30));
        Assert.Equal("UNLIMITED", SubscriptionFormatter.GetTrafficLimitStrategy(0));
    }

    [Theory]
    [InlineData("https://panel.example.com", "sub123", "https://panel.example.com:2096/add/sub123")]
    [InlineData("https://panel.example.com/", "sub456", "https://panel.example.com:2096/add/sub456")]
    public void BuildSubscriptionUrl_UsesOriginWithHardcodedPort(string baseUri, string subId, string expected)
    {
        var url = SubscriptionFormatter.BuildSubscriptionUrl(baseUri, subId);

        Assert.Equal(expected, url);
    }

    [Fact]
    public void UserDetails_FromResponse_MapsFields()
    {
        var expiryMs = DateTimeOffset.UtcNow.AddDays(10).ToUnixTimeMilliseconds();

        var client = new PanelClient("uuid-x", "user@example.com", true, expiryMs, 123,
            100L * 1024 * 1024 * 1024, "c", 3, 2, "monthly", 1, 30, "auto", "sub777", "xtls-rprx-vision", 7, null);
        var response = new PanelClientResponse(client, null, new List<int> { 1 }, 42L * 1024 * 1024);

        var details = UserDetails.From(response, "https://panel.example.com");

        Assert.NotNull(details);
        Assert.Equal("uuid-x", details.Uuid);
        Assert.Equal(7, details.Id);
        Assert.Equal("sub777", details.SubId);
        Assert.Equal("user@example.com", details.Username);
        Assert.Equal(SubscriptionStatus.Active, details.Status);
        Assert.Equal(100L * 1024 * 1024 * 1024, details.TrafficLimitBytes);
        Assert.Equal("RESET_EVERY_30_DAYS", details.TrafficLimitStrategy);
        Assert.Equal("https://panel.example.com:2096/add/sub777", details.SubscriptionUrl);
        Assert.Equal(42L * 1024 * 1024, details.UsedTrafficBytes);
        Assert.Equal(2, details.HwidDeviceLimit);
        Assert.Equal("monthly", details.TrafficReset);
        Assert.Equal(1, details.TrafficResetDay);
        Assert.Equal(123, details.TelegramId);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(expiryMs).ToUniversalTime(),
            details.ExpireAt);
    }

    [Fact]
    public void UserDetails_FromNull_ReturnsNull()
    {
        Assert.Null(UserDetails.From(null, "https://panel.example.com"));
    }
}