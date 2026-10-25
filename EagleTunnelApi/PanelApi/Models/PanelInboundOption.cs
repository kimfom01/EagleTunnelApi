using System.Text.Json.Serialization;

namespace EagleTunnelApi.PanelApi.Models;

/// <summary>
/// Lightweight inbound projection returned by
/// <c>GET /admin/panel/api/inbounds/options</c>. Used to verify that the
/// configured default inbound ids still exist after a panel upgrade.
/// </summary>
public record PanelInboundOption(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("remark")] string? Remark,
    [property: JsonPropertyName("tag")] string? Tag,
    [property: JsonPropertyName("protocol")] string? Protocol,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("tlsFlowCapable")] bool TlsFlowCapable
);
