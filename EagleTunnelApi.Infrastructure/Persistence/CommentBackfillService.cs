using System.Globalization;
using System.Text.Json;
using EagleTunnelApi.Application.Panel;
using EagleTunnelApi.Domain.Identity;
using EagleTunnelApi.Domain.Referrals;
using EagleTunnelApi.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PanelClientEntity = EagleTunnelApi.Infrastructure.Persistence.Entities.PanelClient;

namespace EagleTunnelApi.Infrastructure.Persistence;

/// <summary>
/// Rebuilds app-owned referral, trial, cancellation, reminder, and credit state
/// from the legacy tags in <c>panel_clients.legacy_comment</c>. The operation is
/// idempotent: referral/credit keys are stable, cancellation state is keyed by
/// user, trial rows are reused per user, and reminder rows use the database's
/// unique (user, expiry_date, days_before) key.
/// </summary>
public sealed class CommentBackfillService(
    IDbContextFactory<AppDbContext> contextFactory,
    TimeProvider timeProvider,
    ILogger<CommentBackfillService> logger) : ICommentBackfill
{
    private const string MigrationSource = "migration";
    private const string MigrationReason = "migration";

    public async Task<CommentBackfillReport> RunAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var clients = await context.PanelClients
            .AsNoTracking()
            .Where(client => !client.IsDeleted)
            .OrderBy(client => client.PanelClientId)
            .ToListAsync(cancellationToken);

        var users = await context.AppUsers
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var usersByPanelClientId = users
            .GroupBy(user => user.PanelClientId)
            .ToDictionary(group => group.Key, group => group.First());

        var usersByEmail = users
            .GroupBy(user => user.EmailNormalized, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var usersByTelegramId = users
            .Where(user => user.TelegramId.HasValue)
            .GroupBy(user => user.TelegramId!.Value)
            .ToDictionary(group => group.Key, group => group.First());

        var attributions = await context.ReferralAttributions
            .ToListAsync(cancellationToken);

        var migrationAttributions = attributions
            .Where(attribution => attribution.Source == MigrationSource)
            .ToDictionary(attribution => attribution.IdempotencyKey);

        var refereesWithAttribution = attributions
            .Where(attribution => attribution.RefereeUserId.HasValue)
            .Select(attribution => attribution.RefereeUserId!.Value)
            .ToHashSet();

        var trials = await context.TrialPeriods
            .Where(period => period.Source == MigrationSource)
            .ToListAsync(cancellationToken);

        var migrationTrials = trials
            .GroupBy(period => period.UserId)
            .ToDictionary(group => group.Key,
                group => group.OrderByDescending(period => period.CreatedAt).First());

        var cancellations = await context.CancellationStates
            .ToListAsync(cancellationToken);

        var cancellationsByUser = cancellations
            .ToDictionary(state => state.UserId);

        var reminders = await context.ReminderDeliveries
            .ToListAsync(cancellationToken);

        var reminderKeys = reminders
            .Select(delivery => (delivery.UserId, delivery.ExpiryDate, delivery.DaysBefore))
            .ToHashSet();

        var credits = await context.CreditEntries
            .Where(entry => entry.Reason == MigrationReason)
            .ToListAsync(cancellationToken);

        var migrationCredits = credits
            .ToDictionary(entry => entry.IdempotencyKey);

        var now = timeProvider.GetUtcNow();
        var problems = new List<string>();

        var referralsInserted = 0;
        var referralsUpdated = 0;
        var referralsSkipped = 0;
        var trialsInserted = 0;
        var trialsUpdated = 0;
        var cancellationsInserted = 0;
        var cancellationsUpdated = 0;
        var cancellationsCleared = 0;
        var remindersInserted = 0;
        var creditsInserted = 0;
        var creditsUpdated = 0;

        foreach (var client in clients)
        {
            if (!usersByPanelClientId.TryGetValue(client.PanelClientId, out var user))
            {
                problems.Add(
                    $"Panel client {client.PanelClientId} ({client.EmailNormalized}) has no app user; comment state skipped.");
                continue;
            }

            var comment = client.LegacyComment;

            BackfillReferral(context, client, user, comment, now, usersByEmail, usersByTelegramId,
                migrationAttributions, refereesWithAttribution, problems,
                ref referralsInserted, ref referralsUpdated, ref referralsSkipped);

            BackfillTrial(context, client, user, comment, now, migrationTrials, problems,
                ref trialsInserted, ref trialsUpdated);

            BackfillCancellation(context, client, user, comment, now, cancellationsByUser, problems,
                ref cancellationsInserted, ref cancellationsUpdated, ref cancellationsCleared);

            BackfillReminders(context, client, user, comment, now, reminderKeys,
                ref remindersInserted);

            BackfillCredit(context, client, user, comment, now, migrationCredits, problems,
                ref creditsInserted, ref creditsUpdated);
        }

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Comment backfill completed. Clients: {Clients}, referrals +{ReferralIns}/~{ReferralUpd}/skipped {ReferralSkip}, " +
            "trials +{TrialIns}/~{TrialUpd}, cancellations +{CancelIns}/~{CancelUpd}/cleared {CancelClear}, " +
            "reminders +{ReminderIns}, credits +{CreditIns}/~{CreditUpd}, problems: {Problems}",
            clients.Count, referralsInserted, referralsUpdated, referralsSkipped,
            trialsInserted, trialsUpdated, cancellationsInserted, cancellationsUpdated,
            cancellationsCleared, remindersInserted, creditsInserted, creditsUpdated, problems.Count);

        return new CommentBackfillReport(
            clients.Count,
            referralsInserted,
            referralsUpdated,
            referralsSkipped,
            trialsInserted,
            trialsUpdated,
            cancellationsInserted,
            cancellationsUpdated,
            cancellationsCleared,
            remindersInserted,
            creditsInserted,
            creditsUpdated,
            problems);
    }

    private static void BackfillReferral(AppDbContext context, PanelClientEntity client, AppUser user,
        string? comment, DateTimeOffset now, IReadOnlyDictionary<string, AppUser> usersByEmail,
        IReadOnlyDictionary<long, AppUser> usersByTelegramId,
        IDictionary<string, ReferralAttribution> migrationAttributions,
        ISet<Guid> refereesWithAttribution, ICollection<string> problems,
        ref int inserted, ref int updated, ref int skipped)
    {
        var hasReferredBy = ReferralService.TryParseReferredBy(comment, out var referrerEmailRaw, out _);
        var hasReferrerTelegram = ReferralService.TryParseReferrerTgId(comment, out var referrerTelegramId);
        var paid = ReferralService.HasBonusPaidMarker(comment);
        var key = $"migration:referral:{client.PanelClientId}";

        migrationAttributions.TryGetValue(key, out var attribution);

        if (!hasReferredBy && !hasReferrerTelegram)
        {
            if (comment?.Contains(ReferralService.ReferredByTag, StringComparison.OrdinalIgnoreCase) is true ||
                comment?.Contains(ReferralService.PendingReferredByTag, StringComparison.OrdinalIgnoreCase) is true)
                problems.Add($"Panel client {client.PanelClientId}: malformed referral attribution tag.");

            if (attribution is null) return;

            var newStatus = paid ? ReferralAttributionStatus.Paid : ReferralAttributionStatus.Void;

            if (attribution.Status == newStatus &&
                (!paid || attribution.PaidAt is not null))
                return;

            attribution.Status = newStatus;
            attribution.PaidAt = paid ? attribution.PaidAt ?? client.PanelUpdatedAt ?? now : null;
            updated++;
            return;
        }

        var referrerEmail = hasReferredBy ? EmailAddress.Normalize(referrerEmailRaw) : null;
        var referrerUser = ResolveReferrer(referrerEmail, referrerTelegramId, usersByEmail,
            usersByTelegramId, out var ambiguous);

        if (ambiguous)
            problems.Add(
                $"Panel client {client.PanelClientId}: referrer email and Telegram id resolve to different app users.");

        var selfReferral = (referrerEmail is not null && referrerEmail == client.EmailNormalized) ||
            (referrerTelegramId > 0 && referrerTelegramId == client.TelegramId);

        if (selfReferral)
            problems.Add($"Panel client {client.PanelClientId}: self-referral recorded as void.");

        var isNew = false;

        if (attribution is null)
        {
            if (refereesWithAttribution.Contains(user.Id))
            {
                skipped++;
                problems.Add(
                    $"Panel client {client.PanelClientId}: an app-owned referral attribution already exists; migration row skipped.");
                return;
            }

            attribution = new ReferralAttribution
            {
                Id = Guid.NewGuid(),
                RefereeUserId = user.Id,
                Source = MigrationSource,
                IdempotencyKey = key,
                CreatedAt = client.PanelCreatedAt ?? now
            };

            context.ReferralAttributions.Add(attribution);
            migrationAttributions[key] = attribution;
            refereesWithAttribution.Add(user.Id);
            isNew = true;
            inserted++;
        }

        var status = selfReferral
            ? ReferralAttributionStatus.Void
            : paid
                ? ReferralAttributionStatus.Paid
                : ReferralAttributionStatus.Pending;

        DateTimeOffset? paidAt = status == ReferralAttributionStatus.Paid
            ? attribution.PaidAt ?? client.PanelUpdatedAt ?? now
            : null;

        var changed = attribution.ReferrerUserId != referrerUser?.Id ||
            attribution.ReferrerEmail != referrerEmail ||
            attribution.ReferrerTelegramId != (hasReferrerTelegram ? referrerTelegramId : null) ||
            attribution.RefereeUserId != user.Id ||
            attribution.Status != status ||
            attribution.PaidAt != paidAt;

        attribution.ReferrerUserId = referrerUser?.Id;
        attribution.ReferrerEmail = referrerEmail;
        attribution.ReferrerTelegramId = hasReferrerTelegram ? referrerTelegramId : null;
        attribution.RefereeUserId = user.Id;
        attribution.Status = status;
        attribution.PaidAt = paidAt;

        if (!isNew && changed) updated++;
    }

    private static void BackfillTrial(AppDbContext context, PanelClientEntity client, AppUser user,
        string? comment, DateTimeOffset now, IDictionary<Guid, TrialPeriod> migrationTrials,
        ICollection<string> problems, ref int inserted, ref int updated)
    {
        if (!ReferralService.TryGetTrialEnd(comment, out var trialEnds))
        {
            if (comment?.Contains(ReferralService.TrialTagPrefix, StringComparison.OrdinalIgnoreCase) is true)
                problems.Add($"Panel client {client.PanelClientId}: malformed trial tag.");

            return;
        }

        var startsAt = client.PanelCreatedAt is { } createdAt && createdAt < trialEnds
            ? createdAt
            : trialEnds.AddHours(-1);

        if (migrationTrials.TryGetValue(user.Id, out var trial))
        {
            if (trial.StartsAt == startsAt && trial.EndsAt == trialEnds) return;

            trial.StartsAt = startsAt;
            trial.EndsAt = trialEnds;
            updated++;
            return;
        }

        trial = new TrialPeriod
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            StartsAt = startsAt,
            EndsAt = trialEnds,
            Source = MigrationSource,
            CreatedAt = now
        };

        context.TrialPeriods.Add(trial);
        migrationTrials[user.Id] = trial;
        inserted++;
    }

    private static void BackfillCancellation(AppDbContext context, PanelClientEntity client, AppUser user,
        string? comment, DateTimeOffset now, IDictionary<Guid, CancellationState> cancellationsByUser,
        ICollection<string> problems, ref int inserted, ref int updated, ref int cleared)
    {
        if (ReferralService.TryGetCancelledDate(comment, out var cancelledDate))
        {
            var cancelledAt = new DateTimeOffset(
                cancelledDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
            DateOnly? expiresOn = client.ExpiryTime is { } expiry
                ? DateOnly.FromDateTime(expiry.UtcDateTime)
                : null;

            if (cancellationsByUser.TryGetValue(user.Id, out var state))
            {
                if (state.CancelledAt == cancelledAt &&
                    state.ExpiresOn == expiresOn &&
                    state.ClearedAt is null)
                    return;

                state.CancelledAt = cancelledAt;
                state.ExpiresOn = expiresOn;
                state.ClearedAt = null;
                updated++;
                return;
            }

            state = new CancellationState
            {
                UserId = user.Id,
                CancelledAt = cancelledAt,
                ExpiresOn = expiresOn,
                ClearedAt = null
            };

            context.CancellationStates.Add(state);
            cancellationsByUser[user.Id] = state;
            inserted++;
            return;
        }

        if (comment?.Contains(ReferralService.CancelledTagPrefix, StringComparison.OrdinalIgnoreCase) is true)
            problems.Add($"Panel client {client.PanelClientId}: malformed cancellation tag.");

        if (cancellationsByUser.TryGetValue(user.Id, out var existing) && existing.ClearedAt is null)
        {
            existing.ClearedAt = now;
            cleared++;
        }
    }

    private static void BackfillReminders(AppDbContext context, PanelClientEntity client, AppUser user,
        string? comment, DateTimeOffset now,
        ISet<(Guid UserId, DateOnly ExpiryDate, int DaysBefore)> reminderKeys, ref int inserted)
    {
        foreach (var marker in ReferralService.GetReminderMarkers(comment))
        {
            var key = (user.Id, marker.ExpiryDate, marker.DaysBefore);

            if (!reminderKeys.Add(key)) continue;

            context.ReminderDeliveries.Add(new ReminderDelivery
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                ExpiryDate = marker.ExpiryDate,
                DaysBefore = marker.DaysBefore,
                SentAt = client.PanelUpdatedAt ?? now
            });

            inserted++;
        }
    }

    private static void BackfillCredit(AppDbContext context, PanelClientEntity client, AppUser user,
        string? comment, DateTimeOffset now, IDictionary<string, CreditEntry> migrationCredits,
        ICollection<string> problems, ref int inserted, ref int updated)
    {
        var creditDays = ReferralService.GetCreditDays(comment);

        if (creditDays <= 0)
        {
            if (comment?.Contains(ReferralService.CreditTagPrefix, StringComparison.OrdinalIgnoreCase) is true)
                problems.Add($"Panel client {client.PanelClientId}: malformed credit tag.");

            return;
        }

        var key = $"migration:credit:{client.PanelClientId}";

        if (migrationCredits.TryGetValue(key, out var credit))
        {
            if (credit.Unit == CreditEntryUnit.Days && credit.Amount == creditDays) return;

            credit.Unit = CreditEntryUnit.Days;
            credit.Amount = creditDays;
            credit.Currency = null;
            credit.Metadata = JsonSerializer.SerializeToDocument(new
            {
                source = "panel_comment",
                panelClientId = client.PanelClientId,
                creditDays
            });
            updated++;
            return;
        }

        credit = new CreditEntry
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Unit = CreditEntryUnit.Days,
            Amount = creditDays,
            Currency = null,
            Reason = MigrationReason,
            ReferenceType = "panel_client",
            ReferenceId = client.PanelClientId.ToString(CultureInfo.InvariantCulture),
            IdempotencyKey = key,
            OccurredAt = client.PanelCreatedAt ?? now,
            ExpiresAt = null,
            Metadata = JsonSerializer.SerializeToDocument(new
            {
                source = "panel_comment",
                panelClientId = client.PanelClientId,
                creditDays
            })
        };

        context.CreditEntries.Add(credit);
        migrationCredits[key] = credit;
        inserted++;
    }

    private static AppUser? ResolveReferrer(string? email, long telegramId,
        IReadOnlyDictionary<string, AppUser> usersByEmail,
        IReadOnlyDictionary<long, AppUser> usersByTelegramId, out bool ambiguous)
    {
        ambiguous = false;

        AppUser? emailUser = null;
        if (email is not null) usersByEmail.TryGetValue(email, out emailUser);

        AppUser? telegramUser = null;
        if (telegramId > 0) usersByTelegramId.TryGetValue(telegramId, out telegramUser);

        if (emailUser is not null && telegramUser is not null && emailUser.Id != telegramUser.Id)
        {
            ambiguous = true;
            return null;
        }

        return emailUser ?? telegramUser;
    }
}
