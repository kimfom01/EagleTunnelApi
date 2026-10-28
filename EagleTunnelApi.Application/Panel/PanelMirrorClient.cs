namespace EagleTunnelApi.Application.Panel;

/// <summary>
/// A client row read from the Postgres panel mirror. The panel remains the
/// authority for these values; the mirror is refreshed by sync or manual
/// refresh.
/// </summary>
public sealed record PanelMirrorClient
{
    public required Guid Id { get; init; }
    public required long PanelClientId { get; init; }
    public required string EmailNormalized { get; init; }
    public long? TelegramId { get; init; }
    public bool Enable { get; init; }
    public DateTimeOffset? ExpiryTime { get; init; }
    public long TotalGb { get; init; }
    public int LimitIp { get; init; }
    public int LimitHwid { get; init; }
    public string? TrafficReset { get; init; }
    public int? TrafficResetDay { get; init; }
    public int Reset { get; init; }
    public string? SubId { get; init; }
    public string? Flow { get; init; }
    public string? Security { get; init; }
    public long TrafficUp { get; init; }
    public long TrafficDown { get; init; }
    public bool? TrafficEnabled { get; init; }
    public string? LegacyComment { get; init; }
    public DateTimeOffset? PanelCreatedAt { get; init; }
    public DateTimeOffset? PanelUpdatedAt { get; init; }
    public DateTimeOffset LastSyncedAt { get; init; }
    public bool IsDeleted { get; init; }
    public DateTimeOffset? DeletedAt { get; init; }
    public IReadOnlyList<int> InboundIds { get; init; } = [];
}
