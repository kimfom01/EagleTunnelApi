using EagleTunnelApi.Application.Panel;
using EagleTunnelApi.Domain.Identity;
using EagleTunnelApi.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PanelClientEntity = EagleTunnelApi.Infrastructure.Persistence.Entities.PanelClient;

namespace EagleTunnelApi.Infrastructure.Persistence;

/// <summary>
/// Postgres implementation of the panel mirror writer. Panel fields are
/// authoritative; identity collisions preserve the last-known-good identity and
/// are quarantined in <c>panel_sync_conflicts</c>.
/// </summary>
public sealed class PanelMirrorWriter(
    IDbContextFactory<AppDbContext> contextFactory,
    TimeProvider timeProvider,
    ILogger<PanelMirrorWriter> logger) : IPanelMirrorWriter
{
    public async Task<PanelMirrorWriteResult> UpsertAsync(PanelClientDetails client,
        PanelTrafficSummary? traffic, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);

        if (client.Client.Id <= 0)
            throw new ArgumentException("Panel client id must be positive.", nameof(client));

        var panelClient = client.Client;
        var normalizedEmail = EmailAddress.Normalize(panelClient.Email);
        var telegramId = panelClient.TgId > 0 ? panelClient.TgId : (long?)null;
        var now = timeProvider.GetUtcNow();
        var conflicts = new List<PanelMirrorConflict>();

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var existingClient = await context.PanelClients
            .SingleOrDefaultAsync(candidate => candidate.PanelClientId == panelClient.Id,
                cancellationToken);

        if (normalizedEmail is null)
        {
            conflicts.Add(await PanelConflictRecorder.UpsertAsync(context,
                $"panel:{panelClient.Id}:invalid_email",
                PanelConflictTypes.InvalidEmail,
                null,
                telegramId,
                panelClient.Id,
                null,
                null,
                new
                {
                    panelClientId = panelClient.Id,
                    rawEmail = panelClient.Email
                },
                now,
                cancellationToken));

            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            logger.LogWarning(
                "Panel client {PanelClientId} has no usable email and was not mirrored.",
                panelClient.Id);

            return new PanelMirrorWriteResult(panelClient.Id, existingClient?.Id, null, false, conflicts);
        }

        var emailPanelClient = await context.PanelClients
            .FirstOrDefaultAsync(candidate =>
                    candidate.EmailNormalized == normalizedEmail &&
                    candidate.PanelClientId != panelClient.Id,
                cancellationToken);

        var telegramPanelClient = telegramId is null
            ? null
            : await context.PanelClients
                .FirstOrDefaultAsync(candidate =>
                        candidate.TelegramId == telegramId &&
                        candidate.PanelClientId != panelClient.Id,
                    cancellationToken);

        var existingAppUser = await context.AppUsers
            .SingleOrDefaultAsync(candidate => candidate.PanelClientId == panelClient.Id,
                cancellationToken);

        var emailAppUser = await context.AppUsers
            .FirstOrDefaultAsync(candidate =>
                    candidate.EmailNormalized == normalizedEmail &&
                    candidate.PanelClientId != panelClient.Id,
                cancellationToken);

        var telegramAppUser = telegramId is null
            ? null
            : await context.AppUsers
                .FirstOrDefaultAsync(candidate =>
                        candidate.TelegramId == telegramId &&
                        candidate.PanelClientId != panelClient.Id,
                    cancellationToken);

        var emailConflict = emailPanelClient is not null || emailAppUser is not null;
        var telegramConflict = telegramPanelClient is not null || telegramAppUser is not null;

        if (emailConflict)
            conflicts.Add(await PanelConflictRecorder.UpsertAsync(context,
                $"email:{normalizedEmail}",
                PanelConflictTypes.Email,
                normalizedEmail,
                telegramId,
                panelClient.Id,
                emailPanelClient?.PanelClientId ?? emailAppUser?.PanelClientId,
                emailAppUser?.Id,
                new
                {
                    attemptedEmail = normalizedEmail,
                    attemptedPanelClientId = panelClient.Id,
                    existingPanelClientId = emailPanelClient?.PanelClientId,
                    existingAppUserId = emailAppUser?.Id,
                    existingAppUserPanelClientId = emailAppUser?.PanelClientId
                },
                now,
                cancellationToken));

        if (telegramConflict)
            conflicts.Add(await PanelConflictRecorder.UpsertAsync(context,
                $"tg:{telegramId}",
                PanelConflictTypes.Telegram,
                normalizedEmail,
                telegramId,
                panelClient.Id,
                telegramPanelClient?.PanelClientId ?? telegramAppUser?.PanelClientId,
                telegramAppUser?.Id,
                new
                {
                    attemptedTelegramId = telegramId,
                    attemptedPanelClientId = panelClient.Id,
                    existingPanelClientId = telegramPanelClient?.PanelClientId,
                    existingAppUserId = telegramAppUser?.Id,
                    existingAppUserPanelClientId = telegramAppUser?.PanelClientId
                },
                now,
                cancellationToken));

        // A brand new panel client cannot be inserted while its identity is
        // ambiguous because the mirror has unique email/Telegram constraints.
        if (existingClient is null && (emailConflict || telegramConflict))
        {
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            logger.LogWarning(
                "Panel client {PanelClientId} was quarantined due to an identity conflict.",
                panelClient.Id);

            return new PanelMirrorWriteResult(panelClient.Id, null, null, false, conflicts);
        }

        var effectiveEmail = emailConflict
            ? existingClient?.EmailNormalized ?? normalizedEmail
            : normalizedEmail;

        var effectiveTelegramId = telegramConflict
            ? existingClient?.TelegramId
            : telegramId;

        var row = existingClient;

        if (row is null)
        {
            row = new PanelClientEntity
            {
                Id = Guid.NewGuid(),
                PanelClientId = panelClient.Id
            };

            context.PanelClients.Add(row);
        }

        row.EmailNormalized = effectiveEmail;
        row.TelegramId = effectiveTelegramId;
        row.Enable = panelClient.Enable;
        row.ExpiryTime = FromUnixMilliseconds(panelClient.ExpiryTime);
        row.TotalGb = panelClient.TotalGB;
        row.LimitIp = panelClient.LimitIp;
        row.LimitHwid = panelClient.LimitHwid;
        row.TrafficReset = panelClient.TrafficReset;
        row.TrafficResetDay = panelClient.TrafficResetDay;
        row.Reset = panelClient.Reset;
        row.SubId = panelClient.SubId;
        row.Flow = panelClient.Flow;
        row.Security = panelClient.Security;
        row.TrafficEnabled = traffic?.Enable ?? (existingClient is null ? null : row.TrafficEnabled);
        row.TrafficUp = traffic?.Up ?? (existingClient is null ? 0 : row.TrafficUp);
        row.TrafficDown = traffic?.Down ?? (existingClient is null ? client.UsedTraffic : row.TrafficDown);
        row.LegacyComment = panelClient.Comment;
        row.PanelCreatedAt = client.CreatedAt;
        row.PanelUpdatedAt = client.UpdatedAt;
        row.LastSyncedAt = now;
        row.IsDeleted = false;
        row.DeletedAt = null;

        await context.SaveChangesAsync(cancellationToken);

        var inboundIds = (client.InboundIds ?? panelClient.InboundIds ?? [])
            .Where(id => id > 0)
            .Distinct()
            .ToArray();

        await EnsureInboundRowsAsync(context, inboundIds, now, cancellationToken);
        await ReplaceClientInboundsAsync(context, row.Id, inboundIds, cancellationToken);

        AppUser? appUser = existingAppUser;

        if (appUser is null && !emailConflict && !telegramConflict)
        {
            appUser = new AppUser
            {
                Id = Guid.NewGuid(),
                EmailNormalized = effectiveEmail,
                TelegramId = effectiveTelegramId,
                PanelClientId = row.PanelClientId,
                EmailVerifiedAt = null,
                CreatedAt = now,
                UpdatedAt = now
            };

            context.AppUsers.Add(appUser);
        }
        else if (appUser is not null)
        {
            if (!emailConflict && appUser.EmailNormalized != effectiveEmail)
            {
                appUser.EmailNormalized = effectiveEmail;
                appUser.EmailVerifiedAt = null;
            }

            if (!telegramConflict) appUser.TelegramId = effectiveTelegramId;

            appUser.UpdatedAt = now;
        }

        await PanelConflictRecorder.ResolveOwnedAsync(context,
            $"panel:{panelClient.Id}:missing", panelClient.Id, "panel-sync", now, cancellationToken);

        await PanelConflictRecorder.ResolveOwnedAsync(context,
            $"email:{effectiveEmail}", panelClient.Id, "panel-sync", now, cancellationToken);

        if (effectiveTelegramId is not null)
            await PanelConflictRecorder.ResolveOwnedAsync(context,
                $"tg:{effectiveTelegramId}", panelClient.Id, "panel-sync", now, cancellationToken);

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogDebug(
            "Mirrored panel client {PanelClientId}. Applied: {Applied}, Conflicts: {ConflictCount}",
            panelClient.Id, true, conflicts.Count);

        return new PanelMirrorWriteResult(panelClient.Id, row.Id, appUser?.Id, true, conflicts);
    }

    private static async Task EnsureInboundRowsAsync(AppDbContext context, IReadOnlyList<int> inboundIds,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (inboundIds.Count == 0) return;

        var existingIds = await context.PanelInbounds
            .Where(inbound => inboundIds.Contains(inbound.InboundId))
            .Select(inbound => inbound.InboundId)
            .ToListAsync(cancellationToken);

        var existingSet = existingIds.ToHashSet();

        foreach (var inboundId in inboundIds.Where(id => !existingSet.Contains(id)))
            context.PanelInbounds.Add(new PanelInbound
            {
                InboundId = inboundId,
                IsDefaultForNewClients = false,
                SortOrder = 0,
                LastSyncedAt = now
            });
    }

    private static async Task ReplaceClientInboundsAsync(AppDbContext context, Guid panelClientRowId,
        IReadOnlyList<int> inboundIds, CancellationToken cancellationToken)
    {
        var existing = await context.PanelClientInbounds
            .Where(inbound => inbound.PanelClientId == panelClientRowId)
            .ToListAsync(cancellationToken);

        var desired = inboundIds.ToHashSet();

        context.PanelClientInbounds.RemoveRange(
            existing.Where(inbound => !desired.Contains(inbound.InboundId)));

        var existingIds = existing.Select(inbound => inbound.InboundId).ToHashSet();

        foreach (var inboundId in inboundIds.Where(id => !existingIds.Contains(id)))
            context.PanelClientInbounds.Add(new PanelClientInbound
            {
                PanelClientId = panelClientRowId,
                InboundId = inboundId
            });
    }

    private static DateTimeOffset? FromUnixMilliseconds(long value)
    {
        return value > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(value) : null;
    }
}
