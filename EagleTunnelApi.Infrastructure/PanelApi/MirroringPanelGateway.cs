using EagleTunnelApi.Application.Panel;
using Microsoft.Extensions.Logging;

namespace EagleTunnelApi.Infrastructure.PanelApi;

/// <summary>
/// Mirrors successful app-initiated panel writes into Postgres. The panel write
/// always happens first and remains authoritative; mirror failures are logged
/// and left for the next sync to reconcile.
/// </summary>
public sealed class MirroringPanelGateway(
    IPanelGateway inner,
    IPanelMirrorWriter mirrorWriter,
    ILogger<MirroringPanelGateway> logger) : IPanelGateway
{
    public Task<PanelClientDetails?> GetClientByTelegramIdAsync(long tgId,
        CancellationToken cancellationToken) => inner.GetClientByTelegramIdAsync(tgId, cancellationToken);

    public Task<PanelClientDetails?> GetClientByEmailAsync(string email,
        CancellationToken cancellationToken) => inner.GetClientByEmailAsync(email, cancellationToken);

    public async Task AddClientAsync(PanelClientCreate client, CancellationToken cancellationToken)
    {
        await inner.AddClientAsync(client, cancellationToken);
        await MirrorAsync(client.Email, "create", cancellationToken);
    }

    public async Task UpdateClientAsync(PanelClientUpdate request, CancellationToken cancellationToken)
    {
        await inner.UpdateClientAsync(request, cancellationToken);
        await MirrorAsync(request.Email, "update", cancellationToken);
    }

    public async Task UpdateClientAsync(string keyEmail, PanelClientUpdate request,
        CancellationToken cancellationToken)
    {
        await inner.UpdateClientAsync(keyEmail, request, cancellationToken);
        await MirrorAsync(request.Email, "update", cancellationToken);
    }

    public async Task BulkDisableClientsAsync(IEnumerable<string> emails,
        CancellationToken cancellationToken)
    {
        var addresses = emails.ToArray();

        await inner.BulkDisableClientsAsync(addresses, cancellationToken);
        await MirrorManyAsync(NormalizeEmails(addresses), "disable", cancellationToken);
    }

    public async Task BulkEnableClientsAsync(IEnumerable<string> emails,
        CancellationToken cancellationToken)
    {
        var addresses = emails.ToArray();

        await inner.BulkEnableClientsAsync(addresses, cancellationToken);
        await MirrorManyAsync(NormalizeEmails(addresses), "enable", cancellationToken);
    }

    public Task<IReadOnlyList<PanelClientSummary>> GetAllClientsAsync(
        CancellationToken cancellationToken) => inner.GetAllClientsAsync(cancellationToken);

    public Task<PanelClientPage> GetClientsPageAsync(PanelClientPageRequest request,
        CancellationToken cancellationToken) => inner.GetClientsPageAsync(request, cancellationToken);

    public Task<IReadOnlyList<PanelInboundOption>> GetInboundOptionsAsync(
        CancellationToken cancellationToken) => inner.GetInboundOptionsAsync(cancellationToken);

    public async Task ResetClientTrafficAsync(string email, CancellationToken cancellationToken)
    {
        await inner.ResetClientTrafficAsync(email, cancellationToken);
        await MirrorAsync(email, "reset traffic", cancellationToken);
    }

    private async Task MirrorManyAsync(IReadOnlyList<string> emails, string operation,
        CancellationToken cancellationToken)
    {
        foreach (var email in emails)
            await MirrorAsync(email, operation, cancellationToken);
    }

    private async Task MirrorAsync(string email, string operation, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            logger.LogDebug("Skipping panel mirror after {Operation}: email is empty.", operation);
            return;
        }

        try
        {
            var details = await inner.GetClientByEmailAsync(email, cancellationToken);

            if (details is null)
            {
                logger.LogWarning(
                    "Panel {Operation} succeeded but client {Email} was not found for mirror update; " +
                    "the next sync will reconcile it.",
                    operation, email);
                return;
            }

            var result = await mirrorWriter.UpsertAsync(details, null, cancellationToken);

            if (result.Applied)
                logger.LogDebug(
                    "Mirrored panel {Operation} for {Email} (panel client {PanelClientId}).",
                    operation, email, details.Client.Id);
            else
                logger.LogWarning(
                    "Panel {Operation} for {Email} was quarantined from the mirror with {ConflictCount} conflict(s).",
                    operation, email, result.Conflicts.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Panel {Operation} succeeded but the mirror update failed for {Email}; " +
                "the panel remains authoritative and the next sync will reconcile it.",
                operation, email);
        }
    }

    private static string[] NormalizeEmails(IEnumerable<string> emails)
    {
        return emails
            .Where(email => !string.IsNullOrWhiteSpace(email))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
