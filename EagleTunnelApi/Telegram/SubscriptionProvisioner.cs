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

public class SubscriptionProvisioner : ISubscriptionProvisioner
{
    private const long TotalGigabytes = 300L * 1024 * 1024 * 1024;
    private const string VisionFlow = "xtls-rprx-vision";

    private readonly ILogger<SubscriptionProvisioner> _logger;
    private readonly IPanelClient _panelClient;
    private readonly IOptions<TelegramOptions> _telegramOptions;

    public SubscriptionProvisioner(ILogger<SubscriptionProvisioner> logger, IPanelClient panelClient,
        IOptions<TelegramOptions> telegramOptions)
    {
        _logger = logger;
        _panelClient = panelClient;
        _telegramOptions = telegramOptions;
    }

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
            _logger.LogWarning("No client to disable for TelegramId: {TelegramId}", telegramId);
            return;
        }

        await UpdateClient(client, enable: false, cancellationToken);

        _logger.LogInformation("Client disabled. Email: {Email}, TelegramId: {TelegramId}", client.Email, telegramId);
    }

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
            await UpdateClient(client, enable: true, expiryTimeMs, cancellationToken);
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

            await UpdateClient(client, enable: true, expiryTimeMs, cancellationToken);
        }
    }

    private async Task<PanelClient?> FetchClientByTelegramId(long telegramId, CancellationToken cancellationToken)
    {
        var response = await _panelClient.GetClientByTelegramIdAsync(telegramId, cancellationToken);
        return response?.Client;
    }

    private async Task UpdateClient(PanelClient client, bool enable, long newExpiryTimeMs,
        CancellationToken cancellationToken)
    {
        var updateRequest = new UpdateClientRequest(
            Email: client.Email,
            Enable: enable,
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

    private async Task UpdateClient(PanelClient client, bool enable, CancellationToken cancellationToken)
    {
        var updateRequest = new UpdateClientRequest(
            Email: client.Email,
            Enable: enable,
            ExpiryTime: client.ExpiryTime,
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
}