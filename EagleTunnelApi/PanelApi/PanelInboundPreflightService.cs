using EagleTunnelApi.Configuration;
using Microsoft.Extensions.Options;

namespace EagleTunnelApi.PanelApi;

/// <summary>
/// Checks at startup that every configured default inbound id still exists on
/// the panel. Panel upgrades can renumber or remove inbounds; the new add
/// endpoint rejects the whole create call when any requested inbound is
/// missing, so a stale id silently breaks all new provisioning.
/// </summary>
public sealed class PanelInboundPreflightService(
    IPanelClient panelClient,
    IOptions<TelegramOptions> telegramOptions,
    ILogger<PanelInboundPreflightService> logger) : BackgroundService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return CheckAsync(stoppingToken);
    }

    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        var configured = telegramOptions.Value.DefaultInboundIds;

        if (configured.Length == 0) return;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        try
        {
            var available = (await panelClient.GetInboundOptionsAsync(timeout.Token))
                .Select(inbound => inbound.Id)
                .Distinct()
                .Order()
                .ToArray();

            var missing = configured
                .Where(id => !available.Contains(id))
                .Distinct()
                .Order()
                .ToArray();

            if (missing.Length > 0)
                logger.LogCritical(
                    "Panel inbound preflight failed: Telegram:DefaultInboundIds references inbound id(s) " +
                    "{Missing} that do not exist on the panel. Available inbound ids: {Available}. " +
                    "New client creation will fail until this is corrected.",
                    string.Join(", ", missing), string.Join(", ", available));
            else
                logger.LogInformation(
                    "Panel inbound preflight passed. Configured inbound ids: {InboundIds}",
                    string.Join(", ", configured));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The host is shutting down; no need to warn about a cancelled probe.
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            logger.LogWarning(
                "Panel inbound preflight timed out after {Seconds} seconds; continuing startup.",
                Timeout.TotalSeconds);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Panel inbound preflight failed; continuing startup.");
        }
    }
}
