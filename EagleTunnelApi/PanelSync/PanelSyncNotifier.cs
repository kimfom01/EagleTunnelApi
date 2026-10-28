using System.Text;
using EagleTunnelApi.Application.Panel;
using EagleTunnelApi.Configuration;
using EagleTunnelApi.Infrastructure.Persistence;
using EagleTunnelApi.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Telegram.Bot;

namespace EagleTunnelApi.PanelSync;

/// <summary>
/// Sends Telegram alerts for panel sync conflicts and repeated failures.
/// Conflict messages include only identity and status fields; conflict details,
/// tokens, and subscription URLs are never sent.
/// </summary>
public sealed class PanelSyncNotifier(
    IDbContextFactory<AppDbContext> contextFactory,
    ITelegramBotClient botClient,
    IOptions<TelegramOptions> telegramOptions,
    IOptions<PanelSyncOptions> syncOptions,
    TimeProvider timeProvider,
    ILogger<PanelSyncNotifier> logger) : IPanelSyncNotifier
{
    private const int FailureMessageMaxLength = 500;
    private readonly TelegramOptions _telegramOptions = telegramOptions.Value;
    private readonly PanelSyncOptions _syncOptions = syncOptions.Value;

    public async Task<int> NotifyOpenConflictsAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var conflicts = await context.PanelSyncConflicts
            .Where(conflict => conflict.Status == PanelSyncConflictStatus.Open &&
                conflict.NotifiedAt == null)
            .OrderBy(conflict => conflict.FirstSeenAt)
            .Take(_syncOptions.ConflictNotificationBatchSize)
            .ToListAsync(cancellationToken);

        if (conflicts.Count == 0) return 0;

        var adminIds = _telegramOptions.AdminIds.Distinct().ToArray();

        if (adminIds.Length == 0)
        {
            logger.LogWarning(
                "Panel sync conflicts are waiting to be notified, but no Telegram admin ids are configured.");
            return 0;
        }

        var now = timeProvider.GetUtcNow();
        var notified = 0;

        foreach (var conflict in conflicts)
        {
            var message = BuildConflictMessage(conflict);
            var sent = false;

            foreach (var adminId in adminIds)
            {
                try
                {
                    await botClient.SendMessage(adminId, message,
                        cancellationToken: cancellationToken);
                    sent = true;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Failed to notify Telegram admin {AdminId} about panel conflict {ConflictId}.",
                        adminId, conflict.Id);
                }
            }

            if (!sent) continue;

            conflict.NotifiedAt = now;
            notified++;
        }

        await context.SaveChangesAsync(cancellationToken);
        return notified;
    }

    public async Task NotifySyncFailureAsync(int consecutiveFailures, string error,
        CancellationToken cancellationToken)
    {
        var adminIds = _telegramOptions.AdminIds.Distinct().ToArray();

        if (adminIds.Length == 0)
        {
            logger.LogWarning(
                "Panel sync has failed {ConsecutiveFailures} times, but no Telegram admin ids are configured.",
                consecutiveFailures);
            return;
        }

        var message = BuildFailureMessage(consecutiveFailures, error);

        foreach (var adminId in adminIds)
        {
            try
            {
                await botClient.SendMessage(adminId, message,
                    cancellationToken: cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Failed to notify Telegram admin {AdminId} about repeated panel sync failures.",
                    adminId);
            }
        }
    }

    public static string BuildConflictMessage(PanelSyncConflict conflict)
    {
        var builder = new StringBuilder();

        builder.AppendLine("⚠️ Panel sync conflict");
        builder.AppendLine($"Type: {conflict.Type}");
        builder.AppendLine($"Key: {conflict.DedupeKey}");

        if (!string.IsNullOrWhiteSpace(conflict.EmailNormalized))
            builder.AppendLine($"Email: {conflict.EmailNormalized}");

        if (conflict.TelegramId is not null)
            builder.AppendLine($"Telegram id: {conflict.TelegramId}");

        if (conflict.PanelClientId is not null)
            builder.AppendLine($"Panel client: {conflict.PanelClientId}");

        if (conflict.OtherPanelClientId is not null)
            builder.AppendLine($"Other panel client: {conflict.OtherPanelClientId}");

        if (conflict.AppUserId is not null)
            builder.AppendLine($"App user: {conflict.AppUserId}");

        builder.AppendLine($"Conflict id: {conflict.Id}");
        builder.Append("Open /admin -> Panel Management -> Conflicts.");

        return builder.ToString();
    }

    public static string BuildFailureMessage(int consecutiveFailures, string error)
    {
        var safeError = string.IsNullOrWhiteSpace(error) ? "unknown error" : error.Trim();

        safeError = safeError.ReplaceLineEndings(" ");

        if (safeError.Length > FailureMessageMaxLength)
            safeError = safeError[..FailureMessageMaxLength] + "…";

        return $"⚠️ Panel sync is failing\n" +
            $"Consecutive failures: {consecutiveFailures}\n" +
            $"Last error: {safeError}\n" +
            "Check /admin -> Panel Management for status.";
    }
}
