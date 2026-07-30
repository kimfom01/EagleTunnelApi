using System.Text.Json.Serialization;

namespace EagleTunnelApi.Webhook.Models;

public record UpdateClientRequest(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("enable")] bool Enable,
    [property: JsonPropertyName("expiryTime")] long ExpiryTime,
    [property: JsonPropertyName("totalGB")] long TotalGB,
    [property: JsonPropertyName("tgId")] long TgId,
    [property: JsonPropertyName("comment")] string? Comment,
    [property: JsonPropertyName("limitIp")] int LimitIp,
    [property: JsonPropertyName("reset")] int Reset,
    [property: JsonPropertyName("security")] string? Security,
    [property: JsonPropertyName("subId")] string? SubId,
    [property: JsonPropertyName("flow")] string? Flow
);
