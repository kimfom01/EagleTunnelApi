using EagleTunnelApi.Telegram;
using EagleTunnelApi.Webhook.Events;
using EagleTunnelApi.Webhook.Exceptions;

namespace EagleTunnelApi.Webhook.Handlers;

public interface ITributeEventsHandler
{
    Task HandleNewSubscription(NewSubscription newSubscription, CancellationToken cancellationToken);

    Task HandleRenewedSubscription(RenewedSubscription renewedSubscription,
        CancellationToken cancellationToken);

    Task UnhandledEvent(string eventName);
}

[Obsolete(
    "Legacy Tribute subscription webhooks are deprecated. New orders are created through the Shop API and " +
    "processed by ITributeShopEventsHandler. Remove once all users have migrated.")]
public class TributeEventsHandler : ITributeEventsHandler
{
    private readonly ILogger<TributeEventsHandler> _logger;
    private readonly ISubscriptionProvisioner _provisioner;

    public TributeEventsHandler(ILogger<TributeEventsHandler> logger, ISubscriptionProvisioner provisioner)
    {
        _logger = logger;
        _provisioner = provisioner;
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

        await _provisioner.ActivateAsync(newSubscription.TelegramUserId, expireAt, newSubscription.SubscriptionName,
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

        await _provisioner.ActivateAsync(renewedSubscription.TelegramUserId, expireAt,
            renewedSubscription.SubscriptionName, cancellationToken);
    }

    public Task UnhandledEvent(string eventName)
    {
        _logger.LogError("Unhandled Event: {EventName} @ {Time}", eventName, DateTime.UtcNow);

        return Task.CompletedTask;
    }
}