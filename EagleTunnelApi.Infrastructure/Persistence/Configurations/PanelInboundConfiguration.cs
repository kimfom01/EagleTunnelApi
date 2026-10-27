using EagleTunnelApi.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EagleTunnelApi.Infrastructure.Persistence.Configurations;

public sealed class PanelInboundConfiguration : IEntityTypeConfiguration<PanelInbound>
{
    public void Configure(EntityTypeBuilder<PanelInbound> builder)
    {
        builder.ToTable("panel_inbounds");

        builder.HasKey(inbound => inbound.InboundId);

        builder.Property(inbound => inbound.InboundId)
            .HasColumnName("inbound_id")
            .ValueGeneratedNever();

        builder.Property(inbound => inbound.Remark)
            .HasColumnName("remark");

        builder.Property(inbound => inbound.Tag)
            .HasColumnName("tag");

        builder.Property(inbound => inbound.Protocol)
            .HasColumnName("protocol");

        builder.Property(inbound => inbound.Port)
            .HasColumnName("port");

        builder.Property(inbound => inbound.IsDefaultForNewClients)
            .HasColumnName("is_default_for_new_clients")
            .HasDefaultValue(false);

        builder.Property(inbound => inbound.SortOrder)
            .HasColumnName("sort_order")
            .HasDefaultValue(0);

        builder.Property(inbound => inbound.LastSyncedAt)
            .HasColumnName("last_synced_at")
            .HasColumnType("timestamptz");

        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .IsRowVersion();
    }
}
