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


    public async Task HandleNewSubscription(NewSubscription newSubscription,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Handling New Subscription: {@NewSubscription}", newSubscription);

        _logger.LogInformation("Fetching User Information From Remnawave. TelegramId: {@TelegramId}",
            newSubscription.TelegramUserId);
        var getUserResponse = await _httpClient.GetFromJsonAsync<GetUserResponse>(
            $"api/users/by-telegram-id/{newSubscription.TelegramUserId}", cancellationToken);

        if (getUserResponse is null)
        {
            _logger.LogError("Invalid Response from Remnawave: @{Time}", DateTime.UtcNow);
            throw new InvalidPayloadException();
        }

        var user = getUserResponse.Response[0];

        _logger.LogInformation("Enabling User Subscription At Remnawave. UUID: {@Uuid}", user.Uuid);

        DateTime? expireAt = newSubscription.ExpiresAt.AddHours(1);

        var activateRequest = new ActivateUserRequest(user.Uuid, "ACTIVE", expireAt);

        var responseMessage = await _httpClient.PatchAsJsonAsync("api/users", activateRequest, cancellationToken);

        responseMessage.EnsureSuccessStatusCode();

        _logger.LogInformation("Successfully Activated User Subscription At Remnawave. UUID: {@Uuid}", user.Uuid);
    }

    public async Task HandleRenewedSubscription(RenewedSubscription renewedSubscription,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Handling Renewed Subscription: {@RenewedSubscription}", renewedSubscription);

        _logger.LogInformation("Fetching User Information From Remnawave. TelegramId: {@TelegramId}",
            renewedSubscription.TelegramUserId);
        var getUserResponse = await _httpClient.GetFromJsonAsync<GetUserResponse>(
            $"api/users/by-telegram-id/{renewedSubscription.TelegramUserId}", cancellationToken);

        if (getUserResponse is null)
        {
            _logger.LogError("Invalid Response from Remnawave: @{Time}", DateTime.UtcNow);
            throw new InvalidPayloadException();
        }

        var user = getUserResponse.Response[0];

        DateTime? expireAt = renewedSubscription.ExpiresAt.AddHours(1);

        var activateRequest = new ActivateUserRequest(user.Uuid, "ACTIVE", expireAt);

        var responseMessage = await _httpClient.PatchAsJsonAsync("api/users", activateRequest, cancellationToken);

        responseMessage.EnsureSuccessStatusCode();

        _logger.LogInformation("Successfully Renewed User Subscription At Remnawave. UUID: {@Uuid}", user.Uuid);
    }

    public Task UnhandledEvent(string eventName)
    {
        _logger.LogError("Unhandled Event: {EventName} @{Time}", eventName, DateTime.UtcNow);

        return Task.CompletedTask;
    }
}