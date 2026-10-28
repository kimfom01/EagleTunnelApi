using System.Text.Json;
using EagleTunnelApi.Application.Panel;
using EagleTunnelApi.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace EagleTunnelApi.Infrastructure.Persistence;

/// <summary>
/// Shared conflict upsert/resolve logic used by the mirror writer and the sync
/// reconciler. Callers own the transaction so conflicts commit atomically with
/// the change that produced them.
/// </summary>
public static class PanelConflictRecorder
{
    public static async Task<PanelMirrorConflict> UpsertAsync(AppDbContext context, string dedupeKey,
        string type, string? emailNormalized, long? telegramId, long? panelClientId,
        long? otherPanelClientId, Guid? appUserId, object details, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var detailsDocument = JsonSerializer.SerializeToDocument(details);
        var conflict = await context.PanelSyncConflicts
            .SingleOrDefaultAsync(candidate => candidate.DedupeKey == dedupeKey, cancellationToken);

        var isNew = conflict is null;
        var reopened = false;

        if (conflict is null)
        {
            conflict = new PanelSyncConflict
            {
                Id = Guid.NewGuid(),
                DedupeKey = dedupeKey,
                Status = PanelSyncConflictStatus.Open,
                FirstSeenAt = now,
                LastSeenAt = now
            };

            context.PanelSyncConflicts.Add(conflict);
        }
        else
        {
            var detailsChanged = conflict.Details is null ||
                conflict.Details.RootElement.GetRawText() !=
                detailsDocument.RootElement.GetRawText();

            conflict.LastSeenAt = now;

            if (conflict.Status != PanelSyncConflictStatus.Open)
            {
                conflict.Status = PanelSyncConflictStatus.Open;
                conflict.ResolvedAt = null;
                conflict.ResolvedBy = null;
                reopened = true;
            }

            if (reopened || detailsChanged) conflict.NotifiedAt = null;
        }

        conflict.Type = type;
        conflict.EmailNormalized = emailNormalized;
        conflict.TelegramId = telegramId;
        conflict.PanelClientId = panelClientId;
        conflict.OtherPanelClientId = otherPanelClientId;
        conflict.AppUserId = appUserId;
        conflict.Details = detailsDocument;

        return new PanelMirrorConflict(conflict.Id, dedupeKey, type, isNew, reopened);
    }

    /// <summary>
    /// Resolves an open conflict owned by the panel client whose identity was
    /// successfully applied. Conflicts owned by another panel client are left
    /// open because their attempted identity may still be ambiguous.
    /// </summary>
    public static async Task<bool> ResolveOwnedAsync(AppDbContext context, string dedupeKey,
        long panelClientId, string actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var conflict = await context.PanelSyncConflicts
            .SingleOrDefaultAsync(candidate =>
                    candidate.DedupeKey == dedupeKey &&
                    candidate.Status == PanelSyncConflictStatus.Open &&
                    candidate.PanelClientId == panelClientId,
                cancellationToken);

        if (conflict is null) return false;

        conflict.Status = PanelSyncConflictStatus.Resolved;
        conflict.ResolvedAt = now;
        conflict.ResolvedBy = actor;
        conflict.LastSeenAt = now;

        return true;
    }
}
