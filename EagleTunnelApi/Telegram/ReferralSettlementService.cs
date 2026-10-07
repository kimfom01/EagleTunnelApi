using EagleTunnelApi.Configuration;
using EagleTunnelApi.Telegram;
using Microsoft.Extensions.Options;

namespace EagleTunnelApi.Telegram;

/// <summary>
/// Daily settlement for referrals activated outside the webhook flow.
/// When support extends a subscription directly in the 3X-UI dashboard,
/// no Tribute event fires — this sweep pays any referral bonus that is
/// still outstanding for accounts that converted to paid access.
/// Webhook and bot-grant activations settle instantly; this is the backstop.
/// </summary>
public sealed class ReferralSettlementService(
    IAdminPanelService adminPanelService,
    IOptions<TelegramOptions> telegramOptions,
    ILogger<ReferralSettlementService> logger) : BackgroundService
{
    private readonly TelegramOptions _telegramOptions = telegramOptions.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = ExpiryReminderService.TimeUntilNextRun(DateTime.UtcNow, _telegramOptions.ReminderHourUtc);

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                var settled = await adminPanelService.SettlePendingReferralBonusesAsync(stoppingToken);
                logger.LogInformation("Referral settlement pass complete. Settled: {Settled}", settled);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Referral settlement pass failed.");
            }
        }
    }
}
