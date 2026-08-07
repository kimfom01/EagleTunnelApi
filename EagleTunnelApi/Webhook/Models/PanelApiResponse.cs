using System.Text.Json.Serialization;

namespace EagleTunnelApi.Webhook.Models;

public record PanelApiResponse<T>(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("msg")] string Msg,
    [property: JsonPropertyName("obj")] T? Obj
);

public record PanelClientResponse(
    [property: JsonPropertyName("client")] PanelClient Client,
    [property: JsonPropertyName("externalLinks")] List<string>? ExternalLinks,
    [property: JsonPropertyName("inboundIds")] List<int>? InboundIds,
    [property: JsonPropertyName("usedTraffic")] long UsedTraffic
);

public record PanelInbound(
    [property: JsonPropertyName("id")] int Id
);

public record PanelClient(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("enable")] bool Enable,
    [property: JsonPropertyName("expiryTime")] long ExpiryTime,
    [property: JsonPropertyName("tgId")] long TgId,
    [property: JsonPropertyName("totalGB")] long TotalGB,
    [property: JsonPropertyName("comment")] string? Comment,
    [property: JsonPropertyName("limitIp")] int LimitIp,
    [property: JsonPropertyName("reset")] int Reset,
    [property: JsonPropertyName("security")] string? Security,
    [property: JsonPropertyName("subId")] string? SubId,
    [property: JsonPropertyName("flow")] string? Flow,
    [property: JsonPropertyName("id")] int Id
);
