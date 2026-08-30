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

public class TributeEventsHandler(ILogger<TributeEventsHandler> logger, ISubscriptionProvisioner provisioner)
    : ITributeEventsHandler
{
    private const double ExpiryGraceHours = 1;

    public async Task HandleNewSubscription(NewSubscription newSubscription,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Handling New Subscription: {@NewSubscription}", newSubscription);

        if (newSubscription.TelegramUserId <= 0)
        {
            logger.LogError("Invalid TelegramUser ID in new subscription event. TelegramUserId: {TelegramUserId}",
                newSubscription.TelegramUserId);
            throw new InvalidPayloadException();
        }

        var expireAt = newSubscription.ExpiresAt.AddHours(ExpiryGraceHours);

        await provisioner.ActivateAsync(newSubscription.TelegramUserId, expireAt,
            newSubscription.SubscriptionName, cancellationToken);
    }

    public async Task HandleRenewedSubscription(RenewedSubscription renewedSubscription,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Handling Renewed Subscription: {@RenewedSubscription}", renewedSubscription);

        if (renewedSubscription.TelegramUserId <= 0)
        {
            logger.LogError("Invalid TelegramUser ID in renewed subscription event. TelegramUserId: {TelegramUserId}",
                renewedSubscription.TelegramUserId);
            throw new InvalidPayloadException();
        }

        var expireAt = renewedSubscription.ExpiresAt.AddHours(ExpiryGraceHours);

        await provisioner.ActivateAsync(renewedSubscription.TelegramUserId, expireAt,
            renewedSubscription.SubscriptionName, cancellationToken);
    }

    public Task UnhandledEvent(string eventName)
    {
        logger.LogError("Unhandled Event: {EventName} @ {Time}", eventName, DateTime.UtcNow);

        return Task.CompletedTask;
    }
}
