namespace EagleTunnelApi.Application.Panel;

public sealed record PanelClientSummary(
    int Id,
    string Email,
    string? SubId,
    string? Uuid,
    long TotalGB,
    long ExpiryTime,
    bool Enable,
    IReadOnlyList<int>? InboundIds,
    PanelTrafficSummary? Traffic
);

public sealed record PanelTrafficSummary(long Up, long Down, bool Enable);
