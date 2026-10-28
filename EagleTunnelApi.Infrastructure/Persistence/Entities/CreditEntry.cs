using System.Text.Json;

namespace EagleTunnelApi.Infrastructure.Persistence.Entities;

public sealed class CreditEntry
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Unit { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string? Currency { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? ReferenceType { get; set; }
    public string? ReferenceId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public JsonDocument? Metadata { get; set; }
}

public static class CreditEntryUnit
{
    public const string Days = "days";
    public const string Currency = "currency";
}
