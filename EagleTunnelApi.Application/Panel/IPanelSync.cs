namespace EagleTunnelApi.Application.Panel;

public enum PanelSyncMode
{
    Cursor,
    Full
}

/// <summary>
/// Runs the recurring panel synchronisation and records its lease and last-run
/// status in Postgres. The panel is authoritative; conflicts are persisted,
/// never merged.
/// </summary>
public interface IPanelSync
{
    /// <summary>
    /// Runs a cursor or full synchronisation. A cursor request without a cursor
    /// or with an empty mirror is upgraded to a full scan. Returns a skipped
    /// result when another run owns the lease.
    /// </summary>
    Task<PanelSyncResult> RunAsync(PanelSyncMode mode, string owner,
        CancellationToken cancellationToken);
}

public sealed record PanelSyncResult(
    bool Skipped,
    string Mode,
    int Seen,
    int Changed,
    int Deleted,
    int Conflicts,
    int Failures,
    string? Error,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string? Reason
);
