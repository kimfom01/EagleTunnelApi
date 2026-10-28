using System.Text.Json.Serialization;

namespace EagleTunnelApi.Infrastructure.PanelApi.Models;

public sealed record PanelClientSummaryDto(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("subId")] string? SubId,
    [property: JsonPropertyName("uuid")] string? Uuid,
    [property: JsonPropertyName("totalGB")]
    long TotalGB,
    [property: JsonPropertyName("expiryTime")]
    long ExpiryTime,
    [property: JsonPropertyName("enable")] bool Enable,
    [property: JsonPropertyName("inboundIds")]
    List<int>? InboundIds,
    [property: JsonPropertyName("traffic")]
    PanelTrafficSummaryDto? Traffic
);

public sealed record PanelTrafficSummaryDto(
    [property: JsonPropertyName("up")] long Up,
    [property: JsonPropertyName("down")] long Down,
    [property: JsonPropertyName("enable")] bool Enable
);
