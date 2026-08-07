using System.Text.Json;
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

    private async Task<PanelClient?> FetchClientByTelegramId(long telegramId,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Fetching Client From Panel. TelegramId: {TelegramId}", telegramId);

        var response = await _httpClient.GetFromJsonAsync<PanelApiResponse<List<PanelClientResponse>>>(
            $"/admin/panel/api/clients/get/tgId/{telegramId}", cancellationToken);

        if (response is null)
        {
            throw new PanelApiException("Panel returned an empty response");
        }

        if (!response.Success)
        {
            _logger.LogError("Panel fetch failed. TelegramId: {TelegramId}, Message: {Msg}", telegramId, response.Msg);
            throw new PanelApiException($"Panel fetch failed: {response.Msg}");
        }

        if (response.Obj is not { Count: > 0 } clientResponses)
        {
            _logger.LogWarning("Client not found for TelegramId: {TelegramId}", telegramId);
            return null;
        }

        if (clientResponses.Count > 1)
        {
            var emails = string.Join(", ", clientResponses.Select(c => c.Client.Email));
            _logger.LogWarning(
                "Multiple clients found for TelegramId: {TelegramId}. Using first match. Emails: {Emails}",
                telegramId, emails);
        }

        return clientResponses[0].Client;
    }

    private static string RandomString(int length)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";

        return new string(Enumerable.Repeat(chars, length)
            .Select(s => s[Random.Shared.Next(s.Length)]).ToArray());
    }

    private async Task<List<int>> FetchAllInboundIds(CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetFromJsonAsync<PanelApiResponse<List<PanelInbound>>>(
            "/admin/panel/api/inbounds/list", cancellationToken);

        if (response is null)
        {
            throw new PanelApiException("Panel returned an empty response");
        }

        if (!response.Success)
        {
            _logger.LogError("Panel inbounds fetch failed. Message: {Msg}", response.Msg);
            throw new PanelApiException($"Panel inbounds fetch failed: {response.Msg}");
        }

        return response.Obj?.Select(inbound => inbound.Id).ToList() ?? [];
    }

    private async Task PostAndValidateAsync(string url, object body, string action,
        CancellationToken cancellationToken)
    {
        var responseMessage = await _httpClient.PostAsJsonAsync(url, body, cancellationToken);

        PanelApiResponse<object>? apiResponse = null;
        if (responseMessage.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase)
            is true)
        {
            try
            {
                apiResponse = await responseMessage.Content.ReadFromJsonAsync<PanelApiResponse<object>>(cancellationToken);
            }
            catch (JsonException)
            {
                apiResponse = null;
            }
        }

        if (!responseMessage.IsSuccessStatusCode || apiResponse is null || !apiResponse.Success)
        {
            var message = apiResponse?.Msg ?? responseMessage.ReasonPhrase ?? "unknown";
            _logger.LogError("{Action} failed. Status: {Status}, Message: {Message}", action,
                responseMessage.StatusCode, message);
            throw new PanelApiException($"{action} failed: {message}");
        }
    }

    private async Task CreateClient(long telegramId, long expiryTimeMs, string subscriptionName,
        CancellationToken cancellationToken)
    {
        _logger.LogWarning("Creating Missing Client At Panel. TelegramId: {TelegramId}", telegramId);

        var inboundIds = await FetchAllInboundIds(cancellationToken);

        if (inboundIds.Count == 0)
        {
            _logger.LogError("No inbounds available to attach new client. TelegramId: {TelegramId}", telegramId);
            throw new NotFoundException();
        }

        var email = $"tg{telegramId}";
        var createRequest = new CreateClientPayload(
            new CreateClientRequest(
                Email: email,
                Enable: true,
                ExpiryTime: expiryTimeMs,
                TotalGB: 0,
                TgId: telegramId,
                Comment: $"Created from subscription: {subscriptionName}",
                LimitIp: 0,
                SubId: RandomString(16)
            ),
            inboundIds
        );

        _logger.LogInformation("Creating Client At Panel. Email: {Email}, InboundIds: {@InboundIds}", email,
            inboundIds);

        await PostAndValidateAsync("/admin/panel/api/clients/add", createRequest, "Creating client", cancellationToken);

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

        await PostAndValidateAsync($"/admin/panel/api/clients/update/{client.Email}", updateRequest,
            "Updating client", cancellationToken);

        _logger.LogInformation("Successfully Updated Client At Panel. Email: {Email}", client.Email);
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