using System.Text.Json;
using System.Text.Json.Serialization;

namespace EagleTunnelApi.TributeShop;

public sealed record CreateShopOrderRequest(
    [property: JsonPropertyName("shopId")] long? ShopId,
    [property: JsonPropertyName("amount")] int Amount,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("successUrl")] string? SuccessUrl,
    [property: JsonPropertyName("failUrl")] string? FailUrl,
    [property: JsonPropertyName("comment")] string? Comment,
    [property: JsonPropertyName("customerId")] string? CustomerId,
    [property: JsonPropertyName("period")] string Period
);

public sealed record ShopOrderResponse(
    [property: JsonPropertyName("uuid")] string Uuid,
    [property: JsonPropertyName("shopId")] long ShopId,
    [property: JsonPropertyName("amount")] long Amount,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("successUrl")] string? SuccessUrl,
    [property: JsonPropertyName("failUrl")] string? FailUrl,
    [property: JsonPropertyName("paymentUrl")] string? PaymentUrl,
    [property: JsonPropertyName("webappPaymentUrl")] string? WebappPaymentUrl,
    [property: JsonPropertyName("createdAt")] DateTime CreatedAt,
    [property: JsonPropertyName("period")] string Period
);

internal sealed record TributeErrorResponse(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("message")] string Message
);