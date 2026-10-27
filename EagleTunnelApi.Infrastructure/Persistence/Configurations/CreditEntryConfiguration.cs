using EagleTunnelApi.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EagleTunnelApi.Infrastructure.Persistence.Configurations;

public sealed class CreditEntryConfiguration : IEntityTypeConfiguration<CreditEntry>
{
    public void Configure(EntityTypeBuilder<CreditEntry> builder)
    {
        builder.ToTable("credit_entries", table =>
        {
            table.HasCheckConstraint(
                "ck_credit_entries_unit",
                "unit IN ('days', 'currency')");
            table.HasCheckConstraint(
                "ck_credit_entries_currency",
                "(unit = 'currency' AND currency IS NOT NULL) OR (unit = 'days' AND currency IS NULL)");
            table.HasCheckConstraint(
                "ck_credit_entries_amount_non_zero",
                "amount <> 0");
        });

        builder.HasKey(entry => entry.Id);

        builder.Property(entry => entry.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(entry => entry.UserId)
            .HasColumnName("user_id");

        builder.Property(entry => entry.Unit)
            .HasColumnName("unit")
            .IsRequired();

        builder.Property(entry => entry.Amount)
            .HasColumnName("amount")
            .HasColumnType("numeric(18,4)");

        builder.Property(entry => entry.Currency)
            .HasColumnName("currency")
            .HasColumnType("character(3)");

        builder.Property(entry => entry.Reason)
            .HasColumnName("reason")
            .IsRequired();

        builder.Property(entry => entry.ReferenceType)
            .HasColumnName("reference_type");

        builder.Property(entry => entry.ReferenceId)
            .HasColumnName("reference_id");

        builder.Property(entry => entry.IdempotencyKey)
            .HasColumnName("idempotency_key")
            .IsRequired();

        builder.Property(entry => entry.OccurredAt)
            .HasColumnName("occurred_at")
            .HasColumnType("timestamptz");

        builder.Property(entry => entry.ExpiresAt)
            .HasColumnName("expires_at")
            .HasColumnType("timestamptz");

        builder.Property(entry => entry.Metadata)
            .HasColumnName("metadata")
            .HasColumnType("jsonb");

        builder.HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(entry => entry.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(entry => entry.IdempotencyKey)
            .IsUnique()
            .HasDatabaseName("ux_credit_entries_idempotency_key");

        builder.HasIndex(entry => new { entry.UserId, entry.Unit, entry.OccurredAt })
            .HasDatabaseName("ix_credit_entries_user_id_unit_occurred_at");
    }
}
