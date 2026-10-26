namespace EagleTunnelApi.Application.Panel;

/// <summary>
/// Lightweight inbound projection used to verify that the configured default
/// inbound ids still exist after a panel upgrade.
/// </summary>
public sealed record PanelInboundOption(
    int Id,
    string? Remark,
    string? Tag,
    string? Protocol,
    int Port,
    bool TlsFlowCapable
);
