using EagleTunnelApi.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EagleTunnelApi.Infrastructure.Persistence.Configurations;

public sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.ToTable("audit_events");

        builder.HasKey(auditEvent => auditEvent.Id);

        builder.Property(auditEvent => auditEvent.Id)
            .HasColumnName("id")
            .UseIdentityAlwaysColumn();

        builder.Property(auditEvent => auditEvent.OccurredAt)
            .HasColumnName("occurred_at")
            .HasColumnType("timestamptz");

        builder.Property(auditEvent => auditEvent.ActorUserId)
            .HasColumnName("actor_user_id");

        builder.Property(auditEvent => auditEvent.ActorType)
            .HasColumnName("actor_type")
            .IsRequired();

        builder.Property(auditEvent => auditEvent.EventType)
            .HasColumnName("event_type")
            .IsRequired();

        builder.Property(auditEvent => auditEvent.EmailNormalized)
            .HasColumnName("email_normalized");

        builder.Property(auditEvent => auditEvent.TelegramId)
            .HasColumnName("telegram_id");

        builder.Property(auditEvent => auditEvent.CorrelationId)
            .HasColumnName("correlation_id");

        builder.Property(auditEvent => auditEvent.Ip)
            .HasColumnName("ip")
            .HasColumnType("inet");

        builder.Property(auditEvent => auditEvent.UserAgent)
            .HasColumnName("user_agent");

        builder.Property(auditEvent => auditEvent.Metadata)
            .HasColumnName("metadata")
            .HasColumnType("jsonb");

        builder.HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(auditEvent => auditEvent.ActorUserId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(auditEvent => auditEvent.OccurredAt)
            .HasDatabaseName("ix_audit_events_occurred_at");

        builder.HasIndex(auditEvent => new { auditEvent.ActorUserId, auditEvent.OccurredAt })
            .HasDatabaseName("ix_audit_events_actor_user_id_occurred_at");

        builder.HasIndex(auditEvent => new { auditEvent.EventType, auditEvent.OccurredAt })
            .HasDatabaseName("ix_audit_events_event_type_occurred_at");
    }
}
