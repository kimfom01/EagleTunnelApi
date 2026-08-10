using EagleTunnelApi.Configuration;
using EagleTunnelApi.PanelApi;
using EagleTunnelApi.PanelApi.Models;
using EagleTunnelApi.Webhook.Events;
using EagleTunnelApi.Webhook.Exceptions;
using Microsoft.Extensions.Options;

namespace EagleTunnelApi.Webhook.Handlers;

public interface ITributeEventsHandler
{
    Task HandleNewSubscription(NewSubscription newSubscription, CancellationToken cancellationToken);

    Task HandleRenewedSubscription(RenewedSubscription renewedSubscription,
        CancellationToken cancellationToken);

    Task UnhandledEvent(string eventName);
}

public class TributeEventsHandler : ITributeEventsHandler
{
    private const long TotalGigabytes = 300L * 1024 * 1024 * 1024;
    private const string VisionFlow = "xtls-rprx-vision";

    private readonly ILogger<TributeEventsHandler> _logger;
    private readonly IPanelClient _panelClient;
    private readonly IOptions<TelegramOptions> _telegramOptions;

    public TributeEventsHandler(ILogger<TributeEventsHandler> logger, IPanelClient panelClient,
        IOptions<TelegramOptions> telegramOptions)
    {
        _logger = logger;
        _panelClient = panelClient;
        _telegramOptions = telegramOptions;
    }

    private static long ToUnixTimeMs(DateTime dateTime) =>
        new DateTimeOffset(dateTime).ToUnixTimeMilliseconds();

    private async Task CreateClient(long telegramId, long expiryTimeMs, string subscriptionName,
        CancellationToken cancellationToken)
    {
        _logger.LogWarning("Creating Missing Client At Panel. TelegramId: {TelegramId}", telegramId);

        var email = $"tg{telegramId}";
        var createRequest = new CreateClientPayload(
            new CreateClientRequest(
                Email: email,
                Enable: true,
                ExpiryTime: expiryTimeMs,
                TotalGB: TotalGigabytes,
                TgId: telegramId,
                Comment: $"Created from subscription: {subscriptionName}",
                LimitIp: 0,
                SubId: RandomString.LowerAndNum(16),
                Password: RandomString.LowerAndNum(16),
                Auth: RandomString.LowerAndNum(16),
                Flow: VisionFlow
            ),
            _telegramOptions.Value.DefaultInboundIds.ToList()
        );

        await _panelClient.AddClientAsync(createRequest, cancellationToken);

        _logger.LogInformation("Successfully Created Client At Panel. Email: {Email}", email);
    }

    private async Task EnsureClient(long telegramId, long expiryTimeMs, string subscriptionName,
        CancellationToken cancellationToken)
    {
        var client = await FetchClientByTelegramId(telegramId, cancellationToken);

        if (client is not null)
        {
            await UpdateClient(client, expiryTimeMs, cancellationToken);
            return;
        }

        try
        {
            await CreateClient(telegramId, expiryTimeMs, subscriptionName, cancellationToken);
        }
        catch (PanelApiException)
        {
            _logger.LogWarning(
                "Creating client failed for TelegramId: {TelegramId}. Checking whether it was created concurrently.",
                telegramId);

            client = await FetchClientByTelegramId(telegramId, cancellationToken);

            if (client is null)
            {
                throw;
            }

            await UpdateClient(client, expiryTimeMs, cancellationToken);
        }
    }

    private async Task<PanelClient?> FetchClientByTelegramId(long telegramId, CancellationToken cancellationToken)
    {
        var response = await _panelClient.GetClientByTelegramIdAsync(telegramId, cancellationToken);
        return response?.Client;
    }

    private async Task UpdateClient(PanelClient client, long newExpiryTimeMs,
        CancellationToken cancellationToken)
    {
        var updateRequest = new UpdateClientRequest(
            Email: client.Email,
            Enable: true,
            ExpiryTime: newExpiryTimeMs,
            TotalGB: client.TotalGB,
            TgId: client.TgId,
            Comment: client.Comment,
            LimitIp: client.LimitIp,
            Reset: client.Reset,
            Security: client.Security,
            SubId: client.SubId,
            Flow: client.Flow
        );

        await _panelClient.UpdateClientAsync(updateRequest, cancellationToken);
    }

    public async Task HandleNewSubscription(NewSubscription newSubscription,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Handling New Subscription: {@NewSubscription}", newSubscription);

        if (newSubscription.TelegramUserId <= 0)
        {
            _logger.LogError("Invalid TelegramUser ID in new subscription event. TelegramUserId: {TelegramUserId}",
                newSubscription.TelegramUserId);
            throw new InvalidPayloadException();
        }

        var expireAt = newSubscription.ExpiresAt.AddHours(1);
        var expiryTimeMs = ToUnixTimeMs(expireAt);

        await EnsureClient(newSubscription.TelegramUserId, expiryTimeMs, newSubscription.SubscriptionName,
            cancellationToken);
    }

    public async Task HandleRenewedSubscription(RenewedSubscription renewedSubscription,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Handling Renewed Subscription: {@RenewedSubscription}", renewedSubscription);

        if (renewedSubscription.TelegramUserId <= 0)
        {
            _logger.LogError("Invalid TelegramUser ID in renewed subscription event. TelegramUserId: {TelegramUserId}",
                renewedSubscription.TelegramUserId);
            throw new InvalidPayloadException();
        }

        var expireAt = renewedSubscription.ExpiresAt.AddHours(1);
        var expiryTimeMs = ToUnixTimeMs(expireAt);

        await EnsureClient(renewedSubscription.TelegramUserId, expiryTimeMs, renewedSubscription.SubscriptionName,
            cancellationToken);
    }

    public Task UnhandledEvent(string eventName)
    {
        _logger.LogError("Unhandled Event: {EventName} @ {Time}", eventName, DateTime.UtcNow);

        return Task.CompletedTask;
    }
}
