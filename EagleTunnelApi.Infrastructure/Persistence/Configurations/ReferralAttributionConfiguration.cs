using EagleTunnelApi.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EagleTunnelApi.Infrastructure.Persistence.Configurations;

public sealed class ReferralAttributionConfiguration : IEntityTypeConfiguration<ReferralAttribution>
{
    public void Configure(EntityTypeBuilder<ReferralAttribution> builder)
    {
        builder.ToTable("referral_attributions", table =>
        {
            table.HasCheckConstraint(
                "ck_referral_attributions_status",
                "status IN ('pending', 'paid', 'void')");
            table.HasCheckConstraint(
                "ck_referral_attributions_source",
                "source IN ('telegram', 'admin', 'migration')");
        });

        builder.HasKey(attribution => attribution.Id);

        builder.Property(attribution => attribution.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(attribution => attribution.ReferrerUserId)
            .HasColumnName("referrer_user_id");

        builder.Property(attribution => attribution.RefereeUserId)
            .HasColumnName("referee_user_id");

        builder.Property(attribution => attribution.ReferrerEmail)
            .HasColumnName("referrer_email");

        builder.Property(attribution => attribution.ReferrerTelegramId)
            .HasColumnName("referrer_telegram_id");

        builder.Property(attribution => attribution.Status)
            .HasColumnName("status")
            .HasDefaultValue(ReferralAttributionStatus.Pending)
            .IsRequired();

        builder.Property(attribution => attribution.Source)
            .HasColumnName("source")
            .IsRequired();

        builder.Property(attribution => attribution.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz");

        builder.Property(attribution => attribution.PaidAt)
            .HasColumnName("paid_at")
            .HasColumnType("timestamptz");

        builder.Property(attribution => attribution.IdempotencyKey)
            .HasColumnName("idempotency_key")
            .IsRequired();

        builder.HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(attribution => attribution.ReferrerUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(attribution => attribution.RefereeUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(attribution => attribution.IdempotencyKey)
            .IsUnique()
            .HasDatabaseName("ux_referral_attributions_idempotency_key");

        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .IsRowVersion();
    }
}
