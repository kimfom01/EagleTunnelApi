using EagleTunnelApi.Telegram;
using Xunit;

namespace EagleTunnelApi.Tests.Telegram;

public class TelegramUpdateDeduplicatorTests
{
    [Fact]
    public void TryRegister_SameIdTwice_SecondIsRejected()
    {
        var deduplicator = new TelegramUpdateDeduplicator();

        Assert.True(deduplicator.TryRegister(10));
        Assert.False(deduplicator.TryRegister(10));
    }

    [Fact]
    public void TryRegister_DifferentIds_AreAccepted()
    {
        var deduplicator = new TelegramUpdateDeduplicator();

        Assert.True(deduplicator.TryRegister(10));
        Assert.True(deduplicator.TryRegister(11));
        Assert.True(deduplicator.TryRegister(12));
    }

    [Fact]
    public void TryRegister_NonPositiveId_IsAlwaysAccepted()
    {
        var deduplicator = new TelegramUpdateDeduplicator();

        Assert.True(deduplicator.TryRegister(0));
        Assert.True(deduplicator.TryRegister(0));
        Assert.True(deduplicator.TryRegister(-1));
    }

    [Fact]
    public void TryRegister_OldIdsAreEvictedAfterCapacity()
    {
        var deduplicator = new TelegramUpdateDeduplicator();

        for (var id = 1; id <= 1025; id++) Assert.True(deduplicator.TryRegister(id));

        Assert.False(deduplicator.TryRegister(1025));
        Assert.True(deduplicator.TryRegister(1));
    }
}
