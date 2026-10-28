using EagleTunnelApi.Application.Panel;

namespace EagleTunnelApi.Telegram;

/// <summary>
/// Checks at startup that every effective default inbound id still exists on
/// the panel. Panel upgrades can renumber or remove inbounds; the new add
/// endpoint rejects the whole create call when any requested inbound is
/// missing, so a stale id silently breaks all new provisioning.
/// </summary>
public sealed class PanelInboundPreflightService(
    IPanelGateway panelClient,
    IPanelDirectory panelDirectory,
    ILogger<PanelInboundPreflightService> logger) : BackgroundService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return CheckAsync(stoppingToken);
    }

    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        try
        {
            var effective = await panelDirectory.GetDefaultInboundIdsAsync(timeout.Token);

            if (effective.Count == 0) return;

            var available = (await panelClient.GetInboundOptionsAsync(timeout.Token))
                .Select(inbound => inbound.Id)
                .Distinct()
                .Order()
                .ToArray();

            var missing = effective
                .Where(id => !available.Contains(id))
                .Distinct()
                .Order()
                .ToArray();

            if (missing.Length > 0)
                logger.LogCritical(
                    "Panel inbound preflight failed: the effective default inbound ids reference inbound id(s) " +
                    "{Missing} that do not exist on the panel. Available inbound ids: {Available}. " +
                    "Update /admin -> Panel Management -> Default Inbounds (or Telegram:DefaultInboundIds). " +
                    "New client creation will fail until this is corrected.",
                    string.Join(", ", missing), string.Join(", ", available));
            else
                logger.LogInformation(
                    "Panel inbound preflight passed. Effective default inbound ids: {InboundIds}",
                    string.Join(", ", effective));
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
