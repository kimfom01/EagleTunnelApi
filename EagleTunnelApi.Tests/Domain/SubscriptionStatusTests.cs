using EagleTunnelApi.Domain.Subscriptions;
using Xunit;

namespace EagleTunnelApi.Tests.Domain;

public class SubscriptionStatusTests
{
    [Theory]
    [InlineData(false, 0, 0, 0, SubscriptionStatus.Disabled)]
    [InlineData(true, 0, 0, 0, SubscriptionStatus.Active)]
    [InlineData(true, 1000, 500, 0, SubscriptionStatus.Expired)]
    [InlineData(true, 0, 100, 50, SubscriptionStatus.Active)]
    [InlineData(true, 0, 100, 100, SubscriptionStatus.Limited)]
    public void DeriveStatus_GivenSubscriptionState_ReturnsExpected(bool enable, long expiryTime, long totalBytes,
        long usedTraffic, SubscriptionStatus expected)
    {
        var status = SubscriptionFormatter.DeriveStatus(enable, expiryTime, totalBytes, usedTraffic,
            DateTimeOffset.UtcNow);

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
    [InlineData("https://panel.example.com:8443/admin", "sub789", "https://panel.example.com:2096/add/sub789")]
    public void BuildSubscriptionUrl_UsesOriginWithHardcodedPort(string baseUri, string subId, string expected)
    {
        var url = SubscriptionFormatter.BuildSubscriptionUrl(baseUri, subId);

        Assert.Equal(expected, url);
    }
}
