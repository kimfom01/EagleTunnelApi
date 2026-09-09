using EagleTunnelApi.Configuration;
using EagleTunnelApi.PanelApi;
using EagleTunnelApi.PanelApi.Models;
using EagleTunnelApi.Telegram;
using EagleTunnelApi.Webhook.Events;
using EagleTunnelApi.Webhook.Exceptions;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

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
    IAdminPanelService adminPanelService,
    ITelegramBotClient botClient,
    IOptions<TelegramOptions> telegramOptions)
    : ITributeEventsHandler
{
    private const double ExpiryGraceHours = 1;

    private readonly TelegramOptions _telegramOptions = telegramOptions.Value;

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

        await TryGrantReferralBonusAsync(newSubscription.TelegramUserId, refereeBefore, cancellationToken);
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

    private async Task TryGrantReferralBonusAsync(long refereeTgId, PanelClient? refereeBefore,
        CancellationToken cancellationToken)
    {
        var bonusDays = _telegramOptions.ReferralBonusDays;

        if (bonusDays <= 0 || refereeBefore is null) return;

        if (ReferralService.HasBonusPaidMarker(refereeBefore.Comment)) return;

        if (ReferralService.HasEverBeenProvisioned(refereeBefore)) return;

        if (!ReferralService.TryParseReferredBy(refereeBefore.Comment, out var referrerEmail, out _)) return;

        PanelClient? referrer = null;
        try
        {
            if (ReferralService.TryParseReferrerTgId(refereeBefore.Comment, out var referrerTgId))
                referrer = (await panelClient.GetClientByTelegramIdAsync(referrerTgId, cancellationToken))?.Client;

            referrer ??= (await panelClient.GetClientByEmailAsync(referrerEmail, cancellationToken))?.Client;
        }
        catch (PanelApiException ex)
        {
            logger.LogWarning(ex, "Referrer resolution failed, staying pending. Email: {Email}", referrerEmail);
            return;
        }

        if (referrer is null)
        {
            logger.LogInformation("Referrer not registered yet, staying pending. Email: {Email}", referrerEmail);
            return;
        }

        if (referrer.TgId == refereeTgId ||
            referrer.Email.Equals(refereeBefore.Email, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Ignoring self-referral at payout. Referee: {Email}", refereeBefore.Email);
            return;
        }

        try
        {
            await adminPanelService.GrantAsync(referrer.Email, bonusDays, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Referral grant failed. Referrer: {Email}", referrer.Email);
            await TagBonusFailedAsync(refereeTgId, cancellationToken);
            return;
        }

        logger.LogInformation("Referral bonus granted. Referrer: {Email}, Days: {Days}, Referee: {Referee}",
            referrer.Email, bonusDays, refereeBefore.Email);

        await MarkBonusPaidAsync(refereeTgId, cancellationToken);
        await AddReferrerCreditAsync(referrer.Email, bonusDays, cancellationToken);
        await NotifyReferrerAsync(referrer.TgId, bonusDays, cancellationToken);
    }

    private async Task TagBonusFailedAsync(long refereeTgId, CancellationToken cancellationToken)
    {
        try
        {
            var fresh = (await panelClient.GetClientByTelegramIdAsync(refereeTgId, cancellationToken))?.Client;

            if (fresh is not null && !ReferralService.HasBonusPaidMarker(fresh.Comment))
                await panelClient.UpdateClientAsync(fresh.ToUpdateRequest() with
                {
                    Comment = ReferralService.AppendTag(fresh.Comment, "Referral bonus failed")
                }, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to tag bonus failure. TelegramUserId: {TelegramUserId}", refereeTgId);
        }
    }

    private async Task MarkBonusPaidAsync(long refereeTgId, CancellationToken cancellationToken)
    {
        try
        {
            var fresh = (await panelClient.GetClientByTelegramIdAsync(refereeTgId, cancellationToken))?.Client;

            if (fresh is not null && !ReferralService.HasBonusPaidMarker(fresh.Comment))
                await panelClient.UpdateClientAsync(fresh.ToUpdateRequest() with
                {
                    Comment = ReferralService.AppendTag(fresh.Comment, ReferralService.BonusPaidMarker)
                }, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to mark bonus paid. TelegramUserId: {TelegramUserId}", refereeTgId);
        }
    }

    private async Task AddReferrerCreditAsync(string referrerEmail, int bonusDays,
        CancellationToken cancellationToken)
    {
        try
        {
            var fresh = (await panelClient.GetClientByEmailAsync(referrerEmail, cancellationToken))?.Client;

            if (fresh is not null)
                await panelClient.UpdateClientAsync(fresh.ToUpdateRequest() with
                {
                    Comment = ReferralService.AddCreditDays(fresh.Comment, bonusDays)
                }, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to record referrer credit. Referrer: {Email}", referrerEmail);
        }
    }

    private async Task NotifyReferrerAsync(long referrerTgId, int bonusDays, CancellationToken cancellationToken)
    {
        if (referrerTgId <= 0) return;

        try
        {
            await botClient.SendMessage(referrerTgId,
                $"🎉 Good news! Someone subscribed with your invite link — **+{bonusDays} days** of VPN time added.\n\n" +
                "Tip: cancel your Tribute renewal so you aren't billed while covered — " +
                $"message {_telegramOptions.SupportUrl} if you'd like the step-by-step video guide.",
                ParseMode.Markdown,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to notify referrer. TelegramUserId: {TelegramUserId}", referrerTgId);
        }
    }
}