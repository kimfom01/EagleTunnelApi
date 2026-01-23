using System.Text.Json.Serialization;
using Dunet;

namespace EagleTunnelApi.Webhook.Events;

public record WebhookEvent(
    string Name,
    Payload Payload
);

[Union]
[JsonDerivedType(typeof(NewSubscription), typeDiscriminator: nameof(NewSubscription))]
[JsonDerivedType(typeof(CancelledSubscription), typeDiscriminator: nameof(CancelledSubscription))]
[JsonDerivedType(typeof(RenewedSubscription), typeDiscriminator: nameof(RenewedSubscription))]
public partial record Payload
{
    public partial record NewSubscription(
        [property: JsonPropertyName("subscription_name")]
        string SubscriptionName,
        [property: JsonPropertyName("subscription_id")]
        int SubscriptionId,
        [property: JsonPropertyName("period_id")]
        int PeriodId,
        [property: JsonPropertyName("period")] string Period,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("price")] int Price,
        [property: JsonPropertyName("amount")] int Amount,
        [property: JsonPropertyName("currency")]
        string Currency,
        [property: JsonPropertyName("user_id")]
        int UserId,
        [property: JsonPropertyName("telegram_user_id")]
        int TelegramUserId,
        [property: JsonPropertyName("channel_id")]
        int ChannelId,
        [property: JsonPropertyName("channel_name")]
        string ChannelName,
        [property: JsonPropertyName("expires_at")]
        DateTime ExpiresAt
    );

    public partial record CancelledSubscription(
        [property: JsonPropertyName("subscription_name")]
        string SubscriptionName,
        [property: JsonPropertyName("subscription_id")]
        int SubscriptionId,
        [property: JsonPropertyName("period_id")]
        int PeriodId,
        [property: JsonPropertyName("period")] string Period,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("price")] int Price,
        [property: JsonPropertyName("amount")] int Amount,
        [property: JsonPropertyName("currency")]
        string Currency,
        [property: JsonPropertyName("user_id")]
        int UserId,
        [property: JsonPropertyName("telegram_user_id")]
        int TelegramUserId,
        [property: JsonPropertyName("channel_id")]
        int ChannelId,
        [property: JsonPropertyName("channel_name")]
        string ChannelName,
        [property: JsonPropertyName("cancel_reason")]
        string CancelReason,
        [property: JsonPropertyName("expires_at")]
        DateTime ExpiresAt
    );

    public partial record RenewedSubscription(
        [property: JsonPropertyName("subscription_name")]
        string SubscriptionName,
        [property: JsonPropertyName("subscription_id")]
        int SubscriptionId,
        [property: JsonPropertyName("period_id")]
        int PeriodId,
        [property: JsonPropertyName("period")] string Period,
        [property: JsonPropertyName("price")] int Price,
        [property: JsonPropertyName("amount")] int Amount,
        [property: JsonPropertyName("currency")]
        string Currency,
        [property: JsonPropertyName("user_id")]
        int UserId,
        [property: JsonPropertyName("telegram_user_id")]
        int TelegramUserId,
        [property: JsonPropertyName("email")] string Email,
        [property: JsonPropertyName("web_app_link")]
        string WebAppLink,
        [property: JsonPropertyName("channel_id")]
        int ChannelId,
        [property: JsonPropertyName("channel_name")]
        string ChannelName,
        [property: JsonPropertyName("expires_at")]
        DateTime ExpiresAt,
        [property: JsonPropertyName("type")] string Type);
}