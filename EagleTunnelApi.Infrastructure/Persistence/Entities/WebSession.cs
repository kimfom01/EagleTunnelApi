using System.Net;

namespace EagleTunnelApi.Infrastructure.Persistence.Entities;

public sealed class WebSession
{
    public Guid Id { get; set; }
    public Guid AppUserId { get; set; }
    public byte[] TokenHash { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public IPAddress? Ip { get; set; }
    public string? UserAgent { get; set; }
}
