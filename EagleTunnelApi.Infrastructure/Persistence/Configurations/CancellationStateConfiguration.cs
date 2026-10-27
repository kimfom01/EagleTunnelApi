using EagleTunnelApi.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EagleTunnelApi.Infrastructure.Persistence.Configurations;

public sealed class CancellationStateConfiguration : IEntityTypeConfiguration<CancellationState>
{
    public void Configure(EntityTypeBuilder<CancellationState> builder)
    {
        builder.ToTable("cancellation_state");

        builder.HasKey(state => state.UserId);

        builder.Property(state => state.UserId)
            .HasColumnName("user_id")
            .ValueGeneratedNever();

        builder.Property(state => state.CancelledAt)
            .HasColumnName("cancelled_at")
            .HasColumnType("timestamptz");

        builder.Property(state => state.ExpiresOn)
            .HasColumnName("expires_on")
            .HasColumnType("date");

        builder.Property(state => state.ClearedAt)
            .HasColumnName("cleared_at")
            .HasColumnType("timestamptz");

        builder.HasOne<AppUser>()
            .WithOne()
            .HasForeignKey<CancellationState>(state => state.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .IsRowVersion();
    }
}
