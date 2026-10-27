namespace EagleTunnelApi.Application.Panel;

/// <summary>
/// Sends administrator notifications for panel synchronisation conflicts and
/// repeated sync failures. Implementations must not expose tokens or
/// subscription URLs.
/// </summary>
public interface IPanelSyncNotifier
{
    /// <summary>
    /// Sends one message per unnoticed open conflict to every configured
    /// administrator, then records the notification time. Returns the number of
    /// conflicts notified.
    /// </summary>
    Task<int> NotifyOpenConflictsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Sends a failure alert for a run of consecutive sync failures. The
    /// caller decides when the failure run is long enough to notify.
    /// </summary>
    Task NotifySyncFailureAsync(int consecutiveFailures, string error,
        CancellationToken cancellationToken);
}
