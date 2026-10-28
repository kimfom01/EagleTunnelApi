namespace EagleTunnelApi.Application.Auditing;

public interface IAuditWriter
{
    Task AppendAsync(AuditEventRecord auditEvent, CancellationToken cancellationToken = default);
}
