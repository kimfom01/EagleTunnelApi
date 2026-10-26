namespace EagleTunnelApi.Application.Panel;

public sealed record PanelClientCreate(
    string Email,
    bool Enable,
    long ExpiryTime,
    long TotalGB,
    long TgId,
    string? Comment,
    int LimitHwid,
    string TrafficReset,
    int TrafficResetDay,
    string SubId,
    string Password,
    string Auth,
    string Flow,
    IReadOnlyList<int> InboundIds
);
