using EagleTunnelApi.Configuration;
using EagleTunnelApi.PanelApi;
using EagleTunnelApi.PanelApi.Models;
using EagleTunnelApi.Webhook.Exceptions;
using Microsoft.Extensions.Options;

namespace EagleTunnelApi.Telegram;

public interface ISubscriptionProvisioner
{
    Task ActivateAsync(long telegramId, DateTime expireAtUtc, string source, CancellationToken cancellationToken);

    Task DisableAsync(long telegramId, CancellationToken cancellationToken);
}

public class SubscriptionProvisioner(
    ILogger<SubscriptionProvisioner> logger,
    IPanelClient panelClient,
    IOptions<TelegramOptions> telegramOptions) : ISubscriptionProvisioner
{
    private readonly TelegramOptions _telegramOptions = telegramOptions.Value;

    public async Task ActivateAsync(long telegramId, DateTime expireAtUtc, string source,
        CancellationToken cancellationToken)
    {
        var expiryTimeMs = new DateTimeOffset(expireAtUtc).ToUnixTimeMilliseconds();

        await EnsureClient(telegramId, expiryTimeMs, source, cancellationToken);
    }

    public async Task DisableAsync(long telegramId, CancellationToken cancellationToken)
    {
        var client = await FetchClientByTelegramId(telegramId, cancellationToken);

        if (client is null)
        {
            logger.LogWarning("No client to disable for TelegramId: {TelegramId}", telegramId);
            return;
        }

        await UpdateClientAsync(client, false, null, cancellationToken);

        logger.LogInformation("Client disabled. Email: {Email}, TelegramId: {TelegramId}", client.Email, telegramId);
    }

    private async Task CreateClient(long telegramId, long expiryTimeMs, string subscriptionName,
        CancellationToken cancellationToken)
    {
        logger.LogWarning("Creating Missing Client At Panel. TelegramId: {TelegramId}", telegramId);

        var email = $"tg{telegramId}";
        var createRequest = new CreateClientPayload(
            PanelClientDefaults.CreateClient(email, true, expiryTimeMs, telegramId,
                $"Created from subscription: {subscriptionName}"),
            _telegramOptions.DefaultInboundIds.ToList()
        );

        await panelClient.AddClientAsync(createRequest, cancellationToken);

        logger.LogInformation("Successfully Created Client At Panel. Email: {Email}", email);
    }

    private async Task EnsureClient(long telegramId, long expiryTimeMs, string subscriptionName,
        CancellationToken cancellationToken)
    {
        var client = await FetchClientByTelegramId(telegramId, cancellationToken);

        if (client is not null)
        {
            await UpdateClientAsync(client, true, expiryTimeMs, cancellationToken);
            return;
        }

        try
        {
            await CreateClient(telegramId, expiryTimeMs, subscriptionName, cancellationToken);
        }
        catch (PanelApiException)
        {
            logger.LogWarning(
                "Creating client failed for TelegramId: {TelegramId}. Checking whether it was created concurrently.",
                telegramId);

            client = await FetchClientByTelegramId(telegramId, cancellationToken);

            if (client is null) throw;

            await UpdateClientAsync(client, true, expiryTimeMs, cancellationToken);
        }
    }

    private async Task<PanelClient?> FetchClientByTelegramId(long telegramId, CancellationToken cancellationToken)
    {
        var response = await panelClient.GetClientByTelegramIdAsync(telegramId, cancellationToken);
        return response?.Client;
    }

    private async Task UpdateClientAsync(PanelClient client, bool enable, long? expiryTimeMs,
        CancellationToken cancellationToken)
    {
        var resolvedExpiry = expiryTimeMs;

        if (expiryTimeMs.HasValue)
        {
            var creditMs = (long)ReferralService.GetCreditDays(client.Comment) * 24 * 60 * 60 * 1000;
            resolvedExpiry = expiryTimeMs.Value + creditMs;
        }

        var updateRequest = client.ToUpdateRequest() with
        {
            Enable = enable,
            ExpiryTime = resolvedExpiry ?? client.ExpiryTime,
            InboundIds = _telegramOptions.DefaultInboundIds.ToList()
        };

        await panelClient.UpdateClientAsync(updateRequest, cancellationToken);
    }
}