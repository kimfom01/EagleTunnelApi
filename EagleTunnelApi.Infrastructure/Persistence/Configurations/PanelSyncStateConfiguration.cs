using EagleTunnelApi.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EagleTunnelApi.Infrastructure.Persistence.Configurations;

public sealed class PanelSyncStateConfiguration : IEntityTypeConfiguration<PanelSyncState>
{
    public void Configure(EntityTypeBuilder<PanelSyncState> builder)
    {
        builder.ToTable("panel_sync_state", table =>
        {
            table.HasCheckConstraint(
                "ck_panel_sync_state_last_run_mode",
                "last_run_mode IS NULL OR last_run_mode IN ('cursor', 'full')");
            table.HasCheckConstraint(
                "ck_panel_sync_state_counts_non_negative",
                "last_seen_count >= 0 AND last_changed_count >= 0");
        });

        builder.HasKey(state => state.Id);

        builder.Property(state => state.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(state => state.CursorPanelUpdatedAt)
            .HasColumnName("cursor_panel_updated_at")
            .HasColumnType("timestamptz");

        builder.Property(state => state.LastRunMode)
            .HasColumnName("last_run_mode");

        builder.Property(state => state.LastStartedAt)
            .HasColumnName("last_started_at")
            .HasColumnType("timestamptz");

        builder.Property(state => state.LastCompletedAt)
            .HasColumnName("last_completed_at")
            .HasColumnType("timestamptz");

        builder.Property(state => state.LastSuccessAt)
            .HasColumnName("last_success_at")
            .HasColumnType("timestamptz");

        builder.Property(state => state.LastError)
            .HasColumnName("last_error");

        builder.Property(state => state.LastSeenCount)
            .HasColumnName("last_seen_count")
            .HasDefaultValue(0);

        builder.Property(state => state.LastChangedCount)
            .HasColumnName("last_changed_count")
            .HasDefaultValue(0);

        builder.Property(state => state.LockOwner)
            .HasColumnName("lock_owner");

        builder.Property(state => state.LockExpiresAt)
            .HasColumnName("lock_expires_at")
            .HasColumnType("timestamptz");

        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .IsRowVersion();
    }
}
