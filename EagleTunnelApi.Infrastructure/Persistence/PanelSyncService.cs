using EagleTunnelApi.Application.Panel;
using EagleTunnelApi.Domain.Identity;
using EagleTunnelApi.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EagleTunnelApi.Infrastructure.Persistence;

/// <summary>
/// Recurring panel synchronisation with a Postgres lease. Cursor mode uses the
/// panel's <c>updatedAt</c> ordering when available and falls back to a full
/// scan when it is rejected. A full scan is also required for deletion
/// reconciliation and is used when the mirror is empty or has no cursor.
/// </summary>
public sealed class PanelSyncService(
    IPanelGateway panelGateway,
    IPanelMirrorWriter mirrorWriter,
    IDbContextFactory<AppDbContext> contextFactory,
    TimeProvider timeProvider,
    ILogger<PanelSyncService> logger) : IPanelSync
{
    private const int PageSize = 200;
    private const int MaxPages = 10_000;
    private const int LeaseMinutes = 30;
    private static readonly TimeSpan CursorOverlap = TimeSpan.FromMinutes(5);

    public async Task<PanelSyncResult> RunAsync(PanelSyncMode mode, string owner,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("A sync owner is required.", nameof(owner));

        var startedAt = timeProvider.GetUtcNow();
        var requestedMode = mode;

        if (!await TryAcquireLeaseAsync(owner, startedAt, cancellationToken))
        {
            logger.LogWarning("Panel sync skipped; lease is held by another run. Owner: {Owner}", owner);

            return new PanelSyncResult(true, ModeName(requestedMode), 0, 0, 0, 0, 0, null,
                startedAt, startedAt, "The panel sync lease is held by another run.");
        }

        var effectiveMode = mode;
        var accumulator = new SyncAccumulator();
        DateTimeOffset? previousCursor = null;

        try
        {
            previousCursor = await GetCursorAsync(cancellationToken);

            if (effectiveMode == PanelSyncMode.Cursor)
            {
                var hasMirror = await HasMirrorAsync(cancellationToken);

                if (previousCursor is null || !hasMirror)
                {
                    effectiveMode = PanelSyncMode.Full;
                    logger.LogInformation(
                        "Panel sync upgraded to full: cursor present {HasCursor}, mirror present {HasMirror}",
                        previousCursor is not null, hasMirror);
                }
            }

            await RecordStartAsync(owner, effectiveMode, startedAt, cancellationToken);

            PanelClientPage? firstPage = null;
            DateTimeOffset? activeCursor = null;

            if (effectiveMode == PanelSyncMode.Cursor)
            {
                try
                {
                    firstPage = await panelGateway.GetClientsPageAsync(
                        CreatePageRequest(PanelSyncMode.Cursor, 1), cancellationToken);
                    activeCursor = previousCursor;
                }
                catch (PanelApiException ex)
                {
                    logger.LogWarning(ex,
                        "Panel rejected updatedAt sorting; falling back to a full scan.");
                    effectiveMode = PanelSyncMode.Full;
                    activeCursor = previousCursor;
                    await RecordRunModeAsync(owner, effectiveMode, cancellationToken);
                }
            }
            else
            {
                activeCursor = previousCursor;
            }

            var mirrors = await LoadMirrorSnapshotsAsync(cancellationToken);
            var mirrorsByEmail = mirrors
                .GroupBy(snapshot => snapshot.EmailNormalized, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var mirrorsBySubId = mirrors
                .Where(snapshot => !string.IsNullOrWhiteSpace(snapshot.SubId))
                .GroupBy(snapshot => snapshot.SubId!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            await ProcessPagesAsync(effectiveMode, firstPage, activeCursor, mirrorsByEmail,
                mirrorsBySubId, accumulator, cancellationToken);

            var syncedAt = timeProvider.GetUtcNow();
            await UpdatePageSnapshotAsync(accumulator.PageTraffic, syncedAt, cancellationToken);

            if (effectiveMode == PanelSyncMode.Full)
            {
                var missing = mirrors
                    .Where(snapshot => !snapshot.IsDeleted &&
                        !accumulator.SeenPanelClientIds.Contains(snapshot.PanelClientId))
                    .Select(snapshot => snapshot.PanelClientId)
                    .ToArray();

                await MarkMissingAsync(missing, syncedAt, accumulator, cancellationToken);
                await ReconcileOrphanAppUsersAsync(syncedAt, accumulator, cancellationToken);
            }

            var completedAt = timeProvider.GetUtcNow();
            var cursorValue = accumulator.MaxUpdatedAt is { } maxUpdatedAt &&
                (previousCursor is null || maxUpdatedAt > previousCursor)
                ? maxUpdatedAt
                : previousCursor;

            await RecordSuccessAsync(owner, effectiveMode, accumulator, cursorValue, completedAt,
                cancellationToken);

            logger.LogInformation(
                "Panel sync completed. Mode: {Mode}, Seen: {Seen}, Changed: {Changed}, Deleted: {Deleted}, " +
                "Conflicts: {Conflicts}, Failures: {Failures}",
                ModeName(effectiveMode), accumulator.Seen, accumulator.Changed, accumulator.Deleted,
                accumulator.Conflicts, accumulator.Failures);

            return new PanelSyncResult(false, ModeName(effectiveMode), accumulator.Seen,
                accumulator.Changed, accumulator.Deleted, accumulator.Conflicts, accumulator.Failures,
                null, startedAt, completedAt, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var completedAt = timeProvider.GetUtcNow();
            await SafeRecordFailureAsync(owner, effectiveMode, accumulator, "Panel sync cancelled.",
                completedAt);

            throw;
        }
        catch (Exception ex)
        {
            var completedAt = timeProvider.GetUtcNow();
            await SafeRecordFailureAsync(owner, effectiveMode, accumulator, ex.Message, completedAt);

            logger.LogError(ex,
                "Panel sync failed. Mode: {Mode}, Seen: {Seen}, Changed: {Changed}",
                ModeName(effectiveMode), accumulator.Seen, accumulator.Changed);

            return new PanelSyncResult(false, ModeName(effectiveMode), accumulator.Seen,
                accumulator.Changed, accumulator.Deleted, accumulator.Conflicts, accumulator.Failures,
                ex.Message, startedAt, completedAt, "failed");
        }
    }

    private async Task ProcessPagesAsync(PanelSyncMode mode, PanelClientPage? firstPage,
        DateTimeOffset? cursor, IReadOnlyDictionary<string, MirrorSnapshot> mirrorsByEmail,
        IReadOnlyDictionary<string, MirrorSnapshot> mirrorsBySubId, SyncAccumulator accumulator,
        CancellationToken cancellationToken)
    {
        var completed = false;

        for (var pageNumber = 1; pageNumber <= MaxPages; pageNumber++)
        {
            var page = firstPage ?? await panelGateway.GetClientsPageAsync(
                CreatePageRequest(mode, pageNumber), cancellationToken);

            firstPage = null;

            if (page.Items.Count == 0)
            {
                var emptyPageSize = page.PageSize > 0 ? page.PageSize : PageSize;

                if (page.Total > 0 && (pageNumber - 1) * emptyPageSize < page.Total)
                    throw new InvalidOperationException(
                        "Panel returned an empty page before the reported total.");

                completed = true;
                break;
            }

            foreach (var row in page.Items)
            {
                accumulator.Seen++;
                accumulator.MaxUpdatedAt = Max(accumulator.MaxUpdatedAt, row.UpdatedAt);

                var normalizedEmail = EmailAddress.Normalize(row.Email);

                if (normalizedEmail is null)
                {
                    accumulator.Failures++;
                    logger.LogWarning("Panel sync skipped a client row with no usable email: {Email}",
                        row.Email);
                    continue;
                }

                MirrorSnapshot? mirror;

                if (!string.IsNullOrWhiteSpace(row.SubId))
                {
                    // The sub id is the stable row key from the paged endpoint.
                    // Do not fall back to email when it is present: a new
                    // client can reuse a deleted client's email, and that must
                    // not keep the old mirror row alive.
                    mirrorsBySubId.TryGetValue(row.SubId, out mirror);
                }
                else
                {
                    mirrorsByEmail.TryGetValue(normalizedEmail, out mirror);
                }

                if (mirror is not null && !mirror.IsDeleted)
                    accumulator.SeenPanelClientIds.Add(mirror.PanelClientId);

                if (!ShouldFetchFull(row, mirror, normalizedEmail, cursor))
                {
                    accumulator.PageTraffic[normalizedEmail] = row.Traffic;
                    continue;
                }

                var details = await panelGateway.GetClientByEmailAsync(row.Email, cancellationToken);

                if (details is null)
                {
                    accumulator.Failures++;

                    if (mirror is not null && !mirror.IsDeleted)
                    {
                        accumulator.SeenPanelClientIds.Remove(mirror.PanelClientId);
                        await MarkMissingAsync([mirror.PanelClientId], timeProvider.GetUtcNow(),
                            accumulator, cancellationToken);
                    }

                    continue;
                }

                accumulator.SeenPanelClientIds.Add(details.Client.Id);

                var result = await mirrorWriter.UpsertAsync(details, row.Traffic, cancellationToken);

                if (result.Applied) accumulator.Changed++;
                accumulator.Conflicts += result.Conflicts.Count;

                if (result.Conflicts.Count > 0)
                    logger.LogWarning(
                        "Panel sync recorded {Count} conflict(s) for client {PanelClientId}.",
                        result.Conflicts.Count, details.Client.Id);
            }

            var effectivePageSize = page.PageSize > 0 ? page.PageSize : PageSize;

            if (page.Total > 0 && pageNumber * effectivePageSize >= page.Total)
            {
                completed = true;
                break;
            }

            if (page.Items.Count < effectivePageSize)
            {
                completed = true;
                break;
            }
        }

        if (!completed)
            throw new InvalidOperationException(
                "Panel sync exceeded the maximum page count without completing.");
    }

    private static bool ShouldFetchFull(PanelClientPageRow row, MirrorSnapshot? mirror,
        string normalizedEmail, DateTimeOffset? cursor)
    {
        if (mirror is null || mirror.IsDeleted) return true;
        if (!string.Equals(mirror.EmailNormalized, normalizedEmail, StringComparison.Ordinal))
            return true;
        if (row.UpdatedAt > (mirror.PanelUpdatedAt ?? DateTimeOffset.MinValue)) return true;

        // Equal timestamps can hide a change. Re-read rows at the cursor edge
        // with a small overlap so clock skew does not permanently skip them.
        return cursor is not null &&
            row.UpdatedAt >= cursor.Value - CursorOverlap &&
            row.UpdatedAt == mirror.PanelUpdatedAt;
    }

    private async Task UpdatePageSnapshotAsync(
        IReadOnlyDictionary<string, PanelTrafficSummary?> pageTraffic, DateTimeOffset syncedAt,
        CancellationToken cancellationToken)
    {
        if (pageTraffic.Count == 0) return;

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var emails = pageTraffic.Keys.ToArray();
        var clients = await context.PanelClients
            .Where(client => emails.Contains(client.EmailNormalized) && !client.IsDeleted)
            .ToListAsync(cancellationToken);

        foreach (var client in clients)
        {
            var traffic = pageTraffic[client.EmailNormalized];

            if (traffic is not null)
            {
                client.TrafficUp = traffic.Up;
                client.TrafficDown = traffic.Down;
                client.TrafficEnabled = traffic.Enable;
            }

            client.LastSyncedAt = syncedAt;
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task MarkMissingAsync(IReadOnlyCollection<long> panelClientIds, DateTimeOffset now,
        SyncAccumulator accumulator, CancellationToken cancellationToken)
    {
        if (panelClientIds.Count == 0) return;

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var clients = await context.PanelClients
            .Where(client => panelClientIds.Contains(client.PanelClientId) && !client.IsDeleted)
            .ToListAsync(cancellationToken);

        if (clients.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        var ids = clients.Select(client => client.PanelClientId).ToArray();

        var appUsers = await context.AppUsers
            .Where(user => ids.Contains(user.PanelClientId))
            .ToListAsync(cancellationToken);

        var appUserIds = appUsers.Select(user => user.Id).ToArray();

        var activeSessionUserIds = appUserIds.Length == 0
            ? []
            : await context.WebSessions
                .Where(session => appUserIds.Contains(session.AppUserId) &&
                    session.RevokedAt == null && session.ExpiresAt > now)
                .Select(session => session.AppUserId)
                .Distinct()
                .ToListAsync(cancellationToken);

        foreach (var client in clients)
        {
            client.IsDeleted = true;
            client.DeletedAt = now;
            client.LastSyncedAt = now;
            accumulator.Deleted++;

            var appUser = appUsers.FirstOrDefault(user =>
                user.PanelClientId == client.PanelClientId);

            var hadActiveSession = appUser is not null && activeSessionUserIds.Contains(appUser.Id);

            if (appUser is null && !hadActiveSession) continue;

            await PanelConflictRecorder.UpsertAsync(context,
                $"panel:{client.PanelClientId}:missing",
                PanelConflictTypes.PanelClientMissing,
                client.EmailNormalized,
                client.TelegramId,
                client.PanelClientId,
                null,
                appUser?.Id,
                new
                {
                    panelClientId = client.PanelClientId,
                    emailNormalized = client.EmailNormalized,
                    appUserId = appUser?.Id,
                    hadActiveSession
                },
                now,
                cancellationToken);

            accumulator.Conflicts++;
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task ReconcileOrphanAppUsersAsync(DateTimeOffset now, SyncAccumulator accumulator,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var orphans = await context.AppUsers
            .Where(user => !context.PanelClients.Any(client =>
                client.PanelClientId == user.PanelClientId))
            .ToListAsync(cancellationToken);

        if (orphans.Count == 0) return;

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        foreach (var orphan in orphans)
        {
            await PanelConflictRecorder.UpsertAsync(context,
                $"app:{orphan.Id}:orphan",
                PanelConflictTypes.AppUserOrphan,
                orphan.EmailNormalized,
                orphan.TelegramId,
                orphan.PanelClientId,
                null,
                orphan.Id,
                new
                {
                    appUserId = orphan.Id,
                    panelClientId = orphan.PanelClientId,
                    emailNormalized = orphan.EmailNormalized
                },
                now,
                cancellationToken);

            accumulator.Conflicts++;
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<MirrorSnapshot>> LoadMirrorSnapshotsAsync(
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.PanelClients
            .AsNoTracking()
            .Select(client => new MirrorSnapshot(client.PanelClientId, client.EmailNormalized,
                client.SubId, client.PanelUpdatedAt, client.IsDeleted))
            .ToListAsync(cancellationToken);
    }

    private async Task<DateTimeOffset?> GetCursorAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.PanelSyncStates
            .AsNoTracking()
            .Where(state => state.Id == PanelSyncStateIds.Default)
            .Select(state => state.CursorPanelUpdatedAt)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private async Task<bool> HasMirrorAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.PanelClients
            .AnyAsync(client => !client.IsDeleted, cancellationToken);
    }

    private async Task<bool> TryAcquireLeaseAsync(string owner, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO panel_sync_state (id, last_seen_count, last_changed_count)
            VALUES ({PanelSyncStateIds.Default}, 0, 0)
            ON CONFLICT (id) DO NOTHING
            """, cancellationToken);

        var expiresAt = now.AddMinutes(LeaseMinutes);

        var affected = await context.PanelSyncStates
            .Where(state => state.Id == PanelSyncStateIds.Default &&
                (state.LockOwner == null ||
                 state.LockExpiresAt == null ||
                 state.LockExpiresAt < now ||
                 state.LockOwner == owner))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(state => state.LockOwner, owner)
                .SetProperty(state => state.LockExpiresAt, expiresAt), cancellationToken);

        return affected == 1;
    }

    private async Task RecordStartAsync(string owner, PanelSyncMode mode, DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        await context.PanelSyncStates
            .Where(state => state.Id == PanelSyncStateIds.Default && state.LockOwner == owner)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(state => state.LastStartedAt, startedAt)
                .SetProperty(state => state.LastRunMode, ModeName(mode))
                .SetProperty(state => state.LastSeenCount, 0)
                .SetProperty(state => state.LastChangedCount, 0)
                .SetProperty(state => state.LastError, (string?)null), cancellationToken);
    }

    private async Task RecordRunModeAsync(string owner, PanelSyncMode mode,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        await context.PanelSyncStates
            .Where(state => state.Id == PanelSyncStateIds.Default && state.LockOwner == owner)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(state => state.LastRunMode, ModeName(mode)), cancellationToken);
    }

    private async Task RecordSuccessAsync(string owner, PanelSyncMode mode,
        SyncAccumulator accumulator, DateTimeOffset? cursor, DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var affected = await context.PanelSyncStates
            .Where(state => state.Id == PanelSyncStateIds.Default && state.LockOwner == owner)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(state => state.LastCompletedAt, completedAt)
                .SetProperty(state => state.LastSuccessAt, completedAt)
                .SetProperty(state => state.LastError, (string?)null)
                .SetProperty(state => state.LastSeenCount, accumulator.Seen)
                .SetProperty(state => state.LastChangedCount, accumulator.Changed + accumulator.Deleted)
                .SetProperty(state => state.LastRunMode, ModeName(mode))
                .SetProperty(state => state.CursorPanelUpdatedAt, cursor)
                .SetProperty(state => state.LockOwner, (string?)null)
                .SetProperty(state => state.LockExpiresAt, (DateTimeOffset?)null), cancellationToken);

        if (affected == 0)
            logger.LogWarning("Panel sync completed but the lease was lost before status was recorded.");
    }

    private async Task SafeRecordFailureAsync(string owner, PanelSyncMode mode,
        SyncAccumulator accumulator, string error, DateTimeOffset completedAt)
    {
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(CancellationToken.None);

            await context.PanelSyncStates
                .Where(state => state.Id == PanelSyncStateIds.Default && state.LockOwner == owner)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(state => state.LastCompletedAt, completedAt)
                    .SetProperty(state => state.LastError, error)
                    .SetProperty(state => state.LastSeenCount, accumulator.Seen)
                    .SetProperty(state => state.LastChangedCount,
                        accumulator.Changed + accumulator.Deleted)
                    .SetProperty(state => state.LastRunMode, ModeName(mode))
                    .SetProperty(state => state.LockOwner, (string?)null)
                    .SetProperty(state => state.LockExpiresAt, (DateTimeOffset?)null),
                    CancellationToken.None);
        }
        catch (Exception releaseException)
        {
            logger.LogError(releaseException,
                "Panel sync failed and its lease could not be released. Owner: {Owner}", owner);
        }
    }

    private static PanelClientPageRequest CreatePageRequest(PanelSyncMode mode, int pageNumber)
    {
        return mode == PanelSyncMode.Cursor
            ? new PanelClientPageRequest(pageNumber, PageSize, PanelClientSortFields.UpdatedAt,
                PanelSortOrders.Ascend)
            : new PanelClientPageRequest(pageNumber, PageSize, PanelClientSortFields.Email,
                PanelSortOrders.Ascend);
    }

    private static string ModeName(PanelSyncMode mode)
    {
        return mode == PanelSyncMode.Cursor ? PanelSyncRunModes.Cursor : PanelSyncRunModes.Full;
    }

    private static DateTimeOffset Max(DateTimeOffset? current, DateTimeOffset candidate)
    {
        return current is null || candidate > current ? candidate : current.Value;
    }

    private sealed record MirrorSnapshot(
        long PanelClientId,
        string EmailNormalized,
        string? SubId,
        DateTimeOffset? PanelUpdatedAt,
        bool IsDeleted);

    private sealed class SyncAccumulator
    {
        public int Seen { get; set; }
        public int Changed { get; set; }
        public int Deleted { get; set; }
        public int Conflicts { get; set; }
        public int Failures { get; set; }
        public DateTimeOffset? MaxUpdatedAt { get; set; }
        public Dictionary<string, PanelTrafficSummary?> PageTraffic { get; } =
            new(StringComparer.Ordinal);
        public HashSet<long> SeenPanelClientIds { get; } = [];
    }
}
