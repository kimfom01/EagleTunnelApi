using System.Text.Json.Serialization;

namespace EagleTunnelApi.PanelApi.Models;

public record UpdateClientRequest(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("enable")] bool Enable,
    [property: JsonPropertyName("expiryTime")]
    long ExpiryTime,
    [property: JsonPropertyName("totalGB")]
    long TotalGB,
    [property: JsonPropertyName("tgId")] long TgId,
    [property: JsonPropertyName("comment")]
    string? Comment,
    [property: JsonPropertyName("limitIp")]
    int LimitIp,
    [property: JsonPropertyName("limitHwid")]
    int LimitHwid,
    [property: JsonPropertyName("trafficReset")]
    string TrafficReset,
    [property: JsonPropertyName("trafficResetDay")]
    int TrafficResetDay,
    [property: JsonPropertyName("reset")] int Reset,
    [property: JsonPropertyName("security")]
    string? Security,
    [property: JsonPropertyName("subId")] string? SubId,
    [property: JsonPropertyName("flow")] string? Flow,
    [property: JsonPropertyName("inboundIds")]
    List<int>? InboundIds
);