using System.Text.Json.Serialization;

namespace EagleTunnelApi.Webhook.Models;

public record CreateClientPayload(
    [property: JsonPropertyName("client")] CreateClientRequest Client,
    [property: JsonPropertyName("inboundIds")] List<int> InboundIds
);

public record CreateClientRequest(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("enable")] bool Enable,
    [property: JsonPropertyName("expiryTime")] long ExpiryTime,
    [property: JsonPropertyName("totalGB")] long TotalGB,
    [property: JsonPropertyName("tgId")] long TgId,
    [property: JsonPropertyName("comment")] string? Comment,
    [property: JsonPropertyName("limitIp")] int LimitIp,
    [property: JsonPropertyName("subId")] string SubId
);
