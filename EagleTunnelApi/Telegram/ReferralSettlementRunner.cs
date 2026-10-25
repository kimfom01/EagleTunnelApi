namespace EagleTunnelApi.Telegram;

public sealed record ReferralSettlementResult(bool Succeeded, int Settled);

/// <summary>
/// Serializes referral settlement sweeps. The daily backstop, the admin button,
/// and any Telegram redelivery share one gate, so a slow sweep can never be
/// started twice concurrently.
/// </summary>
public sealed class ReferralSettlementRunner(
    IAdminPanelService adminPanelService,
    IHostApplicationLifetime lifetime,
    ILogger<ReferralSettlementRunner> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Starts a sweep when none is running. Returns <c>null</c> when a sweep is
    /// already in progress. The returned task never faults: failures are logged
    /// and reported through <see cref="ReferralSettlementResult.Succeeded"/>.
    /// </summary>
    public Task<ReferralSettlementResult>? TryStart()
    {
        if (!_gate.Wait(0)) return null;

        return RunAsync();
    }

    private async Task<ReferralSettlementResult> RunAsync()
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);

        try
        {
            var settled = await adminPanelService.SettlePendingReferralBonusesAsync(linked.Token);

            logger.LogInformation("Referral settlement pass complete. Settled: {Settled}", settled);

            return new ReferralSettlementResult(true, settled);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            logger.LogInformation("Referral settlement pass cancelled.");

            return new ReferralSettlementResult(false, 0);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Referral settlement pass failed.");

            return new ReferralSettlementResult(false, 0);
        }
        finally
        {
            _gate.Release();
        }
    }
}
