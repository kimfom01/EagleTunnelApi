using System.Net;
using System.Text.Json;

namespace EagleTunnelApi.Application.Auditing;

public sealed record AuditEventRecord
{
    public required DateTimeOffset OccurredAt { get; init; }

    public Guid? ActorUserId { get; init; }

    public required string ActorType { get; init; }

    public required string EventType { get; init; }

    public string? EmailNormalized { get; init; }

    public long? TelegramId { get; init; }

    public string? CorrelationId { get; init; }

    public IPAddress? Ip { get; init; }

    public string? UserAgent { get; init; }

    public JsonDocument? Metadata { get; init; }
}
