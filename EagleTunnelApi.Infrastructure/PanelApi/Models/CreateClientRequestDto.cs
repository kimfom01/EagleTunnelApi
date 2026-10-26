using System.Text.Json.Serialization;

namespace EagleTunnelApi.Infrastructure.PanelApi.Models;

public sealed record CreateClientPayloadDto(
    [property: JsonPropertyName("client")] CreateClientRequestDto Client,
    [property: JsonPropertyName("inboundIds")]
    List<int> InboundIds
);

public sealed record CreateClientRequestDto(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("enable")] bool Enable,
    [property: JsonPropertyName("expiryTime")]
    long ExpiryTime,
    [property: JsonPropertyName("totalGB")]
    long TotalGB,
    [property: JsonPropertyName("tgId")] long TgId,
    [property: JsonPropertyName("comment")]
    string? Comment,
    [property: JsonPropertyName("limitHwid")]
    int LimitHwid,
    [property: JsonPropertyName("trafficReset")]
    string TrafficReset,
    [property: JsonPropertyName("trafficResetDay")]
    int TrafficResetDay,
    [property: JsonPropertyName("subId")] string SubId,
    [property: JsonPropertyName("password")]
    string Password,
    [property: JsonPropertyName("auth")] string Auth,
    [property: JsonPropertyName("flow")] string Flow
);
