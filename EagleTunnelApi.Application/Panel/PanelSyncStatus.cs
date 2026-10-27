namespace EagleTunnelApi.Application.Panel;

/// <summary>
/// Last recorded panel sync state. Null times and zero counts mean the sync
/// has not completed a run yet.
/// </summary>
public sealed record PanelSyncStatus
{
    public string? LastRunMode { get; init; }
    public DateTimeOffset? LastStartedAt { get; init; }
    public DateTimeOffset? LastCompletedAt { get; init; }
    public DateTimeOffset? LastSuccessAt { get; init; }
    public string? LastError { get; init; }
    public int LastSeenCount { get; init; }
    public int LastChangedCount { get; init; }
    public DateTimeOffset? CursorPanelUpdatedAt { get; init; }
    public string? LockOwner { get; init; }
    public DateTimeOffset? LockExpiresAt { get; init; }
}
