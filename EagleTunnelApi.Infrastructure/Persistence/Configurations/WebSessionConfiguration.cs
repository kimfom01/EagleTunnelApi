using EagleTunnelApi.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EagleTunnelApi.Infrastructure.Persistence.Configurations;

public sealed class WebSessionConfiguration : IEntityTypeConfiguration<WebSession>
{
    public void Configure(EntityTypeBuilder<WebSession> builder)
    {
        builder.ToTable("web_sessions");

        builder.HasKey(session => session.Id);

        builder.Property(session => session.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(session => session.AppUserId)
            .HasColumnName("app_user_id");

        builder.Property(session => session.TokenHash)
            .HasColumnName("token_hash")
            .HasColumnType("bytea")
            .IsRequired();

        builder.Property(session => session.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz");

        builder.Property(session => session.LastSeenAt)
            .HasColumnName("last_seen_at")
            .HasColumnType("timestamptz");

        builder.Property(session => session.ExpiresAt)
            .HasColumnName("expires_at")
            .HasColumnType("timestamptz");

        builder.Property(session => session.RevokedAt)
            .HasColumnName("revoked_at")
            .HasColumnType("timestamptz");

        builder.Property(session => session.Ip)
            .HasColumnName("ip")
            .HasColumnType("inet");

        builder.Property(session => session.UserAgent)
            .HasColumnName("user_agent");

        builder.HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(session => session.AppUserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(session => session.TokenHash)
            .IsUnique()
            .HasDatabaseName("ux_web_sessions_token_hash");

        builder.HasIndex(session => new { session.AppUserId, session.ExpiresAt })
            .HasDatabaseName("ix_web_sessions_app_user_id_expires_at");

        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .IsRowVersion();
    }
}
