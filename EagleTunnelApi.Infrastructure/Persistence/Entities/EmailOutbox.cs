namespace EagleTunnelApi.Infrastructure.Persistence.Entities;

public sealed class EmailOutbox
{
    public Guid Id { get; set; }
    public string TemplateKey { get; set; } = string.Empty;
    public string ToEmail { get; set; } = string.Empty;
    public byte[] PayloadProtected { get; set; } = [];
    public string Status { get; set; } = EmailOutboxStatus.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public string? LastError { get; set; }
    public string? DedupeKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
}

public static class EmailOutboxStatus
{
    public const string Pending = "pending";
    public const string Processing = "processing";
    public const string Sent = "sent";
    public const string Dead = "dead";
}
