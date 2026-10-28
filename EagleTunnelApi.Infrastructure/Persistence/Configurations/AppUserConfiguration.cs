using EagleTunnelApi.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EagleTunnelApi.Infrastructure.Persistence.Configurations;

public sealed class AppUserConfiguration : IEntityTypeConfiguration<AppUser>
{
    public void Configure(EntityTypeBuilder<AppUser> builder)
    {
        builder.ToTable("app_users");

        builder.HasKey(user => user.Id);

        builder.Property(user => user.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(user => user.EmailNormalized)
            .HasColumnName("email_normalized")
            .IsRequired();

        builder.Property(user => user.TelegramId)
            .HasColumnName("telegram_id");

        builder.Property(user => user.PanelClientId)
            .HasColumnName("panel_client_id");

        builder.Property(user => user.EmailVerifiedAt)
            .HasColumnName("email_verified_at")
            .HasColumnType("timestamptz");

        builder.Property(user => user.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz");

        builder.Property(user => user.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("timestamptz");

        builder.Property(user => user.LastLoginAt)
            .HasColumnName("last_login_at")
            .HasColumnType("timestamptz");

        builder.HasIndex(user => user.EmailNormalized)
            .IsUnique()
            .HasDatabaseName("ux_app_users_email_normalized");

        builder.HasIndex(user => user.TelegramId)
            .IsUnique()
            .HasFilter("telegram_id IS NOT NULL")
            .HasDatabaseName("ux_app_users_telegram_id");

        builder.HasIndex(user => user.PanelClientId)
            .IsUnique()
            .HasDatabaseName("ux_app_users_panel_client_id");

        builder.HasOne<PanelClient>()
            .WithOne()
            .HasForeignKey<AppUser>(user => user.PanelClientId)
            .HasPrincipalKey<PanelClient>(client => client.PanelClientId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .IsRowVersion();
    }
}
