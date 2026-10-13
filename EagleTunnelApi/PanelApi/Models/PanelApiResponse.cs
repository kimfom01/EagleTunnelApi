using System.Text.Json.Serialization;

namespace EagleTunnelApi.PanelApi.Models;

public record PanelApiResponse<T>(
    [property: JsonPropertyName("success")]
    bool Success,
    [property: JsonPropertyName("msg")] string Msg,
    [property: JsonPropertyName("obj")] T? Obj
);

public record PanelClientResponse(
    [property: JsonPropertyName("client")] PanelClient Client,
    [property: JsonPropertyName("externalLinks")]
    List<string>? ExternalLinks,
    [property: JsonPropertyName("inboundIds")]
    List<int>? InboundIds,
    [property: JsonPropertyName("usedTraffic")]
    long UsedTraffic
);

public record PanelClient(
    [property: JsonPropertyName("uuid")] string Uuid,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("enable")] bool Enable,
    [property: JsonPropertyName("expiryTime")]
    long ExpiryTime,
    [property: JsonPropertyName("tgId")] long TgId,
    [property: JsonPropertyName("totalGB")]
    long TotalGB,
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
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("inboundIds")]
    List<int>? InboundIds
);

public static class PanelClientExtensions
{
    public static UpdateClientRequest ToUpdateRequest(this PanelClient client)
    {
        return new UpdateClientRequest(client.Email, client.Enable, client.ExpiryTime, client.TotalGB, client.TgId,
            client.Comment,
            client.LimitIp, client.LimitHwid, client.TrafficReset, client.TrafficResetDay,
            client.Reset, client.Security, client.SubId, client.Flow, client.InboundIds);
    }
}