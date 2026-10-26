using EagleTunnelApi.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EagleTunnelApi.Infrastructure.Persistence.Configurations;

public sealed class EmailLoginCodeConfiguration : IEntityTypeConfiguration<EmailLoginCode>
{
    public void Configure(EntityTypeBuilder<EmailLoginCode> builder)
    {
        builder.ToTable("email_login_codes", table =>
            table.HasCheckConstraint(
                "ck_email_login_codes_attempts_non_negative",
                "attempts >= 0"));

        builder.HasKey(code => code.Id);

        builder.Property(code => code.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(code => code.PanelClientId)
            .HasColumnName("panel_client_id");

        builder.Property(code => code.AppUserId)
            .HasColumnName("app_user_id");

        builder.Property(code => code.EmailNormalized)
            .HasColumnName("email_normalized")
            .IsRequired();

        builder.Property(code => code.CodeHash)
            .HasColumnName("code_hash")
            .HasColumnType("bytea")
            .IsRequired();

        builder.Property(code => code.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz");

        builder.Property(code => code.ExpiresAt)
            .HasColumnName("expires_at")
            .HasColumnType("timestamptz");

        builder.Property(code => code.ConsumedAt)
            .HasColumnName("consumed_at")
            .HasColumnType("timestamptz");

        builder.Property(code => code.Attempts)
            .HasColumnName("attempts")
            .HasDefaultValue(0);

        builder.Property(code => code.RequestIp)
            .HasColumnName("request_ip")
            .HasColumnType("inet");

        builder.HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(code => code.AppUserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(code => new { code.EmailNormalized, code.ExpiresAt })
            .HasDatabaseName("ix_email_login_codes_email_normalized_expires_at");

        builder.HasIndex(code => new { code.PanelClientId, code.ExpiresAt })
            .HasDatabaseName("ix_email_login_codes_panel_client_id_expires_at");

        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .IsRowVersion();
    }
}
