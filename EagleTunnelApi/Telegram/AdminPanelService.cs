using EagleTunnelApi.PanelApi;
using EagleTunnelApi.PanelApi.Models;
using EagleTunnelApi.Webhook.Exceptions;

namespace EagleTunnelApi.Telegram;

public interface IAdminPanelService
{
    Task<PanelClient?> GetByTargetAsync(string target, CancellationToken cancellationToken);

    Task<IReadOnlyList<PanelClientSummary>> ListClientsAsync(CancellationToken cancellationToken);

    Task GrantAsync(string email, int days, CancellationToken cancellationToken);

    Task BanAsync(string email, CancellationToken cancellationToken);

    Task UnbanAsync(string email, CancellationToken cancellationToken);

    Task SetDeviceLimitAsync(string email, int limit, CancellationToken cancellationToken);

    Task ResetTrafficAsync(string email, CancellationToken cancellationToken);

    Task LinkToTelegramAsync(string email, long telegramId, CancellationToken cancellationToken);
}

public class AdminPanelService(ILogger<AdminPanelService> logger, IPanelClient panelClient) : IAdminPanelService
{
    public async Task<PanelClient?> GetByTargetAsync(string target, CancellationToken cancellationToken)
    {
        if (long.TryParse(target, out var tgId) && tgId > 0)
        {
            var response = await panelClient.GetClientByTelegramIdAsync(tgId, cancellationToken);
            return response?.Client;
        }

        var responseByEmail = await panelClient.GetClientByEmailAsync(target, cancellationToken);
        return responseByEmail?.Client;
    }

    public async Task<IReadOnlyList<PanelClientSummary>> ListClientsAsync(CancellationToken cancellationToken)
    {
        var clients = await panelClient.GetAllClientsAsync(cancellationToken);
        return clients.OrderBy(c => c.Email).ToList();
    }

    public async Task GrantAsync(string email, int days, CancellationToken cancellationToken)
    {
        var client = await RequireClient(email, cancellationToken);

        var nowUtc = DateTimeOffset.UtcNow;
        var currentExpiry = client.ExpiryTime > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(client.ExpiryTime).ToUniversalTime()
            : DateTimeOffset.MinValue;

        var baseDate = client.Enable
            ? (currentExpiry > nowUtc ? currentExpiry : nowUtc)
            : nowUtc;

        var newExpiryMs = baseDate.AddDays(days).ToUnixTimeMilliseconds();

        await UpdateClient(client, enable: true, expiryTimeMs: newExpiryMs, limitIp: client.LimitIp,
            telegramId: client.TgId, cancellationToken);

        logger.LogInformation("Granted {Days} days to client. Email: {Email}, NewExpiry: {NewExpiry}",
            days, client.Email, newExpiryMs);
    }

    public async Task BanAsync(string email, CancellationToken cancellationToken)
    {
        var client = await RequireClient(email, cancellationToken);

        await panelClient.BulkDisableClientsAsync(new[] { client.Email }, cancellationToken);

        logger.LogInformation("Banned client. Email: {Email}", client.Email);
    }

    public async Task UnbanAsync(string email, CancellationToken cancellationToken)
    {
        var client = await RequireClient(email, cancellationToken);

        await panelClient.BulkEnableClientsAsync(new[] { client.Email }, cancellationToken);

        logger.LogInformation("Unbanned client. Email: {Email}", client.Email);
    }

    public async Task SetDeviceLimitAsync(string email, int limit, CancellationToken cancellationToken)
    {
        var client = await RequireClient(email, cancellationToken);

        await UpdateClient(client, enable: client.Enable, expiryTimeMs: client.ExpiryTime, limitIp: limit,
            telegramId: client.TgId, cancellationToken);

        logger.LogInformation("Set device limit to {Limit} for client. Email: {Email}", limit, client.Email);
    }

    public async Task ResetTrafficAsync(string email, CancellationToken cancellationToken)
    {
        var client = await RequireClient(email, cancellationToken);

        await panelClient.ResetClientTrafficAsync(client.Email, cancellationToken);

        logger.LogInformation("Reset traffic for client. Email: {Email}", client.Email);
    }

    public async Task LinkToTelegramAsync(string email, long telegramId, CancellationToken cancellationToken)
    {
        var client = await RequireClient(email, cancellationToken);

        await UpdateClient(client, enable: client.Enable, expiryTimeMs: client.ExpiryTime, limitIp: client.LimitIp,
            telegramId: telegramId, cancellationToken);

        logger.LogInformation("Linked client to telegram. Email: {Email}, TelegramId: {TelegramId}", client.Email,
            telegramId);
    }

    private async Task<PanelClient> RequireClient(string email, CancellationToken cancellationToken)
    {
        var response = await panelClient.GetClientByEmailAsync(email, cancellationToken);

        if (response?.Client is null)
        {
            throw new PanelApiException($"No client found for email {email}.");
        }

        return response.Client;
    }

    private async Task UpdateClient(PanelClient client, bool enable, long expiryTimeMs, int limitIp, long telegramId,
        CancellationToken cancellationToken)
    {
        var updateRequest = new UpdateClientRequest(
            Email: client.Email,
            Enable: enable,
            ExpiryTime: expiryTimeMs,
            TotalGB: client.TotalGB,
            TgId: telegramId,
            Comment: client.Comment,
            LimitIp: limitIp,
            Reset: client.Reset,
            Security: client.Security,
            SubId: client.SubId,
            Flow: client.Flow
        );

        await panelClient.UpdateClientAsync(updateRequest, cancellationToken);
    }
}