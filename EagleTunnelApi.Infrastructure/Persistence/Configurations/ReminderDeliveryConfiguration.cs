using EagleTunnelApi.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EagleTunnelApi.Infrastructure.Persistence.Configurations;

public sealed class ReminderDeliveryConfiguration : IEntityTypeConfiguration<ReminderDelivery>
{
    public void Configure(EntityTypeBuilder<ReminderDelivery> builder)
    {
        builder.ToTable("reminder_deliveries", table =>
            table.HasCheckConstraint(
                "ck_reminder_deliveries_days_before_non_negative",
                "days_before >= 0"));

        builder.HasKey(delivery => delivery.Id);

        builder.Property(delivery => delivery.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(delivery => delivery.UserId)
            .HasColumnName("user_id");

        builder.Property(delivery => delivery.ExpiryDate)
            .HasColumnName("expiry_date")
            .HasColumnType("date");

        builder.Property(delivery => delivery.DaysBefore)
            .HasColumnName("days_before");

        builder.Property(delivery => delivery.SentAt)
            .HasColumnName("sent_at")
            .HasColumnType("timestamptz");

        builder.HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(delivery => delivery.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(delivery => new
        {
            delivery.UserId,
            delivery.ExpiryDate,
            delivery.DaysBefore
        })
            .IsUnique()
            .HasDatabaseName("ux_reminder_deliveries_user_expiry_days");
    }
}
