namespace EagleTunnelApi.Infrastructure.Persistence.Entities;

public sealed class PanelClient
{
    public Guid Id { get; set; }
    public long PanelClientId { get; set; }
    public string EmailNormalized { get; set; } = string.Empty;
    public long? TelegramId { get; set; }
    public bool Enable { get; set; }
    public DateTimeOffset? ExpiryTime { get; set; }
    public long TotalGb { get; set; }
    public int LimitIp { get; set; }
    public int LimitHwid { get; set; }
    public string? TrafficReset { get; set; }
    public int? TrafficResetDay { get; set; }
    public int Reset { get; set; }
    public string? SubId { get; set; }
    public string? Flow { get; set; }
    public string? Security { get; set; }
    public long TrafficUp { get; set; }
    public long TrafficDown { get; set; }
    public bool? TrafficEnabled { get; set; }
    public string? LegacyComment { get; set; }
    public DateTimeOffset? PanelCreatedAt { get; set; }
    public DateTimeOffset? PanelUpdatedAt { get; set; }
    public DateTimeOffset LastSyncedAt { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}
