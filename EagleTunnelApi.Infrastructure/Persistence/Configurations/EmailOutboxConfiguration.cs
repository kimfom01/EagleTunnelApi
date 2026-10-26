using EagleTunnelApi.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EagleTunnelApi.Infrastructure.Persistence.Configurations;

public sealed class EmailOutboxConfiguration : IEntityTypeConfiguration<EmailOutbox>
{
    public void Configure(EntityTypeBuilder<EmailOutbox> builder)
    {
        builder.ToTable("email_outbox", table =>
        {
            table.HasCheckConstraint(
                "ck_email_outbox_attempts_non_negative",
                "attempts >= 0");
            table.HasCheckConstraint(
                "ck_email_outbox_status",
                "status IN ('pending', 'processing', 'sent', 'dead')");
        });

        builder.HasKey(email => email.Id);

        builder.Property(email => email.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(email => email.TemplateKey)
            .HasColumnName("template_key")
            .IsRequired();

        builder.Property(email => email.ToEmail)
            .HasColumnName("to_email")
            .IsRequired();

        builder.Property(email => email.PayloadProtected)
            .HasColumnName("payload_protected")
            .HasColumnType("bytea")
            .IsRequired();

        builder.Property(email => email.Status)
            .HasColumnName("status")
            .HasDefaultValue(EmailOutboxStatus.Pending)
            .IsRequired();

        builder.Property(email => email.Attempts)
            .HasColumnName("attempts")
            .HasDefaultValue(0);

        builder.Property(email => email.NextAttemptAt)
            .HasColumnName("next_attempt_at")
            .HasColumnType("timestamptz");

        builder.Property(email => email.LockedUntil)
            .HasColumnName("locked_until")
            .HasColumnType("timestamptz");

        builder.Property(email => email.LastError)
            .HasColumnName("last_error");

        builder.Property(email => email.DedupeKey)
            .HasColumnName("dedupe_key");

        builder.Property(email => email.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz");

        builder.Property(email => email.SentAt)
            .HasColumnName("sent_at")
            .HasColumnType("timestamptz");

        builder.HasIndex(email => new { email.Status, email.NextAttemptAt })
            .HasDatabaseName("ix_email_outbox_status_next_attempt_at");

        builder.HasIndex(email => email.CreatedAt)
            .HasDatabaseName("ix_email_outbox_created_at");

        builder.HasIndex(email => email.DedupeKey)
            .IsUnique()
            .HasFilter("dedupe_key IS NOT NULL AND status IN ('pending', 'processing')")
            .HasDatabaseName("ux_email_outbox_pending_dedupe_key");

        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .IsRowVersion();
    }
}
