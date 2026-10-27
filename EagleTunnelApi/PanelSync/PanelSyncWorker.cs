using EagleTunnelApi.Application.Panel;
using EagleTunnelApi.Configuration;
using EagleTunnelApi.Infrastructure.Persistence.Entities;
using Microsoft.Extensions.Options;

namespace EagleTunnelApi.PanelSync;

/// <summary>
/// Runs panel synchronisation immediately at startup and then on a configurable
/// interval. The Postgres lease in <see cref="IPanelSync"/> keeps multiple hosts
/// from applying the same run concurrently.
/// </summary>
public sealed class PanelSyncWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<PanelSyncOptions> options,
    TimeProvider timeProvider,
    ILogger<PanelSyncWorker> logger) : BackgroundService
{
    private readonly PanelSyncOptions _options = options.Value;
    private int _consecutiveFailures;
    private bool _commentBackfillCompleted;
    private DateTimeOffset? _lastFullRunAt;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunOnceSafeAsync(stoppingToken);

        using var timer = new PeriodicTimer(
            TimeSpan.FromMinutes(_options.IntervalMinutes), timeProvider);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken)) return;
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await RunOnceSafeAsync(stoppingToken);
        }
    }

    /// <summary>
    /// Performs one worker cycle. Exposed for tests and for callers that need a
    /// synchronous "run now" operation without starting the host.
    /// </summary>
    public async Task<PanelSyncWorkerResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var services = scope.ServiceProvider;

        try
        {
            var status = await services.GetRequiredService<IPanelDirectory>()
                .GetSyncStatusAsync(cancellationToken);
            var mode = SelectMode(status, timeProvider.GetUtcNow());

            logger.LogInformation("Starting panel sync worker run in {Mode} mode.", mode);

            var result = await services.GetRequiredService<IPanelSync>().RunAsync(
                mode, $"panel-sync:{Guid.NewGuid():N}", cancellationToken);

            if (result.Skipped)
            {
                logger.LogInformation("Panel sync worker run skipped: {Reason}", result.Reason);
                return new PanelSyncWorkerResult(result, false, _consecutiveFailures);
            }

            if (result.Error is not null)
            {
                var failures = Interlocked.Increment(ref _consecutiveFailures);

                logger.LogError(
                    "Panel sync failed. Consecutive failures: {ConsecutiveFailures}. Error: {Error}",
                    failures, result.Error);

                await NotifyFailureAsync(services, failures, result.Error, cancellationToken);

                return new PanelSyncWorkerResult(result, false, failures);
            }

            Interlocked.Exchange(ref _consecutiveFailures, 0);

            if (result.Mode == PanelSyncRunModes.Full) _lastFullRunAt = timeProvider.GetUtcNow();

            logger.LogInformation(
                "Panel sync complete. Mode: {Mode}, seen: {Seen}, changed: {Changed}, " +
                "deleted: {Deleted}, conflicts: {Conflicts}",
                result.Mode, result.Seen, result.Changed, result.Deleted, result.Conflicts);

            var commentsRan = await TryBackfillCommentsAsync(services, cancellationToken);
            await NotifyConflictsAsync(services, cancellationToken);

            return new PanelSyncWorkerResult(result, commentsRan, 0);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failures = Interlocked.Increment(ref _consecutiveFailures);

            logger.LogError(ex,
                "Panel sync worker run failed unexpectedly. Consecutive failures: {ConsecutiveFailures}",
                failures);

            await NotifyFailureAsync(services, failures, ex.Message, cancellationToken);

            return new PanelSyncWorkerResult(null, false, failures);
        }
    }

    private PanelSyncMode SelectMode(PanelSyncStatus status, DateTimeOffset now)
    {
        // LastRunMode describes the last attempt, not necessarily a success.
        // Only trust it when the last run completed successfully. After a
        // restart with an unknown last-full time, prefer one safe full scan.
        if (_lastFullRunAt is null && status.LastRunMode == PanelSyncRunModes.Full &&
            status.LastError is null && status.LastSuccessAt is not null &&
            status.LastCompletedAt == status.LastSuccessAt)
            _lastFullRunAt = status.LastSuccessAt;

        if (_lastFullRunAt is null) return PanelSyncMode.Full;

        return now - _lastFullRunAt.Value >= TimeSpan.FromHours(_options.FullReconcileHours)
            ? PanelSyncMode.Full
            : PanelSyncMode.Cursor;
    }

    private async Task<bool> TryBackfillCommentsAsync(IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (!_options.CommentBackfillOnStartup || _commentBackfillCompleted) return false;

        try
        {
            var report = await services.GetRequiredService<ICommentBackfill>()
                .RunAsync(cancellationToken);

            _commentBackfillCompleted = true;

            logger.LogInformation(
                "Panel comment backfill complete. Clients: {Clients}, referrals inserted: {Referrals}, " +
                "trials inserted: {Trials}, reminders inserted: {Reminders}",
                report.ClientsScanned, report.ReferralsInserted, report.TrialsInserted,
                report.RemindersInserted);

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Panel comment backfill failed; it will be retried on the next sync cycle.");
            return false;
        }
    }

    private async Task NotifyConflictsAsync(IServiceProvider services,
        CancellationToken cancellationToken)
    {
        try
        {
            var notified = await services.GetRequiredService<IPanelSyncNotifier>()
                .NotifyOpenConflictsAsync(cancellationToken);

            if (notified > 0)
                logger.LogInformation("Notified administrators about {Count} panel conflicts.", notified);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Panel conflict notification failed; unnotified conflicts will be retried.");
        }
    }

    private async Task NotifyFailureAsync(IServiceProvider services, int consecutiveFailures,
        string error, CancellationToken cancellationToken)
    {
        if (consecutiveFailures != _options.FailureAlertThreshold) return;

        try
        {
            await services.GetRequiredService<IPanelSyncNotifier>()
                .NotifySyncFailureAsync(consecutiveFailures, error, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Panel sync failure notification failed.");
        }
    }

    private async Task RunOnceSafeAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is shutting down.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Panel sync worker loop failed.");
        }
    }
}

public sealed record PanelSyncWorkerResult(
    PanelSyncResult? Sync,
    bool CommentBackfillRan,
    int ConsecutiveFailures);
