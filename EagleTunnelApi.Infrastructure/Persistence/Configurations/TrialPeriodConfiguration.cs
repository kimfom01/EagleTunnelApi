using EagleTunnelApi.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EagleTunnelApi.Infrastructure.Persistence.Configurations;

public sealed class TrialPeriodConfiguration : IEntityTypeConfiguration<TrialPeriod>
{
    public void Configure(EntityTypeBuilder<TrialPeriod> builder)
    {
        builder.ToTable("trial_periods", table =>
            table.HasCheckConstraint(
                "ck_trial_periods_period_valid",
                "ends_at > starts_at"));

        builder.HasKey(period => period.Id);

        builder.Property(period => period.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(period => period.UserId)
            .HasColumnName("user_id");

        builder.Property(period => period.StartsAt)
            .HasColumnName("starts_at")
            .HasColumnType("timestamptz");

        builder.Property(period => period.EndsAt)
            .HasColumnName("ends_at")
            .HasColumnType("timestamptz");

        builder.Property(period => period.Source)
            .HasColumnName("source")
            .IsRequired();

        builder.Property(period => period.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz");

        builder.HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(period => period.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .IsRowVersion();
    }
}
