using EagleTunnelApi.Application.Panel;
using EagleTunnelApi.Configuration;
using EagleTunnelApi.Infrastructure.Persistence.Entities;
using EagleTunnelApi.PanelSync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EagleTunnelApi.Tests.PanelSync;

public class PanelSyncWorkerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RunOnceAsync_uses_full_when_no_success_has_been_recorded()
    {
        var sync = new FakePanelSync();
        var directory = new FakePanelDirectory();
        var comments = new FakeCommentBackfill();
        var notifier = new FakeNotifier();

        using var provider = BuildProvider(sync, directory, comments, notifier);
        var worker = CreateWorker(provider, new PanelSyncOptions());

        var result = await worker.RunOnceAsync(CancellationToken.None);

        var call = Assert.Single(sync.Calls);
        Assert.Equal(PanelSyncMode.Full, call.Mode);
        Assert.NotNull(result.Sync);
        Assert.False(result.Sync!.Skipped);
        Assert.True(result.CommentBackfillRan);
        Assert.Equal(1, comments.Calls);
        Assert.Equal(1, notifier.ConflictCalls);
        Assert.Equal(0, result.ConsecutiveFailures);
    }

    [Fact]
    public async Task RunOnceAsync_uses_full_when_last_success_is_stale()
    {
        var sync = new FakePanelSync();
        var directory = new FakePanelDirectory
        {
            Status = new PanelSyncStatus { LastSuccessAt = Now.AddHours(-25) }
        };
        var comments = new FakeCommentBackfill();
        var notifier = new FakeNotifier();

        using var provider = BuildProvider(sync, directory, comments, notifier);
        var worker = CreateWorker(provider, new PanelSyncOptions { FullReconcileHours = 24 });

        await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(PanelSyncMode.Full, Assert.Single(sync.Calls).Mode);
    }

    [Fact]
    public async Task RunOnceAsync_uses_cursor_when_the_last_full_scan_is_recent()
    {
        var sync = new FakePanelSync();
        var directory = new FakePanelDirectory
        {
            Status = new PanelSyncStatus
            {
                LastSuccessAt = Now.AddMinutes(-5),
                LastCompletedAt = Now.AddMinutes(-5),
                LastRunMode = PanelSyncRunModes.Full
            }
        };
        var comments = new FakeCommentBackfill();
        var notifier = new FakeNotifier();

        using var provider = BuildProvider(sync, directory, comments, notifier);
        var worker = CreateWorker(provider, new PanelSyncOptions { FullReconcileHours = 24 });

        await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(PanelSyncMode.Cursor, Assert.Single(sync.Calls).Mode);
    }

    [Fact]
    public async Task RunOnceAsync_uses_full_when_the_last_full_attempt_failed()
    {
        var sync = new FakePanelSync();
        var directory = new FakePanelDirectory
        {
            Status = new PanelSyncStatus
            {
                LastSuccessAt = Now.AddMinutes(-5),
                LastCompletedAt = Now.AddMinutes(-1),
                LastRunMode = PanelSyncRunModes.Full,
                LastError = "panel unavailable"
            }
        };
        var comments = new FakeCommentBackfill();
        var notifier = new FakeNotifier();

        using var provider = BuildProvider(sync, directory, comments, notifier);
        var worker = CreateWorker(provider, new PanelSyncOptions { FullReconcileHours = 24 });

        await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(PanelSyncMode.Full, Assert.Single(sync.Calls).Mode);
    }

    [Fact]
    public async Task RunOnceAsync_uses_full_when_the_last_success_was_a_cursor_run()
    {
        var sync = new FakePanelSync();
        var directory = new FakePanelDirectory
        {
            Status = new PanelSyncStatus
            {
                LastSuccessAt = Now.AddMinutes(-5),
                LastRunMode = PanelSyncRunModes.Cursor
            }
        };
        var comments = new FakeCommentBackfill();
        var notifier = new FakeNotifier();

        using var provider = BuildProvider(sync, directory, comments, notifier);
        var worker = CreateWorker(provider, new PanelSyncOptions { FullReconcileHours = 24 });

        await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(PanelSyncMode.Full, Assert.Single(sync.Calls).Mode);
    }

    [Fact]
    public async Task RunOnceAsync_runs_a_full_scan_when_the_daily_reconcile_interval_elapses()
    {
        var sync = new FakePanelSync();
        var directory = new FakePanelDirectory();
        var comments = new FakeCommentBackfill();
        var notifier = new FakeNotifier();
        var clock = new MutableTimeProvider(Now);

        using var provider = BuildProvider(sync, directory, comments, notifier);
        var worker = CreateWorker(provider, new PanelSyncOptions { FullReconcileHours = 24 },
            clock);

        await worker.RunOnceAsync(CancellationToken.None);

        clock.UtcNow = Now.AddHours(25);

        await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, sync.Calls.Count);
        Assert.Equal(PanelSyncMode.Full, sync.Calls[0].Mode);
        Assert.Equal(PanelSyncMode.Full, sync.Calls[1].Mode);
    }

    [Fact]
    public async Task RunOnceAsync_runs_comment_backfill_once_per_process()
    {
        var sync = new FakePanelSync();
        var directory = new FakePanelDirectory
        {
            Status = new PanelSyncStatus { LastSuccessAt = Now.AddMinutes(-5) }
        };
        var comments = new FakeCommentBackfill();
        var notifier = new FakeNotifier();

        using var provider = BuildProvider(sync, directory, comments, notifier);
        var worker = CreateWorker(provider, new PanelSyncOptions());

        var first = await worker.RunOnceAsync(CancellationToken.None);
        var second = await worker.RunOnceAsync(CancellationToken.None);

        Assert.True(first.CommentBackfillRan);
        Assert.False(second.CommentBackfillRan);
        Assert.Equal(1, comments.Calls);
        Assert.Equal(2, notifier.ConflictCalls);
    }

    [Fact]
    public async Task RunOnceAsync_retries_comment_backfill_after_failure()
    {
        var sync = new FakePanelSync();
        var directory = new FakePanelDirectory
        {
            Status = new PanelSyncStatus { LastSuccessAt = Now.AddMinutes(-5) }
        };
        var comments = new FakeCommentBackfill { ThrowOnce = true };
        var notifier = new FakeNotifier();

        using var provider = BuildProvider(sync, directory, comments, notifier);
        var worker = CreateWorker(provider, new PanelSyncOptions());

        var failed = await worker.RunOnceAsync(CancellationToken.None);
        var retried = await worker.RunOnceAsync(CancellationToken.None);

        Assert.False(failed.CommentBackfillRan);
        Assert.True(retried.CommentBackfillRan);
        Assert.Equal(2, comments.Calls);
        Assert.Equal(0, failed.ConsecutiveFailures);
        Assert.Equal(0, retried.ConsecutiveFailures);
    }

    [Fact]
    public async Task RunOnceAsync_does_not_run_comment_backfill_when_disabled()
    {
        var sync = new FakePanelSync();
        var directory = new FakePanelDirectory
        {
            Status = new PanelSyncStatus { LastSuccessAt = Now.AddMinutes(-5) }
        };
        var comments = new FakeCommentBackfill();
        var notifier = new FakeNotifier();

        using var provider = BuildProvider(sync, directory, comments, notifier);
        var worker = CreateWorker(provider,
            new PanelSyncOptions { CommentBackfillOnStartup = false });

        var result = await worker.RunOnceAsync(CancellationToken.None);

        Assert.False(result.CommentBackfillRan);
        Assert.Equal(0, comments.Calls);
        Assert.Equal(1, notifier.ConflictCalls);
    }

    [Fact]
    public async Task RunOnceAsync_alerts_once_when_the_failure_threshold_is_reached()
    {
        var sync = new FakePanelSync();
        var directory = new FakePanelDirectory
        {
            Status = new PanelSyncStatus { LastSuccessAt = Now.AddMinutes(-5) }
        };
        var comments = new FakeCommentBackfill();
        var notifier = new FakeNotifier();

        for (var i = 0; i < 4; i++) sync.Results.Enqueue(Failure());
        sync.Results.Enqueue(Success());
        sync.Results.Enqueue(Failure());

        using var provider = BuildProvider(sync, directory, comments, notifier);
        var worker = CreateWorker(provider,
            new PanelSyncOptions { FailureAlertThreshold = 3 });

        var first = await worker.RunOnceAsync(CancellationToken.None);
        var second = await worker.RunOnceAsync(CancellationToken.None);
        var third = await worker.RunOnceAsync(CancellationToken.None);
        var fourth = await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, first.ConsecutiveFailures);
        Assert.Equal(2, second.ConsecutiveFailures);
        Assert.Equal(3, third.ConsecutiveFailures);
        Assert.Equal(4, fourth.ConsecutiveFailures);
        Assert.Equal(1, notifier.FailureCalls);
        Assert.Equal(3, notifier.LastFailureCount);
        Assert.Equal(0, notifier.ConflictCalls);

        var recovered = await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, recovered.ConsecutiveFailures);
        Assert.Equal(1, notifier.FailureCalls);
        Assert.Equal(1, notifier.ConflictCalls);

        var afterRecovery = await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, afterRecovery.ConsecutiveFailures);
        Assert.Equal(1, notifier.FailureCalls);
    }

    [Fact]
    public async Task RunOnceAsync_uses_a_unique_owner_for_each_run()
    {
        var sync = new FakePanelSync();
        var directory = new FakePanelDirectory
        {
            Status = new PanelSyncStatus { LastSuccessAt = Now.AddMinutes(-5) }
        };
        var comments = new FakeCommentBackfill();
        var notifier = new FakeNotifier();

        using var provider = BuildProvider(sync, directory, comments, notifier);
        var worker = CreateWorker(provider, new PanelSyncOptions());

        await worker.RunOnceAsync(CancellationToken.None);
        await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, sync.Calls.Count);
        Assert.All(sync.Calls, call => Assert.StartsWith("panel-sync:", call.Owner));
        Assert.NotEqual(sync.Calls[0].Owner, sync.Calls[1].Owner);
    }

    [Fact]
    public async Task RunOnceAsync_does_not_backfill_or_notify_when_the_run_is_skipped()
    {
        var sync = new FakePanelSync();
        sync.Results.Enqueue(Skipped());
        var directory = new FakePanelDirectory
        {
            Status = new PanelSyncStatus { LastSuccessAt = Now.AddMinutes(-5) }
        };
        var comments = new FakeCommentBackfill();
        var notifier = new FakeNotifier();

        using var provider = BuildProvider(sync, directory, comments, notifier);
        var worker = CreateWorker(provider, new PanelSyncOptions());

        var result = await worker.RunOnceAsync(CancellationToken.None);

        Assert.NotNull(result.Sync);
        Assert.True(result.Sync!.Skipped);
        Assert.False(result.CommentBackfillRan);
        Assert.Equal(0, comments.Calls);
        Assert.Equal(0, notifier.ConflictCalls);
    }

    [Fact]
    public async Task RunOnceAsync_keeps_the_sync_successful_when_conflict_notification_fails()
    {
        var sync = new FakePanelSync();
        var directory = new FakePanelDirectory
        {
            Status = new PanelSyncStatus { LastSuccessAt = Now.AddMinutes(-5) }
        };
        var comments = new FakeCommentBackfill();
        var notifier = new FakeNotifier { ThrowOnConflicts = true };

        using var provider = BuildProvider(sync, directory, comments, notifier);
        var worker = CreateWorker(provider, new PanelSyncOptions());

        var result = await worker.RunOnceAsync(CancellationToken.None);

        Assert.NotNull(result.Sync);
        Assert.Null(result.Sync!.Error);
        Assert.True(result.CommentBackfillRan);
        Assert.Equal(0, result.ConsecutiveFailures);
        Assert.Equal(1, notifier.ConflictCalls);
    }

    private static ServiceProvider BuildProvider(FakePanelSync sync, FakePanelDirectory directory,
        FakeCommentBackfill comments, FakeNotifier notifier)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IPanelSync>(sync);
        services.AddSingleton<IPanelDirectory>(directory);
        services.AddSingleton<ICommentBackfill>(comments);
        services.AddSingleton<IPanelSyncNotifier>(notifier);

        return services.BuildServiceProvider();
    }

    private static PanelSyncWorker CreateWorker(IServiceProvider provider, PanelSyncOptions options,
        TimeProvider? timeProvider = null)
    {
        return new PanelSyncWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options), timeProvider ?? new FixedTimeProvider(Now),
            NullLogger<PanelSyncWorker>.Instance);
    }

    private static PanelSyncResult Success(PanelSyncMode mode = PanelSyncMode.Cursor)
    {
        var modeName = mode == PanelSyncMode.Full
            ? PanelSyncRunModes.Full
            : PanelSyncRunModes.Cursor;

        return new PanelSyncResult(false, modeName, 0, 0, 0, 0, 0, null,
            Now, Now, null);
    }

    private static PanelSyncResult Failure()
    {
        return new PanelSyncResult(false, PanelSyncRunModes.Cursor, 0, 0, 0, 0, 1,
            "panel unavailable", Now, Now, "failed");
    }

    private static PanelSyncResult Skipped()
    {
        return new PanelSyncResult(true, PanelSyncRunModes.Cursor, 0, 0, 0, 0, 0, null,
            Now, Now, "another run holds the lease");
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class FakePanelDirectory : IPanelDirectory
    {
        public PanelSyncStatus Status { get; set; } = new();

        public Exception? Exception { get; set; }

        public Task<PanelSyncStatus> GetSyncStatusAsync(CancellationToken cancellationToken)
        {
            if (Exception is not null) throw Exception;
            return Task.FromResult(Status);
        }

        public Task<PanelMirrorClient?> GetClientByEmailAsync(string email,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<PanelSyncConflictView>> GetSyncConflictsAsync(string? status,
            int limit, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ResolveSyncConflictAsync(Guid conflictId, string status, string actor,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<PanelInboundSetting>> GetInboundSettingsAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<int>> GetDefaultInboundIdsAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SetDefaultInboundsAsync(IReadOnlyList<int> inboundIds,
            IReadOnlyList<PanelInboundOption> availableInbounds, string actor,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakePanelSync : IPanelSync
    {
        public List<(PanelSyncMode Mode, string Owner)> Calls { get; } = [];

        public Queue<PanelSyncResult> Results { get; } = new();

        public Task<PanelSyncResult> RunAsync(PanelSyncMode mode, string owner,
            CancellationToken cancellationToken)
        {
            Calls.Add((mode, owner));

            return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : Success(mode));
        }
    }

    private sealed class FakeCommentBackfill : ICommentBackfill
    {
        public int Calls { get; private set; }

        public bool ThrowOnce { get; set; }

        public Task<CommentBackfillReport> RunAsync(CancellationToken cancellationToken)
        {
            Calls++;

            if (ThrowOnce && Calls == 1)
                throw new InvalidOperationException("comment backfill failed");

            return Task.FromResult(new CommentBackfillReport(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                []));
        }
    }

    private sealed class FakeNotifier : IPanelSyncNotifier
    {
        public int ConflictCalls { get; private set; }

        public int FailureCalls { get; private set; }

        public int LastFailureCount { get; private set; }

        public bool ThrowOnConflicts { get; set; }

        public Task<int> NotifyOpenConflictsAsync(CancellationToken cancellationToken)
        {
            ConflictCalls++;

            if (ThrowOnConflicts) throw new InvalidOperationException("telegram unavailable");

            return Task.FromResult(0);
        }

        public Task NotifySyncFailureAsync(int consecutiveFailures, string error,
            CancellationToken cancellationToken)
        {
            FailureCalls++;
            LastFailureCount = consecutiveFailures;
            return Task.CompletedTask;
        }
    }
}
