using System.Text.Json;

namespace EagleTunnelApi.Infrastructure.Persistence.Entities;

public sealed class PanelSyncConflict
{
    public Guid Id { get; set; }
    public string DedupeKey { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? EmailNormalized { get; set; }
    public long? TelegramId { get; set; }
    public long? PanelClientId { get; set; }
    public long? OtherPanelClientId { get; set; }
    public Guid? AppUserId { get; set; }
    public JsonDocument? Details { get; set; }
    public string Status { get; set; } = PanelSyncConflictStatus.Open;
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? NotifiedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public string? ResolvedBy { get; set; }
}

public static class PanelSyncConflictStatus
{
    public const string Open = "open";
    public const string Resolved = "resolved";
    public const string Dismissed = "dismissed";
}
