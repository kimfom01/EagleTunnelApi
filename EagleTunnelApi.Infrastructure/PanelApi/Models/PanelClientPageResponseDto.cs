using System.Text.Json.Serialization;

namespace EagleTunnelApi.Infrastructure.PanelApi.Models;

/// <summary>
/// Wire shape for the panel's <c>ClientPageResponse</c>. Summary and group
/// fields are not needed by the mirror and are intentionally omitted.
/// </summary>
public sealed record PanelClientPageResponseDto(
    [property: JsonPropertyName("items")] List<PanelClientSlimDto>? Items,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("pageSize")] int PageSize,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("filtered")] int Filtered
);

/// <summary>
/// Wire shape for the panel's <c>ClientSlim</c> row. Credentials and full
/// client settings are deliberately absent from this endpoint.
/// </summary>
public sealed record PanelClientSlimDto(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("subId")] string? SubId,
    [property: JsonPropertyName("enable")] bool Enable,
    [property: JsonPropertyName("totalGB")] long TotalGB,
    [property: JsonPropertyName("expiryTime")] long ExpiryTime,
    [property: JsonPropertyName("limitIp")] int LimitIp,
    [property: JsonPropertyName("limitHwid")] int LimitHwid,
    [property: JsonPropertyName("reset")] int Reset,
    [property: JsonPropertyName("resetDay")] int ResetDay,
    [property: JsonPropertyName("resetMax")] int ResetMax,
    [property: JsonPropertyName("resetWeekday")] int ResetWeekday,
    [property: JsonPropertyName("inboundIds")] List<int>? InboundIds,
    [property: JsonPropertyName("traffic")] PanelTrafficSummaryDto? Traffic,
    [property: JsonPropertyName("createdAt")] long CreatedAt,
    [property: JsonPropertyName("updatedAt")] long UpdatedAt
);
