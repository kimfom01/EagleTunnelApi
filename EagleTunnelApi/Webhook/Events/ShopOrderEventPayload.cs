using System.Text.Json.Serialization;

namespace EagleTunnelApi.Webhook.Events;

/// <summary>
/// Common shape of the shop order webhook payloads. Only the fields relevant
/// to provisioning are bound; extras are ignored.
/// </summary>
public record ShopOrderEventPayload(
    [property: JsonPropertyName("uuid")] string? Uuid,
    [property: JsonPropertyName("shopId")] long? ShopId,
    [property: JsonPropertyName("amount")] long? Amount,
    [property: JsonPropertyName("currency")] string? Currency,
    [property: JsonPropertyName("period")] string? Period,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("customerId")] string? CustomerId,
    [property: JsonPropertyName("isRecurrent")] bool? IsRecurrent,
    [property: JsonPropertyName("memberStatus")] string? MemberStatus,
    [property: JsonPropertyName("memberExpiresAt")] DateTime? MemberExpiresAt,
    [property: JsonPropertyName("cancelReason")] string? CancelReason,
    [property: JsonPropertyName("chargeRetries")] int? ChargeRetries,
    [property: JsonPropertyName("transactionId")] long? TransactionId,
    [property: JsonPropertyName("refundedAt")] DateTime? RefundedAt,
    [property: JsonPropertyName("starsAmount")] long? StarsAmount,
    [property: JsonPropertyName("onlyStars")] bool? OnlyStars
);