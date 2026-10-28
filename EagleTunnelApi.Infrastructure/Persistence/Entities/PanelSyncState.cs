namespace EagleTunnelApi.Infrastructure.Persistence.Entities;

public sealed class PanelSyncState
{
    public string Id { get; set; } = string.Empty;
    public DateTimeOffset? CursorPanelUpdatedAt { get; set; }
    public string? LastRunMode { get; set; }
    public DateTimeOffset? LastStartedAt { get; set; }
    public DateTimeOffset? LastCompletedAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public string? LastError { get; set; }
    public int LastSeenCount { get; set; }
    public int LastChangedCount { get; set; }
    public string? LockOwner { get; set; }
    public DateTimeOffset? LockExpiresAt { get; set; }
}

public static class PanelSyncStateIds
{
    public const string Default = "default";
}

public static class PanelSyncRunModes
{
    public const string Cursor = "cursor";
    public const string Full = "full";
}
