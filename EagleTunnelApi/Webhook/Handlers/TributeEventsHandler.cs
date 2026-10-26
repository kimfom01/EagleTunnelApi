using EagleTunnelApi.Domain.Referrals;
using EagleTunnelApi.PanelApi;
using EagleTunnelApi.PanelApi.Models;
using EagleTunnelApi.Telegram;
using EagleTunnelApi.Webhook.Events;
using EagleTunnelApi.Webhook.Exceptions;

namespace EagleTunnelApi.Webhook.Handlers;

public interface ITributeEventsHandler
{
    Task HandleNewSubscription(NewSubscription newSubscription, CancellationToken cancellationToken);

    Task HandleRenewedSubscription(RenewedSubscription renewedSubscription,
        CancellationToken cancellationToken);

    Task HandleCancelledSubscription(CancelledSubscription cancelledSubscription,
        CancellationToken cancellationToken);

    Task UnhandledEvent(string eventName);
}

public class TributeEventsHandler(
    ILogger<TributeEventsHandler> logger,
    ISubscriptionProvisioner provisioner,
    IPanelClient panelClient,
    IAdminPanelService adminPanelService)
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

        PanelClient? refereeBefore = null;
        try
        {
            refereeBefore =
                (await panelClient.GetClientByTelegramIdAsync(newSubscription.TelegramUserId, cancellationToken))
                ?.Client;
        }
        catch (PanelApiException ex)
        {
            logger.LogWarning(ex, "Pre-activation referee fetch failed, continuing. TelegramUserId: {TelegramUserId}",
                newSubscription.TelegramUserId);
        }

        var expireAt = newSubscription.ExpiresAt.AddHours(ExpiryGraceHours);

        await provisioner.ActivateAsync(newSubscription.TelegramUserId, expireAt,
            newSubscription.SubscriptionName, cancellationToken);

        await adminPanelService.TrySettleReferralBonusAsync(newSubscription.TelegramUserId, refereeBefore,
            cancellationToken);
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

    public async Task HandleCancelledSubscription(CancelledSubscription cancelledSubscription,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Handling Cancelled Subscription: {@CancelledSubscription}", cancelledSubscription);

        if (cancelledSubscription.TelegramUserId <= 0)
        {
            logger.LogWarning(
                "Ignoring cancelled subscription event with missing TelegramUser ID. Payload: {@Payload}",
                cancelledSubscription);
            return;
        }

        try
        {
            var response =
                await panelClient.GetClientByTelegramIdAsync(cancelledSubscription.TelegramUserId, cancellationToken);

            if (response?.Client is null) return;

            var client = response.Client;

            if (ReferralService.IsCancelled(client.Comment)) return;

            await panelClient.UpdateClientAsync(client.ToUpdateRequest() with
            {
                Comment = ReferralService.MarkCancelled(client.Comment, DateOnly.FromDateTime(DateTime.UtcNow))
            }, cancellationToken);

            logger.LogInformation("Marked client as cancelled. Email: {Email}", client.Email);
        }
        catch (PanelApiException ex)
        {
            logger.LogError(ex, "Failed to mark client as cancelled. TelegramUserId: {TelegramUserId}",
                cancelledSubscription.TelegramUserId);
            throw;
        }
    }

    public Task UnhandledEvent(string eventName)
    {
        logger.LogError("Unhandled Event: {EventName} @ {Time}", eventName, DateTime.UtcNow);

        return Task.CompletedTask;
    }
}