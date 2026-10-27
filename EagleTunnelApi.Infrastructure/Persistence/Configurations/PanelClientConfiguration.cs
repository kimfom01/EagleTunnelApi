using EagleTunnelApi.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EagleTunnelApi.Infrastructure.Persistence.Configurations;

public sealed class PanelClientConfiguration : IEntityTypeConfiguration<PanelClient>
{
    public void Configure(EntityTypeBuilder<PanelClient> builder)
    {
        builder.ToTable("panel_clients");

        builder.HasKey(client => client.Id);

        builder.Property(client => client.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(client => client.PanelClientId)
            .HasColumnName("panel_client_id");

        builder.Property(client => client.EmailNormalized)
            .HasColumnName("email_normalized")
            .IsRequired();

        builder.Property(client => client.TelegramId)
            .HasColumnName("telegram_id");

        builder.Property(client => client.Enable)
            .HasColumnName("enable");

        builder.Property(client => client.ExpiryTime)
            .HasColumnName("expiry_time")
            .HasColumnType("timestamptz");

        builder.Property(client => client.TotalGb)
            .HasColumnName("total_gb");

        builder.Property(client => client.LimitIp)
            .HasColumnName("limit_ip");

        builder.Property(client => client.LimitHwid)
            .HasColumnName("limit_hwid");

        builder.Property(client => client.TrafficReset)
            .HasColumnName("traffic_reset");

        builder.Property(client => client.TrafficResetDay)
            .HasColumnName("traffic_reset_day");

        builder.Property(client => client.Reset)
            .HasColumnName("reset");

        builder.Property(client => client.SubId)
            .HasColumnName("sub_id");

        builder.Property(client => client.Flow)
            .HasColumnName("flow");

        builder.Property(client => client.Security)
            .HasColumnName("security");

        builder.Property(client => client.TrafficUp)
            .HasColumnName("traffic_up");

        builder.Property(client => client.TrafficDown)
            .HasColumnName("traffic_down");

        builder.Property(client => client.TrafficEnabled)
            .HasColumnName("traffic_enabled");

        builder.Property(client => client.LegacyComment)
            .HasColumnName("legacy_comment");

        builder.Property(client => client.PanelCreatedAt)
            .HasColumnName("panel_created_at")
            .HasColumnType("timestamptz");

        builder.Property(client => client.PanelUpdatedAt)
            .HasColumnName("panel_updated_at")
            .HasColumnType("timestamptz");

        builder.Property(client => client.LastSyncedAt)
            .HasColumnName("last_synced_at")
            .HasColumnType("timestamptz");

        builder.Property(client => client.IsDeleted)
            .HasColumnName("is_deleted")
            .HasDefaultValue(false);

        builder.Property(client => client.DeletedAt)
            .HasColumnName("deleted_at")
            .HasColumnType("timestamptz");

        builder.HasAlternateKey(client => client.PanelClientId);

        builder.HasIndex(client => client.EmailNormalized)
            .IsUnique()
            .HasDatabaseName("ux_panel_clients_email_normalized");

        builder.HasIndex(client => client.TelegramId)
            .IsUnique()
            .HasFilter("telegram_id IS NOT NULL")
            .HasDatabaseName("ux_panel_clients_telegram_id");

        builder.HasIndex(client => client.PanelUpdatedAt)
            .HasDatabaseName("ix_panel_clients_panel_updated_at");

        builder.HasIndex(client => new { client.IsDeleted, client.LastSyncedAt })
            .HasDatabaseName("ix_panel_clients_is_deleted_last_synced_at");

        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .IsRowVersion();
    }
}
