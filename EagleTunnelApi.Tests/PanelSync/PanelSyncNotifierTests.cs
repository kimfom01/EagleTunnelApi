using System.Text.Json;
using EagleTunnelApi.Application.Panel;
using EagleTunnelApi.Configuration;
using EagleTunnelApi.Infrastructure.Persistence;
using EagleTunnelApi.Infrastructure.Persistence.Entities;
using EagleTunnelApi.PanelSync;
using EagleTunnelApi.Tests.Helpers;
using EagleTunnelApi.Tests.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EagleTunnelApi.Tests.PanelSync;

[Collection(PostgresCollection.Name)]
public class PanelSyncNotifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresContainerFixture _fixture;

    public PanelSyncNotifierTests(PostgresContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task NotifyOpenConflictsAsync_sends_one_message_per_admin_and_marks_notified()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(_fixture.ConnectionString);
        using var provider = await CreateProviderAsync(database);
        var factory = provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        var conflict = await SeedConflictAsync(factory);

        var bot = new FakeTelegramBotClient();
        var notifier = CreateNotifier(factory, bot, [111, 222]);

        var notified = await notifier.NotifyOpenConflictsAsync(CancellationToken.None);

        Assert.Equal(1, notified);
        Assert.Equal(2, bot.Requests.Count);

        foreach (var request in bot.Requests)
        {
            var message = GetMessageText(request.Body);

            Assert.Contains("Panel sync conflict", message);
            Assert.Contains(conflict.Id.ToString(), message);
            Assert.Contains("email:user@example.com", message);
            Assert.Contains("Email: user@example.com", message);
            Assert.Contains("Panel client: 101", message);
            Assert.Contains("Other panel client: 202", message);
            Assert.Contains("Telegram id: 555", message);
            Assert.Contains("/admin -> Panel Management -> Conflicts", message);
            Assert.DoesNotContain("secret-token", message);
            Assert.DoesNotContain("https://secret.example", message);
        }

        using (var readScope = provider.CreateScope())
        {
            var context = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stored = await context.PanelSyncConflicts.AsNoTracking().SingleAsync();
            Assert.Equal(Now, stored.NotifiedAt);
        }

        var second = await notifier.NotifyOpenConflictsAsync(CancellationToken.None);

        Assert.Equal(0, second);
        Assert.Equal(2, bot.Requests.Count);
    }

    [Fact]
    public async Task NotifyOpenConflictsAsync_skips_when_no_admins_are_configured()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(_fixture.ConnectionString);
        using var provider = await CreateProviderAsync(database);
        var factory = provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await SeedConflictAsync(factory);

        var bot = new FakeTelegramBotClient();
        var notifier = CreateNotifier(factory, bot, []);

        var notified = await notifier.NotifyOpenConflictsAsync(CancellationToken.None);

        Assert.Equal(0, notified);
        Assert.Empty(bot.Requests);

        using var readScope = provider.CreateScope();
        var context = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await context.PanelSyncConflicts.AsNoTracking().SingleAsync();
        Assert.Null(stored.NotifiedAt);
    }

    [Fact]
    public async Task NotifyOpenConflictsAsync_retries_when_every_admin_send_fails()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(_fixture.ConnectionString);
        using var provider = await CreateProviderAsync(database);
        var factory = provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await SeedConflictAsync(factory);

        var bot = new FakeTelegramBotClient
        {
            FailureFactory = (_, _) => new InvalidOperationException("telegram unavailable")
        };
        var notifier = CreateNotifier(factory, bot, [111, 222]);

        var failed = await notifier.NotifyOpenConflictsAsync(CancellationToken.None);

        Assert.Equal(0, failed);
        Assert.Equal(2, bot.Requests.Count);

        using (var readScope = provider.CreateScope())
        {
            var context = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stored = await context.PanelSyncConflicts.AsNoTracking().SingleAsync();
            Assert.Null(stored.NotifiedAt);
        }

        bot.FailureFactory = null;

        var retried = await notifier.NotifyOpenConflictsAsync(CancellationToken.None);

        Assert.Equal(1, retried);
        Assert.Equal(4, bot.Requests.Count);

        using (var readScope = provider.CreateScope())
        {
            var context = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stored = await context.PanelSyncConflicts.AsNoTracking().SingleAsync();
            Assert.Equal(Now, stored.NotifiedAt);
        }
    }

    [Fact]
    public async Task NotifySyncFailureAsync_sends_a_failure_alert_to_every_admin()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(_fixture.ConnectionString);
        using var provider = await CreateProviderAsync(database);
        var factory = provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        var bot = new FakeTelegramBotClient();
        var notifier = CreateNotifier(factory, bot, [111, 222]);

        await notifier.NotifySyncFailureAsync(3, "panel unavailable", CancellationToken.None);

        Assert.Equal(2, bot.Requests.Count);
        Assert.All(bot.Requests, request =>
        {
            var message = GetMessageText(request.Body);

            Assert.Contains("Panel sync is failing", message);
            Assert.Contains("Consecutive failures: 3", message);
            Assert.Contains("panel unavailable", message);
            Assert.Contains("/admin -> Panel Management", message);
        });
    }

    private static string GetMessageText(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("text").GetString()!;
    }

    private async Task<ServiceProvider> CreateProviderAsync(PostgresTestDatabase database)
    {
        var provider = PersistenceTestProvider.Build(database.ConnectionString,
            timeProvider: new FixedTimeProvider(Now));

        await provider.GetRequiredService<DatabaseMigrationRunner>().MigrateAsync();
        return provider;
    }

    private static PanelSyncNotifier CreateNotifier(
        IDbContextFactory<AppDbContext> factory,
        FakeTelegramBotClient bot,
        long[] adminIds)
    {
        return new PanelSyncNotifier(factory, bot,
            Options.Create(new TelegramOptions { AdminIds = adminIds }),
            Options.Create(new PanelSyncOptions()),
            new FixedTimeProvider(Now),
            NullLogger<PanelSyncNotifier>.Instance);
    }

    private static async Task<PanelSyncConflict> SeedConflictAsync(
        IDbContextFactory<AppDbContext> factory)
    {
        await using var context = await factory.CreateDbContextAsync();

        var conflict = new PanelSyncConflict
        {
            Id = Guid.NewGuid(),
            DedupeKey = "email:user@example.com",
            Type = PanelConflictTypes.Email,
            EmailNormalized = "user@example.com",
            TelegramId = 555,
            PanelClientId = 101,
            OtherPanelClientId = 202,
            Details = JsonSerializer.SerializeToDocument(new
            {
                token = "secret-token",
                subscriptionUrl = "https://secret.example/sub"
            }),
            Status = PanelSyncConflictStatus.Open,
            FirstSeenAt = Now.AddMinutes(-5),
            LastSeenAt = Now.AddMinutes(-1)
        };

        context.PanelSyncConflicts.Add(conflict);
        await context.SaveChangesAsync();
        return conflict;
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
