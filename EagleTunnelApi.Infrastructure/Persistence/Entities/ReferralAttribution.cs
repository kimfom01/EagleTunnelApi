namespace EagleTunnelApi.Infrastructure.Persistence.Entities;

public sealed class ReferralAttribution
{
    public Guid Id { get; set; }
    public Guid? ReferrerUserId { get; set; }
    public Guid? RefereeUserId { get; set; }
    public string? ReferrerEmail { get; set; }
    public long? ReferrerTelegramId { get; set; }
    public string Status { get; set; } = ReferralAttributionStatus.Pending;
    public string Source { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
}

public static class ReferralAttributionStatus
{
    public const string Pending = "pending";
    public const string Paid = "paid";
    public const string Void = "void";
}

public static class ReferralAttributionSource
{
    public const string Telegram = "telegram";
    public const string Admin = "admin";
    public const string Migration = "migration";
}
