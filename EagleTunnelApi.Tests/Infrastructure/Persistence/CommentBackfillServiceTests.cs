using EagleTunnelApi.Application.Panel;
using EagleTunnelApi.Infrastructure.Persistence;
using EagleTunnelApi.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using PanelClientModel = EagleTunnelApi.Application.Panel.PanelClient;

namespace EagleTunnelApi.Tests.Infrastructure.Persistence;

[Collection(PostgresCollection.Name)]
public class CommentBackfillServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PanelCreatedAt = Now.AddDays(-10);
    private static readonly DateTimeOffset PanelUpdatedAt = Now.AddDays(-1);
    private static readonly DateTimeOffset Expiry = new(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);

    private readonly PostgresContainerFixture _fixture;

    public CommentBackfillServiceTests(PostgresContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RunAsync_backfills_all_comment_state_and_is_idempotent()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(_fixture.ConnectionString);
        using var provider = await CreateProviderAsync(database);
        var writer = provider.GetRequiredService<IPanelMirrorWriter>();

        await writer.UpsertAsync(CreateDetails("friend@example.com", 1, 111, null), null,
            CancellationToken.None);

        const string comment =
            "Referral credit: 30d · Referrer tgId: 111 · Referred by: friend@example.com · " +
            "Referral bonus paid · Trial until 2026-11-01 12:00 UTC · Cancelled 2026-10-20 · " +
            "Reminder 3d sent for 2026-11-01 · Reminder 1d sent for 2026-11-01";

        await writer.UpsertAsync(CreateDetails("referee@example.com", 2, 222, comment), null,
            CancellationToken.None);

        var backfill = provider.GetRequiredService<ICommentBackfill>();
        var first = await backfill.RunAsync(CancellationToken.None);

        Assert.Equal(2, first.ClientsScanned);
        Assert.Equal(1, first.ReferralsInserted);
        Assert.Equal(0, first.ReferralsUpdated);
        Assert.Equal(0, first.ReferralsSkipped);
        Assert.Equal(1, first.TrialsInserted);
        Assert.Equal(0, first.TrialsUpdated);
        Assert.Equal(1, first.CancellationsInserted);
        Assert.Equal(0, first.CancellationsUpdated);
        Assert.Equal(0, first.CancellationsCleared);
        Assert.Equal(2, first.RemindersInserted);
        Assert.Equal(1, first.CreditsInserted);
        Assert.Equal(0, first.CreditsUpdated);
        Assert.Empty(first.Problems);

        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var referrer = await context.AppUsers.AsNoTracking()
            .SingleAsync(user => user.PanelClientId == 1);
        var referee = await context.AppUsers.AsNoTracking()
            .SingleAsync(user => user.PanelClientId == 2);

        var attribution = await context.ReferralAttributions.AsNoTracking().SingleAsync();
        Assert.Equal("migration:referral:2", attribution.IdempotencyKey);
        Assert.Equal(ReferralAttributionSource.Migration, attribution.Source);
        Assert.Equal(ReferralAttributionStatus.Paid, attribution.Status);
        Assert.Equal(referrer.Id, attribution.ReferrerUserId);
        Assert.Equal(referee.Id, attribution.RefereeUserId);
        Assert.Equal("friend@example.com", attribution.ReferrerEmail);
        Assert.Equal(111, attribution.ReferrerTelegramId);
        Assert.Equal(PanelCreatedAt, attribution.CreatedAt);
        Assert.Equal(PanelUpdatedAt, attribution.PaidAt);

        var trial = await context.TrialPeriods.AsNoTracking().SingleAsync();
        Assert.Equal(referee.Id, trial.UserId);
        Assert.Equal("migration", trial.Source);
        Assert.Equal(PanelCreatedAt, trial.StartsAt);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero), trial.EndsAt);
        Assert.Equal(Now, trial.CreatedAt);

        var cancellation = await context.CancellationStates.AsNoTracking().SingleAsync();
        Assert.Equal(referee.Id, cancellation.UserId);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero), cancellation.CancelledAt);
        Assert.Equal(new DateOnly(2026, 11, 5), cancellation.ExpiresOn!.Value);
        Assert.Null(cancellation.ClearedAt);

        var reminders = await context.ReminderDeliveries.AsNoTracking()
            .OrderBy(delivery => delivery.DaysBefore)
            .ToListAsync();
        Assert.Equal(2, reminders.Count);
        Assert.All(reminders, delivery =>
        {
            Assert.Equal(referee.Id, delivery.UserId);
            Assert.Equal(new DateOnly(2026, 11, 1), delivery.ExpiryDate);
            Assert.Equal(PanelUpdatedAt, delivery.SentAt);
        });
        Assert.Equal(new[] { 1, 3 }, reminders.Select(delivery => delivery.DaysBefore));

        var credit = await context.CreditEntries.AsNoTracking().SingleAsync();
        Assert.Equal(referee.Id, credit.UserId);
        Assert.Equal(CreditEntryUnit.Days, credit.Unit);
        Assert.Equal(30m, credit.Amount);
        Assert.Equal("migration", credit.Reason);
        Assert.Equal("panel_client", credit.ReferenceType);
        Assert.Equal("2", credit.ReferenceId);
        Assert.Equal("migration:credit:2", credit.IdempotencyKey);
        Assert.Equal(PanelCreatedAt, credit.OccurredAt);

        var second = await backfill.RunAsync(CancellationToken.None);

        Assert.Equal(2, second.ClientsScanned);
        Assert.Equal(0, second.ReferralsInserted);
        Assert.Equal(0, second.ReferralsUpdated);
        Assert.Equal(0, second.ReferralsSkipped);
        Assert.Equal(0, second.TrialsInserted);
        Assert.Equal(0, second.TrialsUpdated);
        Assert.Equal(0, second.CancellationsInserted);
        Assert.Equal(0, second.CancellationsUpdated);
        Assert.Equal(0, second.CancellationsCleared);
        Assert.Equal(0, second.RemindersInserted);
        Assert.Equal(0, second.CreditsInserted);
        Assert.Equal(0, second.CreditsUpdated);
        Assert.Empty(second.Problems);

        var verifyScope = provider.CreateScope();
        var verifyContext = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await verifyContext.ReferralAttributions.CountAsync());
        Assert.Equal(1, await verifyContext.TrialPeriods.CountAsync());
        Assert.Equal(1, await verifyContext.CancellationStates.CountAsync());
        Assert.Equal(2, await verifyContext.ReminderDeliveries.CountAsync());
        Assert.Equal(1, await verifyContext.CreditEntries.CountAsync());
    }

    [Fact]
    public async Task RunAsync_voids_referral_and_clears_cancellation_when_tags_are_removed()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(_fixture.ConnectionString);
        using var provider = await CreateProviderAsync(database);
        var writer = provider.GetRequiredService<IPanelMirrorWriter>();

        await writer.UpsertAsync(CreateDetails("friend@example.com", 1, 111, null), null,
            CancellationToken.None);
        await writer.UpsertAsync(CreateDetails("referee@example.com", 2, 222,
            "Referred by: friend@example.com · Cancelled 2026-10-20 · Referral credit: 30d"), null,
            CancellationToken.None);

        var backfill = provider.GetRequiredService<ICommentBackfill>();
        await backfill.RunAsync(CancellationToken.None);

        using (var updateScope = provider.CreateScope())
        {
            var context = updateScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var mirror = await context.PanelClients.SingleAsync(client => client.PanelClientId == 2);
            mirror.LegacyComment = "plain";
            mirror.PanelUpdatedAt = Now;
            await context.SaveChangesAsync();
        }

        var report = await backfill.RunAsync(CancellationToken.None);

        Assert.Equal(1, report.ReferralsUpdated);
        Assert.Equal(1, report.CancellationsCleared);

        using var scope = provider.CreateScope();
        var readContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var attribution = await readContext.ReferralAttributions.AsNoTracking().SingleAsync();
        Assert.Equal(ReferralAttributionStatus.Void, attribution.Status);
        Assert.Null(attribution.PaidAt);

        var cancellation = await readContext.CancellationStates.AsNoTracking().SingleAsync();
        Assert.Equal(Now, cancellation.ClearedAt);

        var credit = await readContext.CreditEntries.AsNoTracking().SingleAsync();
        Assert.Equal(30m, credit.Amount);
    }

    [Fact]
    public async Task RunAsync_records_a_pending_referral_without_a_resolved_referrer()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(_fixture.ConnectionString);
        using var provider = await CreateProviderAsync(database);
        var writer = provider.GetRequiredService<IPanelMirrorWriter>();

        await writer.UpsertAsync(CreateDetails("referee@example.com", 2, 222,
            "Referred by: missing@example.com · Referrer tgId: 999"), null,
            CancellationToken.None);

        var report = await provider.GetRequiredService<ICommentBackfill>().RunAsync(CancellationToken.None);

        Assert.Equal(1, report.ReferralsInserted);
        Assert.Empty(report.Problems);

        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var attribution = await context.ReferralAttributions.AsNoTracking().SingleAsync();

        Assert.Equal(ReferralAttributionStatus.Pending, attribution.Status);
        Assert.Null(attribution.ReferrerUserId);
        Assert.Equal("missing@example.com", attribution.ReferrerEmail);
        Assert.Equal(999, attribution.ReferrerTelegramId);
    }

    [Fact]
    public async Task RunAsync_skips_a_referral_when_app_owned_attribution_exists()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(_fixture.ConnectionString);
        using var provider = await CreateProviderAsync(database);
        var writer = provider.GetRequiredService<IPanelMirrorWriter>();

        await writer.UpsertAsync(CreateDetails("friend@example.com", 1, 111, null), null,
            CancellationToken.None);
        await writer.UpsertAsync(CreateDetails("referee@example.com", 2, 222,
            "Referred by: friend@example.com"), null, CancellationToken.None);

        using (var seedScope = provider.CreateScope())
        {
            var context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var referee = await context.AppUsers.SingleAsync(user => user.PanelClientId == 2);
            context.ReferralAttributions.Add(new ReferralAttribution
            {
                Id = Guid.NewGuid(),
                RefereeUserId = referee.Id,
                Status = ReferralAttributionStatus.Pending,
                Source = ReferralAttributionSource.Telegram,
                CreatedAt = Now.AddDays(-2),
                IdempotencyKey = "telegram:referee"
            });
            await context.SaveChangesAsync();
        }

        var report = await provider.GetRequiredService<ICommentBackfill>().RunAsync(CancellationToken.None);

        Assert.Equal(1, report.ReferralsSkipped);

        using var scope = provider.CreateScope();
        var readContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var attribution = await readContext.ReferralAttributions.AsNoTracking().SingleAsync();
        Assert.Equal(ReferralAttributionSource.Telegram, attribution.Source);
    }

    [Fact]
    public async Task RunAsync_reports_a_missing_app_user()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(_fixture.ConnectionString);
        using var provider = await CreateProviderAsync(database);
        var writer = provider.GetRequiredService<IPanelMirrorWriter>();

        await writer.UpsertAsync(CreateDetails("orphan@example.com", 2, 222,
            "Referred by: friend@example.com"), null, CancellationToken.None);

        using (var deleteScope = provider.CreateScope())
        {
            var context = deleteScope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.AppUsers.RemoveRange(context.AppUsers);
            await context.SaveChangesAsync();
        }

        var report = await provider.GetRequiredService<ICommentBackfill>().RunAsync(CancellationToken.None);

        Assert.Equal(1, report.ClientsScanned);
        Assert.Contains(report.Problems, problem => problem.Contains("has no app user"));
        Assert.Equal(0, report.ReferralsInserted);
    }

    private async Task<ServiceProvider> CreateProviderAsync(PostgresTestDatabase database)
    {
        var provider = PersistenceTestProvider.Build(database.ConnectionString,
            timeProvider: new FixedTimeProvider(Now));
        await provider.GetRequiredService<DatabaseMigrationRunner>().MigrateAsync();
        return provider;
    }

    private static PanelClientDetails CreateDetails(string email, long panelClientId, long telegramId,
        string? comment)
    {
        var client = new PanelClientModel(
            $"uuid-{panelClientId}",
            email,
            true,
            Expiry.ToUnixTimeMilliseconds(),
            telegramId,
            100L * 1024 * 1024 * 1024,
            comment,
            2,
            3,
            "monthly",
            4,
            1,
            "reality",
            $"sub-{panelClientId}",
            "xtls-rprx-vision",
            panelClientId,
            [1]);

        return new PanelClientDetails(client, null, [1], 0)
        {
            CreatedAt = PanelCreatedAt,
            UpdatedAt = PanelUpdatedAt
        };
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
