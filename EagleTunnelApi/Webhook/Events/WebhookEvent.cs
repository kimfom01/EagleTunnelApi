using System.Text.Json;
using System.Text.Json.Serialization;

namespace EagleTunnelApi.Webhook.Events;

public record WebhookEvent(
    string Name,
    DateTime CreatedAt,
    DateTime SentAt,
    JsonElement Payload
);

public record NewSubscription(
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

public record CancelledSubscription(
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

public record RenewedSubscription(
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
    [property: JsonPropertyName("type")] string Type
);