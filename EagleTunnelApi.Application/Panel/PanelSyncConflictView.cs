namespace EagleTunnelApi.Application.Panel;

/// <summary>
/// A conflict that could not be applied without choosing between conflicting
/// identities. Conflicts are quarantined, never merged automatically.
/// </summary>
public sealed record PanelSyncConflictView
{
    public required Guid Id { get; init; }
    public required string DedupeKey { get; init; }
    public required string Type { get; init; }
    public string? EmailNormalized { get; init; }
    public long? TelegramId { get; init; }
    public long? PanelClientId { get; init; }
    public long? OtherPanelClientId { get; init; }
    public Guid? AppUserId { get; init; }
    public string? DetailsJson { get; init; }
    public required string Status { get; init; }
    public DateTimeOffset FirstSeenAt { get; init; }
    public DateTimeOffset LastSeenAt { get; init; }
    public DateTimeOffset? NotifiedAt { get; init; }
    public DateTimeOffset? ResolvedAt { get; init; }
    public string? ResolvedBy { get; init; }
}

public static class PanelSyncConflictStates
{
    public const string Open = "open";
    public const string Resolved = "resolved";
    public const string Dismissed = "dismissed";

    public static bool IsKnown(string status)
    {
        return status is Open or Resolved or Dismissed;
    }

    public static bool IsResolution(string status)
    {
        return status is Resolved or Dismissed;
    }
}
