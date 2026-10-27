using EagleTunnelApi.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EagleTunnelApi.Infrastructure.Persistence.Configurations;

public sealed class CreditApplicationConfiguration : IEntityTypeConfiguration<CreditApplication>
{
    public void Configure(EntityTypeBuilder<CreditApplication> builder)
    {
        builder.ToTable("credit_applications");

        builder.HasKey(application => application.Id);

        builder.Property(application => application.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(application => application.UserId)
            .HasColumnName("user_id");

        builder.Property(application => application.EntryId)
            .HasColumnName("entry_id");

        builder.Property(application => application.AppliedAt)
            .HasColumnName("applied_at")
            .HasColumnType("timestamptz");

        builder.Property(application => application.PanelExpiryBefore)
            .HasColumnName("panel_expiry_before")
            .HasColumnType("timestamptz");

        builder.Property(application => application.PanelExpiryAfter)
            .HasColumnName("panel_expiry_after")
            .HasColumnType("timestamptz");

        builder.HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(application => application.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<CreditEntry>()
            .WithMany()
            .HasForeignKey(application => application.EntryId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(application => application.EntryId)
            .IsUnique()
            .HasDatabaseName("ux_credit_applications_entry_id");
    }
}
