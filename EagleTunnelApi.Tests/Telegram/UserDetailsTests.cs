using EagleTunnelApi.Application.Panel;
using EagleTunnelApi.Domain.Subscriptions;
using EagleTunnelApi.Telegram;
using Xunit;

namespace EagleTunnelApi.Tests.Telegram;

public class UserDetailsTests
{
    [Fact]
    public void FromResponse_MapsFields()
    {
        var expiryMs = DateTimeOffset.UtcNow.AddDays(10).ToUnixTimeMilliseconds();

        var client = new PanelClient("uuid-x", "user@example.com", true, expiryMs, 123,
            100L * 1024 * 1024 * 1024, "c", 3, 2, "monthly", 1, 30, "auto", "sub777", "xtls-rprx-vision", 7, null);
        var response = new PanelClientDetails(client, null, new List<int> { 1 }, 42L * 1024 * 1024);

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
    public void FromNull_ReturnsNull()
    {
        Assert.Null(UserDetails.From(null, "https://panel.example.com"));
    }
}
