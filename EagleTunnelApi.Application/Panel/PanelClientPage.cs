namespace EagleTunnelApi.Application.Panel;

/// <summary>
/// Request for the panel's server-side client page endpoint. The panel caps
/// <see cref="PageSize"/> at 200; callers are responsible for keeping requests
/// within that limit.
/// </summary>
public sealed record PanelClientPageRequest(
    int Page,
    int PageSize,
    string? Sort = null,
    string? Order = null,
    string? Search = null
);

/// <summary>
/// One page of slim panel client rows. <see cref="Total"/> is the row count in
/// the panel database and <see cref="Filtered"/> is the count after filters.
/// </summary>
public sealed record PanelClientPage(
    IReadOnlyList<PanelClientPageRow> Items,
    int Page,
    int PageSize,
    int Total,
    int Filtered
);

/// <summary>
/// Slim row returned by the paged endpoint. It intentionally omits credentials,
/// UUID, flow, security, reverse settings, Telegram ID, and comment; call
/// <see cref="IPanelGateway.GetClientByEmailAsync"/> for the full record.
/// </summary>
public sealed record PanelClientPageRow(
    string Email,
    string? SubId,
    bool Enable,
    long ExpiryTime,
    long TotalGB,
    int LimitIp,
    int LimitHwid,
    int Reset,
    int ResetDay,
    int ResetMax,
    int ResetWeekday,
    IReadOnlyList<int> InboundIds,
    PanelTrafficSummary? Traffic,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public static class PanelClientSortFields
{
    public const string Enable = "enable";
    public const string Email = "email";
    public const string InboundIds = "inboundIds";
    public const string Traffic = "traffic";
    public const string Remaining = "remaining";
    public const string ExpiryTime = "expiryTime";
    public const string CreatedAt = "createdAt";
    public const string UpdatedAt = "updatedAt";
    public const string LastOnline = "lastOnline";
}

public static class PanelSortOrders
{
    public const string Ascend = "ascend";
    public const string Descend = "descend";
}
