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
    long SubscriptionId,
    [property: JsonPropertyName("period_id")]
    long PeriodId,
    [property: JsonPropertyName("period")] string Period,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("price")] decimal Price,
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("currency")]
    string Currency,
    [property: JsonPropertyName("user_id")]
    long UserId,
    [property: JsonPropertyName("telegram_user_id")]
    long TelegramUserId,
    [property: JsonPropertyName("channel_id")]
    long ChannelId,
    [property: JsonPropertyName("channel_name")]
    string ChannelName,
    [property: JsonPropertyName("expires_at")]
    DateTime ExpiresAt
);

public record RenewedSubscription(
    [property: JsonPropertyName("subscription_name")]
    string SubscriptionName,
    [property: JsonPropertyName("subscription_id")]
    long SubscriptionId,
    [property: JsonPropertyName("period_id")]
    long PeriodId,
    [property: JsonPropertyName("period")] string Period,
    [property: JsonPropertyName("price")] decimal Price,
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("currency")]
    string Currency,
    [property: JsonPropertyName("user_id")]
    long UserId,
    [property: JsonPropertyName("telegram_user_id")]
    long TelegramUserId,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("web_app_link")]
    string WebAppLink,
    [property: JsonPropertyName("channel_id")]
    long ChannelId,
    [property: JsonPropertyName("channel_name")]
    string ChannelName,
    [property: JsonPropertyName("expires_at")]
    DateTime ExpiresAt,
    [property: JsonPropertyName("type")] string Type
);

public record CancelledSubscription(
    [property: JsonPropertyName("subscription_name")]
    string SubscriptionName,
    [property: JsonPropertyName("subscription_id")]
    long SubscriptionId,
    [property: JsonPropertyName("period_id")]
    long PeriodId,
    [property: JsonPropertyName("period")] string Period,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("price")] decimal Price,
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("currency")]
    string Currency,
    [property: JsonPropertyName("trb_user_id")]
    string? TrbUserId,
    [property: JsonPropertyName("telegram_user_id")]
    long TelegramUserId,
    [property: JsonPropertyName("telegram_username")]
    string? TelegramUsername,
    [property: JsonPropertyName("channel_id")]
    long ChannelId,
    [property: JsonPropertyName("channel_name")]
    string ChannelName,
    [property: JsonPropertyName("cancel_reason")]
    string? CancelReason,
    [property: JsonPropertyName("expires_at")]
    DateTime ExpiresAt
);