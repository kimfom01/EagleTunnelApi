using EagleTunnelApi.Webhook.Events;
using EagleTunnelApi.Webhook.Exceptions;
using EagleTunnelApi.Webhook.Models;

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
    private readonly ILogger<TributeEventsHandler> _logger;
    private readonly HttpClient _httpClient;

    public TributeEventsHandler(ILogger<TributeEventsHandler> logger, HttpClient httpClient)
    {
        _logger = logger;
        _httpClient = httpClient;
    }

    private static long ToUnixTimeMs(DateTime dateTime) =>
        new DateTimeOffset(dateTime).ToUnixTimeMilliseconds();

    private async Task<PanelClient> FetchClientByTelegramId(long telegramId,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Fetching Client From Panel. TelegramId: {TelegramId}", telegramId);

        var response = await _httpClient.GetFromJsonAsync<PanelApiResponse<List<PanelClientResponse>>>(
            $"/admin/panel/api/clients/get/tgId/{telegramId}", cancellationToken);

        if (response?.Obj is not { Count: > 0 } clientResponses)
        {
            _logger.LogError("Client not found for TelegramId: {TelegramId}", telegramId);
            throw new NotFoundException();
        }

        return clientResponses[0].Client;
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

        _logger.LogInformation("Updating Client At Panel. Email: {Email}", client.Email);

        var responseMessage = await _httpClient.PostAsJsonAsync(
            $"/admin/panel/api/clients/update/{client.Email}",
            updateRequest, cancellationToken);

        responseMessage.EnsureSuccessStatusCode();

        _logger.LogInformation("Successfully Updated Client At Panel. Email: {Email}", client.Email);
    }

    public async Task HandleNewSubscription(NewSubscription newSubscription,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Handling New Subscription: {@NewSubscription}", newSubscription);

        var client = await FetchClientByTelegramId(newSubscription.TelegramUserId, cancellationToken);

        var expireAt = newSubscription.ExpiresAt.AddHours(1);
        var expiryTimeMs = ToUnixTimeMs(expireAt);

        await UpdateClient(client, expiryTimeMs, cancellationToken);
    }

    public async Task HandleRenewedSubscription(RenewedSubscription renewedSubscription,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Handling Renewed Subscription: {@RenewedSubscription}", renewedSubscription);

        var client = await FetchClientByTelegramId(renewedSubscription.TelegramUserId, cancellationToken);

        var expireAt = renewedSubscription.ExpiresAt.AddHours(1);
        var expiryTimeMs = ToUnixTimeMs(expireAt);

        await UpdateClient(client, expiryTimeMs, cancellationToken);
    }

    public Task UnhandledEvent(string eventName)
    {
        _logger.LogError("Unhandled Event: {EventName} @ {Time}", eventName, DateTime.UtcNow);

        return Task.CompletedTask;
    }
}