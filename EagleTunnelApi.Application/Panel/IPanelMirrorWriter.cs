namespace EagleTunnelApi.Application.Panel;

/// <summary>
/// Applies a full panel client snapshot to the Postgres mirror. The panel is
/// authoritative for panel fields; identity collisions are quarantined as
/// conflicts and never auto-merged.
/// </summary>
public interface IPanelMirrorWriter
{
    /// <summary>
    /// Upserts one full panel client snapshot, its inbounds, and its app user.
    /// Identity collisions keep the last-known-good identity and create or
    /// reopen a conflict row. The operation is transactional.
    /// </summary>
    Task<PanelMirrorWriteResult> UpsertAsync(PanelClientDetails client,
        PanelTrafficSummary? traffic, CancellationToken cancellationToken);
}

public sealed record PanelMirrorWriteResult(
    long PanelClientId,
    Guid? PanelClientRowId,
    Guid? AppUserId,
    bool Applied,
    IReadOnlyList<PanelMirrorConflict> Conflicts
);

public sealed record PanelMirrorConflict(
    Guid Id,
    string DedupeKey,
    string Type,
    bool IsNew,
    bool Reopened
);

public static class PanelConflictTypes
{
    public const string Email = "email";
    public const string Telegram = "telegram";
    public const string InvalidEmail = "invalid_email";
}
