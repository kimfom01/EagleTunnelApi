using System.Text.Json.Serialization;

namespace EagleTunnelApi.PanelApi.Models;

public record PanelClientSummary(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("subId")] string? SubId,
    [property: JsonPropertyName("uuid")] string? Uuid,
    [property: JsonPropertyName("totalGB")] long TotalGB,
    [property: JsonPropertyName("expiryTime")] long ExpiryTime,
    [property: JsonPropertyName("enable")] bool Enable,
    [property: JsonPropertyName("inboundIds")] List<int>? InboundIds,
    [property: JsonPropertyName("traffic")] PanelTrafficSummary? Traffic
);

public record PanelTrafficSummary(
    [property: JsonPropertyName("up")] long Up,
    [property: JsonPropertyName("down")] long Down,
    [property: JsonPropertyName("enable")] bool Enable
);