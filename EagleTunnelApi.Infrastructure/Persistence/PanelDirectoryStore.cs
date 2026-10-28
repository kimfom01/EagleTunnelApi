using System.Text.Json;
using EagleTunnelApi.Application.Auditing;
using EagleTunnelApi.Application.Panel;
using EagleTunnelApi.Domain.Identity;
using EagleTunnelApi.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PanelClientEntity = EagleTunnelApi.Infrastructure.Persistence.Entities.PanelClient;

namespace EagleTunnelApi.Infrastructure.Persistence;

/// <summary>
/// Postgres implementation of the panel directory. It owns mirror reads and
/// the persisted default-inbound setting; live panel I/O stays behind
/// <see cref="IPanelGateway"/>.
/// </summary>
public sealed class PanelDirectoryStore(
    IDbContextFactory<AppDbContext> contextFactory,
    PanelDirectoryFallback fallback,
    TimeProvider timeProvider,
    ILogger<PanelDirectoryStore> logger) : IPanelDirectory
{
    private const int MaxConflictLimit = 500;

    public async Task<PanelMirrorClient?> GetClientByEmailAsync(string email,
        CancellationToken cancellationToken)
    {
        var normalized = EmailAddress.Normalize(email);

        if (normalized is null) return null;

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var client = await context.PanelClients
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.EmailNormalized == normalized && !candidate.IsDeleted,
                cancellationToken);

        if (client is null) return null;

        var inboundIds = await context.PanelClientInbounds
            .AsNoTracking()
            .Where(inbound => inbound.PanelClientId == client.Id)
            .OrderBy(inbound => inbound.InboundId)
            .Select(inbound => inbound.InboundId)
            .ToListAsync(cancellationToken);

        return Map(client, inboundIds);
    }

    public async Task<PanelSyncStatus> GetSyncStatusAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var state = await context.PanelSyncStates
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == PanelSyncStateIds.Default,
                cancellationToken);

        if (state is null) return new PanelSyncStatus();

        return new PanelSyncStatus
        {
            LastRunMode = state.LastRunMode,
            LastStartedAt = state.LastStartedAt,
            LastCompletedAt = state.LastCompletedAt,
            LastSuccessAt = state.LastSuccessAt,
            LastError = state.LastError,
            LastSeenCount = state.LastSeenCount,
            LastChangedCount = state.LastChangedCount,
            CursorPanelUpdatedAt = state.CursorPanelUpdatedAt,
            LockOwner = state.LockOwner,
            LockExpiresAt = state.LockExpiresAt
        };
    }

    public async Task<IReadOnlyList<PanelSyncConflictView>> GetSyncConflictsAsync(string? status,
        int limit, CancellationToken cancellationToken)
    {
        var effectiveStatus = status ?? PanelSyncConflictStates.Open;

        if (!PanelSyncConflictStates.IsKnown(effectiveStatus))
            throw new ArgumentException($"Unknown conflict status '{effectiveStatus}'.", nameof(status));

        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit), "The conflict limit must be positive.");

        limit = Math.Min(limit, MaxConflictLimit);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var conflicts = await context.PanelSyncConflicts
            .AsNoTracking()
            .Where(conflict => conflict.Status == effectiveStatus)
            .OrderByDescending(conflict => conflict.LastSeenAt)
            .ThenByDescending(conflict => conflict.FirstSeenAt)
            .ThenBy(conflict => conflict.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return conflicts.Select(Map).ToArray();
    }

    public async Task ResolveSyncConflictAsync(Guid conflictId, string status, string actor,
        CancellationToken cancellationToken)
    {
        var normalizedStatus = status?.Trim().ToLowerInvariant();

        if (normalizedStatus is null || !PanelSyncConflictStates.IsResolution(normalizedStatus))
            throw new ArgumentException(
                "A conflict can only be resolved or dismissed.", nameof(status));

        if (string.IsNullOrWhiteSpace(actor))
            throw new ArgumentException("The resolving actor is required.", nameof(actor));

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var conflict = await context.PanelSyncConflicts
            .SingleOrDefaultAsync(candidate => candidate.Id == conflictId, cancellationToken);

        if (conflict is null)
            throw new KeyNotFoundException($"Panel sync conflict '{conflictId}' was not found.");

        if (conflict.Status == normalizedStatus)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        var previousStatus = conflict.Status;
        var now = timeProvider.GetUtcNow();

        conflict.Status = normalizedStatus;
        conflict.ResolvedAt = now;
        conflict.ResolvedBy = actor;

        using var metadata = JsonSerializer.SerializeToDocument(new
        {
            actor,
            previousStatus,
            status = normalizedStatus
        });

        await new EfAuditWriter(context).AppendAsync(new AuditEventRecord
        {
            OccurredAt = now,
            ActorType = "admin",
            EventType = normalizedStatus == PanelSyncConflictStates.Resolved
                ? "panel.conflict.resolved"
                : "panel.conflict.dismissed",
            EmailNormalized = conflict.EmailNormalized,
            TelegramId = conflict.TelegramId,
            Metadata = metadata
        }, cancellationToken);

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PanelInboundSetting>> GetInboundSettingsAsync(
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var inbounds = await context.PanelInbounds
            .AsNoTracking()
            .OrderBy(inbound => inbound.InboundId)
            .ToListAsync(cancellationToken);

        return inbounds.Select(Map).ToArray();
    }

    public async Task<IReadOnlyList<int>> GetDefaultInboundIdsAsync(
        CancellationToken cancellationToken)
    {
        var fallbackIds = GetFallbackIds();

        try
        {
            var storedIds = await ReadDefaultInboundIdsAsync(cancellationToken);

            if (storedIds.Count > 0) return storedIds;

            if (fallbackIds.Count == 0) return [];

            await SeedDefaultInboundIdsAsync(fallbackIds, cancellationToken);

            var seededIds = await ReadDefaultInboundIdsAsync(cancellationToken);

            return seededIds.Count > 0 ? seededIds : fallbackIds;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception,
                "Could not read panel default inbound ids from Postgres; using configured fallback ids {InboundIds}.",
                string.Join(", ", fallbackIds));

            return fallbackIds;
        }
    }

    public async Task SetDefaultInboundsAsync(IReadOnlyList<int> inboundIds,
        IReadOnlyList<PanelInboundOption> availableInbounds, string actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inboundIds);
        ArgumentNullException.ThrowIfNull(availableInbounds);

        if (string.IsNullOrWhiteSpace(actor))
            throw new ArgumentException("The changing actor is required.", nameof(actor));

        var selectedIds = inboundIds.Distinct().ToArray();

        if (selectedIds.Length == 0)
            throw new ArgumentException(
                "At least one default inbound is required for new clients.", nameof(inboundIds));

        if (selectedIds.Any(id => id <= 0))
            throw new ArgumentException(
                "Default inbound ids must be positive.", nameof(inboundIds));

        var options = availableInbounds
            .GroupBy(inbound => inbound.Id)
            .ToDictionary(group => group.Key, group => group.Last());

        if (options.Keys.Any(id => id <= 0))
            throw new ArgumentException(
                "Available inbound ids must be positive.", nameof(availableInbounds));

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var existing = await context.PanelInbounds
            .ToDictionaryAsync(inbound => inbound.InboundId, cancellationToken);

        var unknownIds = selectedIds
            .Where(id => !options.ContainsKey(id) && !existing.ContainsKey(id))
            .ToArray();

        if (unknownIds.Length > 0)
            throw new ArgumentException(
                $"Unknown inbound id(s): {string.Join(", ", unknownIds)}.", nameof(inboundIds));

        var oldDefaultIds = existing.Values
            .Where(inbound => inbound.IsDefaultForNewClients)
            .OrderBy(inbound => inbound.SortOrder)
            .ThenBy(inbound => inbound.InboundId)
            .Select(inbound => inbound.InboundId)
            .ToArray();

        var selectedSet = selectedIds.ToHashSet();
        var now = timeProvider.GetUtcNow();

        foreach (var option in options.Values.OrderBy(inbound => inbound.Id))
        {
            if (!existing.TryGetValue(option.Id, out var inbound))
            {
                inbound = new PanelInbound
                {
                    InboundId = option.Id
                };

                context.PanelInbounds.Add(inbound);
                existing[option.Id] = inbound;
            }

            inbound.Remark = option.Remark;
            inbound.Tag = option.Tag;
            inbound.Protocol = option.Protocol;
            inbound.Port = option.Port;
            inbound.LastSyncedAt = now;
        }

        for (var index = 0; index < selectedIds.Length; index++)
        {
            var inbound = existing[selectedIds[index]];

            inbound.IsDefaultForNewClients = true;
            inbound.SortOrder = index;
        }

        foreach (var inbound in existing.Values)
        {
            if (selectedSet.Contains(inbound.InboundId)) continue;

            inbound.IsDefaultForNewClients = false;
            inbound.SortOrder = 0;
        }

        using var metadata = JsonSerializer.SerializeToDocument(new
        {
            actor,
            oldDefaultInboundIds = oldDefaultIds,
            newDefaultInboundIds = selectedIds,
            availableInboundIds = options.Keys.Order().ToArray()
        });

        await new EfAuditWriter(context).AppendAsync(new AuditEventRecord
        {
            OccurredAt = now,
            ActorType = "admin",
            EventType = "panel.default_inbounds.changed",
            Metadata = metadata
        }, cancellationToken);

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<int>> ReadDefaultInboundIdsAsync(
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.PanelInbounds
            .AsNoTracking()
            .Where(inbound => inbound.IsDefaultForNewClients)
            .OrderBy(inbound => inbound.SortOrder)
            .ThenBy(inbound => inbound.InboundId)
            .Select(inbound => inbound.InboundId)
            .ToListAsync(cancellationToken);
    }

    private async Task SeedDefaultInboundIdsAsync(IReadOnlyList<int> inboundIds,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var alreadySeeded = await context.PanelInbounds
            .AnyAsync(inbound => inbound.IsDefaultForNewClients, cancellationToken);

        if (alreadySeeded)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        var now = timeProvider.GetUtcNow();

        var existing = await context.PanelInbounds
            .Where(inbound => inboundIds.Contains(inbound.InboundId))
            .ToDictionaryAsync(inbound => inbound.InboundId, cancellationToken);

        for (var index = 0; index < inboundIds.Count; index++)
        {
            if (!existing.TryGetValue(inboundIds[index], out var inbound))
            {
                inbound = new PanelInbound
                {
                    InboundId = inboundIds[index]
                };

                context.PanelInbounds.Add(inbound);
            }

            inbound.IsDefaultForNewClients = true;
            inbound.SortOrder = index;
            inbound.LastSyncedAt = now;
        }

        using var metadata = JsonSerializer.SerializeToDocument(new
        {
            source = "configuration",
            seededInboundIds = inboundIds
        });

        await new EfAuditWriter(context).AppendAsync(new AuditEventRecord
        {
            OccurredAt = now,
            ActorType = "system",
            EventType = "panel.default_inbounds.seeded",
            Metadata = metadata
        }, cancellationToken);

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private IReadOnlyList<int> GetFallbackIds()
    {
        return fallback.DefaultInboundIds
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
    }

    private static PanelMirrorClient Map(PanelClientEntity client, IReadOnlyList<int> inboundIds)
    {
        return new PanelMirrorClient
        {
            Id = client.Id,
            PanelClientId = client.PanelClientId,
            EmailNormalized = client.EmailNormalized,
            TelegramId = client.TelegramId,
            Enable = client.Enable,
            ExpiryTime = client.ExpiryTime,
            TotalGb = client.TotalGb,
            LimitIp = client.LimitIp,
            LimitHwid = client.LimitHwid,
            TrafficReset = client.TrafficReset,
            TrafficResetDay = client.TrafficResetDay,
            Reset = client.Reset,
            SubId = client.SubId,
            Flow = client.Flow,
            Security = client.Security,
            TrafficUp = client.TrafficUp,
            TrafficDown = client.TrafficDown,
            TrafficEnabled = client.TrafficEnabled,
            LegacyComment = client.LegacyComment,
            PanelCreatedAt = client.PanelCreatedAt,
            PanelUpdatedAt = client.PanelUpdatedAt,
            LastSyncedAt = client.LastSyncedAt,
            IsDeleted = client.IsDeleted,
            DeletedAt = client.DeletedAt,
            InboundIds = inboundIds
        };
    }

    private static PanelSyncConflictView Map(PanelSyncConflict conflict)
    {
        return new PanelSyncConflictView
        {
            Id = conflict.Id,
            DedupeKey = conflict.DedupeKey,
            Type = conflict.Type,
            EmailNormalized = conflict.EmailNormalized,
            TelegramId = conflict.TelegramId,
            PanelClientId = conflict.PanelClientId,
            OtherPanelClientId = conflict.OtherPanelClientId,
            AppUserId = conflict.AppUserId,
            DetailsJson = conflict.Details?.RootElement.GetRawText(),
            Status = conflict.Status,
            FirstSeenAt = conflict.FirstSeenAt,
            LastSeenAt = conflict.LastSeenAt,
            NotifiedAt = conflict.NotifiedAt,
            ResolvedAt = conflict.ResolvedAt,
            ResolvedBy = conflict.ResolvedBy
        };
    }

    private static PanelInboundSetting Map(PanelInbound inbound)
    {
        return new PanelInboundSetting(
            inbound.InboundId,
            inbound.Remark,
            inbound.Tag,
            inbound.Protocol,
            inbound.Port,
            inbound.IsDefaultForNewClients,
            inbound.SortOrder,
            inbound.LastSyncedAt);
    }
}
