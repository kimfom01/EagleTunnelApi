using EagleTunnelApi.Telegram;
using EagleTunnelApi.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EagleTunnelApi.Tests.Telegram;

public class ReferralSettlementRunnerTests
{
    [Fact]
    public async Task TryStart_WhileRunning_ReturnsNullAndDoesNotRunAgain()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new StubAdminPanelService
        {
            SettleHandler = async cancellationToken =>
            {
                await release.Task.WaitAsync(cancellationToken);
                return 0;
            }
        };
        var runner = new ReferralSettlementRunner(service, new TestHostApplicationLifetime(),
            NullLogger<ReferralSettlementRunner>.Instance);

        var first = runner.TryStart();

        Assert.NotNull(first);
        Assert.Null(runner.TryStart());
        Assert.Equal(1, service.SettleCalls);

        release.SetResult(true);

        var result = await first!;

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.Settled);
    }

    [Fact]
    public async Task TryStart_AfterCompletion_AllowsAnotherRun()
    {
        var service = new StubAdminPanelService();
        var runner = new ReferralSettlementRunner(service, new TestHostApplicationLifetime(),
            NullLogger<ReferralSettlementRunner>.Instance);

        var first = await runner.TryStart()!;

        Assert.True(first.Succeeded);

        service.SettleHandler = _ => Task.FromResult(3);

        var second = await runner.TryStart()!;

        Assert.True(second.Succeeded);
        Assert.Equal(3, second.Settled);
        Assert.Equal(2, service.SettleCalls);
    }

    [Fact]
    public async Task TryStart_WhenSweepThrows_ReturnsFailedResultAndReleasesGate()
    {
        var service = new StubAdminPanelService
        {
            SettleHandler = _ => throw new InvalidOperationException("panel down")
        };
        var runner = new ReferralSettlementRunner(service, new TestHostApplicationLifetime(),
            NullLogger<ReferralSettlementRunner>.Instance);

        var result = await runner.TryStart()!;

        Assert.False(result.Succeeded);
        Assert.Equal(0, result.Settled);

        service.SettleHandler = _ => Task.FromResult(1);

        var retry = await runner.TryStart()!;

        Assert.True(retry.Succeeded);
        Assert.Equal(1, retry.Settled);
    }

    [Fact]
    public async Task TryStart_WhenApplicationStops_CancelsTheSweep()
    {
        var lifetime = new TestHostApplicationLifetime();
        var service = new StubAdminPanelService
        {
            SettleHandler = async cancellationToken =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }
        };
        var runner = new ReferralSettlementRunner(service, lifetime,
            NullLogger<ReferralSettlementRunner>.Instance);

        var run = runner.TryStart();

        Assert.NotNull(run);

        lifetime.StopApplication();

        var result = await run!;

        Assert.False(result.Succeeded);
        Assert.Equal(0, result.Settled);
    }
}
