namespace EagleTunnelApi.Infrastructure.Persistence.Entities;

public sealed class AppUser
{
    public Guid Id { get; set; }
    public string EmailNormalized { get; set; } = string.Empty;
    public long? TelegramId { get; set; }
    public long PanelClientId { get; set; }
    public DateTimeOffset? EmailVerifiedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
}
