using EagleTunnelApi.Telegram;
using EagleTunnelApi.TributeShop;
using EagleTunnelApi.Webhook.Events;
using EagleTunnelApi.Webhook.Exceptions;

namespace EagleTunnelApi.Webhook.Handlers;

public interface ITributeShopEventsHandler
{
    Task HandlePaymentReceived(ShopOrderEventPayload payload, CancellationToken cancellationToken);

    Task HandleChargeSuccess(ShopOrderEventPayload payload, CancellationToken cancellationToken);

    Task HandleChargeFailed(ShopOrderEventPayload payload, CancellationToken cancellationToken);

    Task HandleSubscriptionCancelled(ShopOrderEventPayload payload, CancellationToken cancellationToken);

    Task HandleRefunded(ShopOrderEventPayload payload, CancellationToken cancellationToken);

    Task HandlePaymentFailed(ShopOrderEventPayload payload, CancellationToken cancellationToken);
}

public class TributeShopEventsHandler : ITributeShopEventsHandler
{
    private readonly ILogger<TributeShopEventsHandler> _logger;
    private readonly ISubscriptionProvisioner _provisioner;

    public TributeShopEventsHandler(ILogger<TributeShopEventsHandler> logger, ISubscriptionProvisioner provisioner)
    {
        _logger = logger;
        _provisioner = provisioner;
    }

    public async Task HandlePaymentReceived(ShopOrderEventPayload payload, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Handling shop order payment received: {@Payload}", payload);

        var telegramId = ParseCustomerId(payload);
        var expireAt = ResolveExpiry(payload);

        await _provisioner.ActivateAsync(telegramId, expireAt, $"shop order {payload.Uuid}", cancellationToken);
    }

    public async Task HandleChargeSuccess(ShopOrderEventPayload payload, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Handling shop order charge success: {@Payload}", payload);

        var telegramId = ParseCustomerId(payload);

        if (payload.MemberExpiresAt is null)
        {
            _logger.LogError("Recurring charge success event without memberExpiresAt. Uuid: {Uuid}", payload.Uuid);
            throw new InvalidPayloadException();
        }

        var expireAt = payload.MemberExpiresAt.Value.AddHours(1);

        await _provisioner.ActivateAsync(telegramId, expireAt, $"shop order charge {payload.Uuid}", cancellationToken);
    }

    public Task HandleChargeFailed(ShopOrderEventPayload payload, CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            "Recurring charge failed. Uuid: {Uuid}, Attempts: {ChargeRetries}, Period: {Period}. " +
            "Access is retained until the paid period expires.",
            payload.Uuid, payload.ChargeRetries, payload.Period);

        return Task.CompletedTask;
    }

    public Task HandleSubscriptionCancelled(ShopOrderEventPayload payload, CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            "Subscription cancelled. Uuid: {Uuid}, Reason: {CancelReason}. " +
            "Access is retained until the paid period expires.",
            payload.Uuid, payload.CancelReason);

        return Task.CompletedTask;
    }

    public async Task HandleRefunded(ShopOrderEventPayload payload, CancellationToken cancellationToken)
    {
        _logger.LogWarning("Refund detected for shop order: {@Payload}", payload);

        var telegramId = ParseCustomerId(payload);

        await _provisioner.DisableAsync(telegramId, cancellationToken);
    }

    public Task HandlePaymentFailed(ShopOrderEventPayload payload, CancellationToken cancellationToken)
    {
        _logger.LogWarning("Initial payment failed. Uuid: {Uuid}. No access was granted.", payload.Uuid);

        return Task.CompletedTask;
    }

    private static long ParseCustomerId(ShopOrderEventPayload payload)
    {
        if (!long.TryParse(payload.CustomerId, out var telegramId) || telegramId <= 0)
        {
            throw new InvalidPayloadException();
        }

        return telegramId;
    }

    private DateTime ResolveExpiry(ShopOrderEventPayload payload)
    {
        var isRecurring = payload.IsRecurrent == true
                          || payload.Period is not null && !payload.Period.Equals("onetime", StringComparison.OrdinalIgnoreCase);

        if (payload.MemberExpiresAt is not null)
        {
            return payload.MemberExpiresAt.Value.AddHours(1);
        }

        if (isRecurring && payload.Period is not null)
        {
            var plan = SubscriptionPlans.ByPeriod(payload.Period);
            if (plan is not null)
            {
                return DateTime.UtcNow.AddDays(plan.DurationDays);
            }
        }

        if (payload.Amount is { } amount)
        {
            var oneTimePlan = SubscriptionPlans.ByAmount((int)amount);
            if (oneTimePlan is not null && !oneTimePlan.IsRecurring)
            {
                return DateTime.UtcNow.AddDays(oneTimePlan.DurationDays);
            }
        }

        _logger.LogError("Could not resolve subscription duration from payload: {@Payload}", payload);
        throw new InvalidPayloadException();
    }
}