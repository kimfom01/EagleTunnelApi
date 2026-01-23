using EagleTunnelApi.Webhook.Events;

namespace EagleTunnelApi.Webhook.Handlers;

public interface ITributeEventsHandler
{
    Task HandleNewSubscription(WebhookEvent evt);

    Task HandleCancelledSubscription(WebhookEvent evt);

    Task HandleRenewedSubscription(WebhookEvent evt);
}

public class TributeEventsHandler : ITributeEventsHandler
{
    public Task HandleNewSubscription(WebhookEvent evt)
    {
        throw new NotImplementedException();
    }

    public Task HandleCancelledSubscription(WebhookEvent evt)
    {
        throw new NotImplementedException();
    }

    public Task HandleRenewedSubscription(WebhookEvent evt)
    {
        throw new NotImplementedException();
    }
}