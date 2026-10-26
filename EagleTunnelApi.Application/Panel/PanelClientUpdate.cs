namespace EagleTunnelApi.Application.Panel;

public sealed record PanelClientUpdate(
    string Email,
    bool Enable,
    long ExpiryTime,
    long TotalGB,
    long TgId,
    string? Comment,
    int LimitIp,
    int LimitHwid,
    string TrafficReset,
    int TrafficResetDay,
    int Reset,
    string? Security,
    string? SubId,
    string? Flow,
    IReadOnlyList<int>? InboundIds
);
