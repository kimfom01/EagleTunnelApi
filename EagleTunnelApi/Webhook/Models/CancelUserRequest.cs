using System.Text.Json.Serialization;

namespace EagleTunnelApi.Webhook.Models;

public record CancelUserRequest(
    [property: JsonPropertyName("uuid")] string Uuid,
    [property: JsonPropertyName("expireAt")]
    DateTime? ExpireAt
);