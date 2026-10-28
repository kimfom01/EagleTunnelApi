namespace EagleTunnelApi.Application.Panel;

/// <summary>
/// Reads the Postgres panel mirror and owns the application-side panel
/// settings. This port never performs live panel I/O.
/// </summary>
public interface IPanelDirectory
{
    /// <summary>
    /// Finds a non-deleted mirrored client by email. The lookup is
    /// case-insensitive and returns <see langword="null"/> when the mirror has
    /// no matching client.
    /// </summary>
    Task<PanelMirrorClient?> GetClientByEmailAsync(string email, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the last recorded sync status, or an empty status when the sync
    /// has never run.
    /// </summary>
    Task<PanelSyncStatus> GetSyncStatusAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Returns conflicts with the requested status. A null status defaults to
    /// open conflicts. Results are ordered by most recently seen first.
    /// </summary>
    Task<IReadOnlyList<PanelSyncConflictView>> GetSyncConflictsAsync(string? status, int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Marks an open conflict as resolved or dismissed and records who made the
    /// decision. The operation is idempotent when the target status is already
    /// set.
    /// </summary>
    Task ResolveSyncConflictAsync(Guid conflictId, string status, string actor,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns every mirrored inbound and whether it is part of the default set
    /// used for new clients.
    /// </summary>
    Task<IReadOnlyList<PanelInboundSetting>> GetInboundSettingsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Returns the effective default inbound ids. On the first call, if the
    /// mirror has no defaults, the configured fallback ids are seeded into
    /// <c>panel_inbounds</c>. If the database is unavailable, the configured
    /// fallback is returned without persisting it.
    /// </summary>
    Task<IReadOnlyList<int>> GetDefaultInboundIdsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the default inbound set used for new clients. The selected set
    /// must not be empty. Available panel inbounds are upserted into the mirror,
    /// and the old and new sets are audited.
    /// </summary>
    Task SetDefaultInboundsAsync(IReadOnlyList<int> inboundIds,
        IReadOnlyList<PanelInboundOption> availableInbounds, string actor,
        CancellationToken cancellationToken);
}
