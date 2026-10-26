using System.Net;
using System.Text.Json;

namespace EagleTunnelApi.Infrastructure.Persistence.Entities;

public sealed class AuditEvent
{
    public long Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public Guid? ActorUserId { get; set; }
    public string ActorType { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string? EmailNormalized { get; set; }
    public long? TelegramId { get; set; }
    public string? CorrelationId { get; set; }
    public IPAddress? Ip { get; set; }
    public string? UserAgent { get; set; }
    public JsonDocument? Metadata { get; set; }
}
