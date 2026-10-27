using EagleTunnelApi.Application.Auditing;
using EagleTunnelApi.Infrastructure.Persistence.Entities;

namespace EagleTunnelApi.Infrastructure.Persistence;

public sealed class EfAuditWriter(AppDbContext context) : IAuditWriter
{
    public Task AppendAsync(AuditEventRecord auditEvent, CancellationToken cancellationToken = default)
    {
        context.AuditEvents.Add(new AuditEvent
        {
            OccurredAt = auditEvent.OccurredAt,
            ActorUserId = auditEvent.ActorUserId,
            ActorType = auditEvent.ActorType,
            EventType = auditEvent.EventType,
            EmailNormalized = auditEvent.EmailNormalized,
            TelegramId = auditEvent.TelegramId,
            CorrelationId = auditEvent.CorrelationId,
            Ip = auditEvent.Ip,
            UserAgent = auditEvent.UserAgent,
            Metadata = auditEvent.Metadata
        });

        return Task.CompletedTask;
    }
}
