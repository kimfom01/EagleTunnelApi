namespace EagleTunnelApi.Configuration;

/// <summary>
/// Controls the recurring panel synchronisation, full reconciliation cadence,
/// and administrator notifications.
/// </summary>
public sealed class PanelSyncOptions
{
    public const string SectionName = "PanelSync";

    /// <summary>Minutes between cursor sync runs. Defaults to 10.</summary>
    public int IntervalMinutes { get; set; } = 10;

    /// <summary>
    /// Hours between full reconciliation scans used to detect deletions.
    /// Defaults to 24.
    /// </summary>
    public int FullReconcileHours { get; set; } = 24;

    /// <summary>
    /// Consecutive failures required before administrators are alerted. A
    /// single transient timeout does not notify. Defaults to 3.
    /// </summary>
    public int FailureAlertThreshold { get; set; } = 3;

    /// <summary>Maximum number of open conflicts notified in one pass.</summary>
    public int ConflictNotificationBatchSize { get; set; } = 50;

    /// <summary>
    /// Runs the legacy comment backfill once per process after the first
    /// successful sync. Disable after the comment read switch.
    /// </summary>
    public bool CommentBackfillOnStartup { get; set; } = true;
}
