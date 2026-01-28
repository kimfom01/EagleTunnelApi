using System.Text.Json.Serialization;

namespace EagleTunnelApi.Webhook.Models;

public record ActivateUserRequest(
    [property: JsonPropertyName("uuid")] string Uuid,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("expireAt")]
    DateTime? ExpireAt
);