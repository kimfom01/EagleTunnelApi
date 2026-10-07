using EagleTunnelApi.Telegram;
using Xunit;

namespace EagleTunnelApi.Tests.Telegram;

public class SubscriptionProvisionerTests
{
    private const long DayMs = 24L * 60 * 60 * 1000;
    private const long ToleranceMs = 5L * 60 * 1000;

    [Fact]
    public void RebaseWhenExpired_ExpiredSevenDaysAgo_RebasesPaidDaysOntoNow()
    {
        var now = DateTimeOffset.UtcNow;
        var oldExpiryMs = now.AddDays(-7).ToUnixTimeMilliseconds();
        var tributeExpiryMs = now.AddDays(-7).AddDays(30).ToUnixTimeMilliseconds();

        var result = SubscriptionProvisioner.RebaseWhenExpired(oldExpiryMs, tributeExpiryMs, 0);

        var expected = now.AddDays(30).ToUnixTimeMilliseconds();
        Assert.InRange(result, expected - ToleranceMs, expected + ToleranceMs);
    }

    [Fact]
    public void RebaseWhenExpired_ActiveClient_KeepsTributeExpiry()
    {
        var now = DateTimeOffset.UtcNow;
        var oldExpiryMs = now.AddDays(5).ToUnixTimeMilliseconds();
        var tributeExpiryMs = now.AddDays(35).ToUnixTimeMilliseconds();

        var result = SubscriptionProvisioner.RebaseWhenExpired(oldExpiryMs, tributeExpiryMs, 0);

        Assert.Equal(tributeExpiryMs, result);
    }

    [Fact]
    public void RebaseWhenExpired_PlaceholderOldExpiry_KeepsTributeExpiry()
    {
        var tributeExpiryMs = new DateTimeOffset(2026, 1, 28, 11, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var result = SubscriptionProvisioner.RebaseWhenExpired(1, tributeExpiryMs, 0);

        Assert.Equal(tributeExpiryMs, result);
    }

    [Fact]
    public void RebaseWhenExpired_ExpiredWithCredit_PreservesCreditOnTop()
    {
        var now = DateTimeOffset.UtcNow;
        var oldExpiryMs = now.AddDays(-7).ToUnixTimeMilliseconds();
        var tributeExpiryMs = now.AddDays(-7).AddDays(30).ToUnixTimeMilliseconds();
        var creditMs = 30 * DayMs;

        var result = SubscriptionProvisioner.RebaseWhenExpired(oldExpiryMs, tributeExpiryMs, creditMs);

        var expected = now.AddDays(60).ToUnixTimeMilliseconds();
        Assert.InRange(result, expected - ToleranceMs, expected + ToleranceMs);
    }

    [Fact]
    public void RebaseWhenExpired_MissingOldExpiry_KeepsTributeExpiry()
    {
        var tributeExpiryMs = new DateTimeOffset(2026, 1, 28, 11, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var result = SubscriptionProvisioner.RebaseWhenExpired(0, tributeExpiryMs, 0);

        Assert.Equal(tributeExpiryMs, result);
    }
}
