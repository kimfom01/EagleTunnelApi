using EagleTunnelApi.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EagleTunnelApi.Infrastructure.Persistence.Configurations;

public sealed class PanelSyncConflictConfiguration : IEntityTypeConfiguration<PanelSyncConflict>
{
    public void Configure(EntityTypeBuilder<PanelSyncConflict> builder)
    {
        builder.ToTable("panel_sync_conflicts", table =>
            table.HasCheckConstraint(
                "ck_panel_sync_conflicts_status",
                "status IN ('open', 'resolved', 'dismissed')"));

        builder.HasKey(conflict => conflict.Id);

        builder.Property(conflict => conflict.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(conflict => conflict.DedupeKey)
            .HasColumnName("dedupe_key")
            .IsRequired();

        builder.Property(conflict => conflict.Type)
            .HasColumnName("type")
            .IsRequired();

        builder.Property(conflict => conflict.EmailNormalized)
            .HasColumnName("email_normalized");

        builder.Property(conflict => conflict.TelegramId)
            .HasColumnName("telegram_id");

        builder.Property(conflict => conflict.PanelClientId)
            .HasColumnName("panel_client_id");

        builder.Property(conflict => conflict.OtherPanelClientId)
            .HasColumnName("other_panel_client_id");

        builder.Property(conflict => conflict.AppUserId)
            .HasColumnName("app_user_id");

        builder.Property(conflict => conflict.Details)
            .HasColumnName("details")
            .HasColumnType("jsonb");

        builder.Property(conflict => conflict.Status)
            .HasColumnName("status")
            .HasDefaultValue(PanelSyncConflictStatus.Open)
            .IsRequired();

        builder.Property(conflict => conflict.FirstSeenAt)
            .HasColumnName("first_seen_at")
            .HasColumnType("timestamptz");

        builder.Property(conflict => conflict.LastSeenAt)
            .HasColumnName("last_seen_at")
            .HasColumnType("timestamptz");

        builder.Property(conflict => conflict.NotifiedAt)
            .HasColumnName("notified_at")
            .HasColumnType("timestamptz");

        builder.Property(conflict => conflict.ResolvedAt)
            .HasColumnName("resolved_at")
            .HasColumnType("timestamptz");

        builder.Property(conflict => conflict.ResolvedBy)
            .HasColumnName("resolved_by");

        builder.HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(conflict => conflict.AppUserId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(conflict => conflict.DedupeKey)
            .IsUnique()
            .HasDatabaseName("ux_panel_sync_conflicts_dedupe_key");

        builder.HasIndex(conflict => new { conflict.Status, conflict.LastSeenAt })
            .HasDatabaseName("ix_panel_sync_conflicts_status_last_seen_at");

        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .IsRowVersion();
    }
}
