using System.Text.Json.Serialization;

namespace EagleTunnelApi.Infrastructure.PanelApi.Models;

public sealed record PanelClientResponseDto(
    [property: JsonPropertyName("client")] PanelClientDto Client,
    [property: JsonPropertyName("externalLinks")]
    List<string>? ExternalLinks,
    [property: JsonPropertyName("inboundIds")]
    List<int>? InboundIds,
    [property: JsonPropertyName("usedTraffic")]
    long UsedTraffic
);
