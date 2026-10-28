using EagleTunnelApi.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EagleTunnelApi.Infrastructure.Persistence.Configurations;

public sealed class PanelClientInboundConfiguration : IEntityTypeConfiguration<PanelClientInbound>
{
    public void Configure(EntityTypeBuilder<PanelClientInbound> builder)
    {
        builder.ToTable("panel_client_inbounds");

        builder.HasKey(clientInbound => new
        {
            clientInbound.PanelClientId,
            clientInbound.InboundId
        });

        builder.Property(clientInbound => clientInbound.PanelClientId)
            .HasColumnName("panel_client_id");

        builder.Property(clientInbound => clientInbound.InboundId)
            .HasColumnName("inbound_id");

        builder.HasOne<PanelClient>()
            .WithMany()
            .HasForeignKey(clientInbound => clientInbound.PanelClientId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<PanelInbound>()
            .WithMany()
            .HasForeignKey(clientInbound => clientInbound.InboundId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
