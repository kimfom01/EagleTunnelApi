using EagleTunnelApi.Application.Panel;
using EagleTunnelApi.Infrastructure.Persistence;
using EagleTunnelApi.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using PanelClientModel = EagleTunnelApi.Application.Panel.PanelClient;

namespace EagleTunnelApi.Tests.Infrastructure.Persistence;

[Collection(PostgresCollection.Name)]
public class PanelMirrorWriterTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresContainerFixture _fixture;

    public PanelMirrorWriterTests(PostgresContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task UpsertAsync_creates_mirror_inbounds_and_unverified_app_user()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(_fixture.ConnectionString);
        using var provider = await CreateProviderAsync(database);
        var writer = provider.GetRequiredService<IPanelMirrorWriter>();

        var details = CreateDetails(" User@Example.com ", panelClientId: 101, telegramId: 12345,
            inboundIds: [2, 1, 2], usedTraffic: 777);

        var result = await writer.UpsertAsync(details, null, CancellationToken.None);

        Assert.True(result.Applied);
        Assert.NotNull(result.PanelClientRowId);
        Assert.NotNull(result.AppUserId);
        Assert.Empty(result.Conflicts);

        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var mirrored = await context.PanelClients.AsNoTracking().SingleAsync();
        Assert.Equal(result.PanelClientRowId, mirrored.Id);
        Assert.Equal(101, mirrored.PanelClientId);
        Assert.Equal("user@example.com", mirrored.EmailNormalized);
        Assert.Equal(12345, mirrored.TelegramId);
        Assert.True(mirrored.Enable);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(details.Client.ExpiryTime), mirrored.ExpiryTime);
        Assert.Equal(details.Client.TotalGB, mirrored.TotalGb);
        Assert.Equal(2, mirrored.LimitIp);
        Assert.Equal(3, mirrored.LimitHwid);
        Assert.Equal("monthly", mirrored.TrafficReset);
        Assert.Equal(4, mirrored.TrafficResetDay);
        Assert.Equal(1, mirrored.Reset);
        Assert.Equal("reality", mirrored.Security);
        Assert.Equal("sub-101", mirrored.SubId);
        Assert.Equal("xtls-rprx-vision", mirrored.Flow);
        Assert.Equal("comment-101", mirrored.LegacyComment);
        Assert.Equal(Now.AddDays(-10), mirrored.PanelCreatedAt);
        Assert.Equal(Now.AddDays(-1), mirrored.PanelUpdatedAt);
        Assert.Equal(Now, mirrored.LastSyncedAt);
        Assert.False(mirrored.IsDeleted);
        Assert.Null(mirrored.DeletedAt);
        Assert.Equal(0, mirrored.TrafficUp);
        Assert.Equal(777, mirrored.TrafficDown);
        Assert.Null(mirrored.TrafficEnabled);

        var inboundIds = await context.PanelClientInbounds.AsNoTracking()
            .OrderBy(inbound => inbound.InboundId)
            .Select(inbound => inbound.InboundId)
            .ToListAsync();
        Assert.Equal([1, 2], inboundIds);

        var inbounds = await context.PanelInbounds.AsNoTracking()
            .OrderBy(inbound => inbound.InboundId)
            .ToListAsync();
        Assert.Equal([1, 2], inbounds.Select(inbound => inbound.InboundId));
        Assert.All(inbounds, inbound => Assert.False(inbound.IsDefaultForNewClients));
        Assert.All(inbounds, inbound => Assert.Equal(Now, inbound.LastSyncedAt));

        var appUser = await context.AppUsers.AsNoTracking().SingleAsync();
        Assert.Equal(result.AppUserId, appUser.Id);
        Assert.Equal("user@example.com", appUser.EmailNormalized);
        Assert.Equal(12345, appUser.TelegramId);
        Assert.Equal(101, appUser.PanelClientId);
        Assert.Null(appUser.EmailVerifiedAt);
        Assert.Equal(Now, appUser.CreatedAt);
        Assert.Equal(Now, appUser.UpdatedAt);
    }

    [Fact]
    public async Task UpsertAsync_updates_existing_client_inbounds_and_traffic()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(_fixture.ConnectionString);
        using var provider = await CreateProviderAsync(database);
        var writer = provider.GetRequiredService<IPanelMirrorWriter>();

        var first = await writer.UpsertAsync(CreateDetails("user@example.com", 101, 12345, [1, 2]),
            new PanelTrafficSummary(10, 20, true), CancellationToken.None);

        using (var verifyScope = provider.CreateScope())
        {
            var context = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await context.AppUsers.SingleAsync();
            user.EmailVerifiedAt = Now.AddDays(-1);
            await context.SaveChangesAsync();
        }

        var second = await writer.UpsertAsync(CreateDetails("new@example.com", 101, 12345, [2, 3],
            expiryDays: 5, enable: false, comment: "updated", usedTraffic: 999),
            new PanelTrafficSummary(30, 40, true), CancellationToken.None);

        Assert.True(second.Applied);
        Assert.Equal(first.PanelClientRowId, second.PanelClientRowId);
        Assert.Equal(first.AppUserId, second.AppUserId);

        using var scope = provider.CreateScope();
        var readContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var mirrored = await readContext.PanelClients.AsNoTracking().SingleAsync();

        Assert.Equal("new@example.com", mirrored.EmailNormalized);
        Assert.False(mirrored.Enable);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(
            CreateDetails("new@example.com", 101, 12345, expiryDays: 5).Client.ExpiryTime), mirrored.ExpiryTime);
        Assert.Equal("updated", mirrored.LegacyComment);
        Assert.Equal(30, mirrored.TrafficUp);
        Assert.Equal(40, mirrored.TrafficDown);
        Assert.True(mirrored.TrafficEnabled);

        var inboundIds = await readContext.PanelClientInbounds.AsNoTracking()
            .OrderBy(inbound => inbound.InboundId)
            .Select(inbound => inbound.InboundId)
            .ToListAsync();
        Assert.Equal([2, 3], inboundIds);

        var appUser = await readContext.AppUsers.AsNoTracking().SingleAsync();
        Assert.Equal("new@example.com", appUser.EmailNormalized);
        Assert.Null(appUser.EmailVerifiedAt);
    }

    [Fact]
    public async Task UpsertAsync_preserves_verification_when_email_does_not_change()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(_fixture.ConnectionString);
        using var provider = await CreateProviderAsync(database);
        var writer = provider.GetRequiredService<IPanelMirrorWriter>();

        var details = CreateDetails("user@example.com", 101, 12345, [1]);
        await writer.UpsertAsync(details, null, CancellationToken.None);

        using (var verifyScope = provider.CreateScope())
        {
            var context = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await context.AppUsers.SingleAsync();
            user.EmailVerifiedAt = Now.AddDays(-1);
            await context.SaveChangesAsync();
        }

        await writer.UpsertAsync(details with { Client = details.Client with { Enable = false } }, null,
            CancellationToken.None);

        using var scope = provider.CreateScope();
        var readContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var appUser = await readContext.AppUsers.AsNoTracking().SingleAsync();

        Assert.Equal(Now.AddDays(-1), appUser.EmailVerifiedAt);
    }

    [Fact]
    public async Task UpsertAsync_quarantines_a_new_client_with_a_duplicate_email()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(_fixture.ConnectionString);
        using var provider = await CreateProviderAsync(database);
        var writer = provider.GetRequiredService<IPanelMirrorWriter>();

        await writer.UpsertAsync(CreateDetails("shared@example.com", 101, 111, [1]), null,
            CancellationToken.None);

        var result = await writer.UpsertAsync(CreateDetails("SHARED@example.com", 202, 222, [2]), null,
            CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Null(result.PanelClientRowId);
        Assert.Null(result.AppUserId);
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(PanelConflictTypes.Email, conflict.Type);
        Assert.True(conflict.IsNew);

        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.Equal(1, await context.PanelClients.CountAsync());
        Assert.Equal(1, await context.AppUsers.CountAsync());
        Assert.Equal(1, await context.PanelSyncConflicts.CountAsync());

        var stored = await context.PanelSyncConflicts.AsNoTracking().SingleAsync();
        Assert.Equal("email:shared@example.com", stored.DedupeKey);
        Assert.Equal(PanelSyncConflictStatus.Open, stored.Status);
        Assert.Equal(202, stored.PanelClientId);
        Assert.Equal(101, stored.OtherPanelClientId);
        Assert.NotNull(stored.AppUserId);
        Assert.Null(stored.NotifiedAt);
        Assert.Equal(Now, stored.FirstSeenAt);
        Assert.Equal(Now, stored.LastSeenAt);
        Assert.Equal("shared@example.com",
            stored.Details!.RootElement.GetProperty("attemptedEmail").GetString());
        Assert.Equal(101,
            stored.Details.RootElement.GetProperty("existingPanelClientId").GetInt64());

        var second = await writer.UpsertAsync(CreateDetails("shared@example.com", 202, 222, [2]), null,
            CancellationToken.None);

        Assert.False(second.Applied);
        var repeated = Assert.Single(second.Conflicts);
        Assert.False(repeated.IsNew);
        Assert.Equal(conflict.Id, repeated.Id);
        Assert.Equal(1, await context.PanelSyncConflicts.CountAsync());
    }

    [Fact]
    public async Task UpsertAsync_quarantines_a_new_client_with_a_duplicate_telegram_id()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(_fixture.ConnectionString);
        using var provider = await CreateProviderAsync(database);
        var writer = provider.GetRequiredService<IPanelMirrorWriter>();

        await writer.UpsertAsync(CreateDetails("first@example.com", 101, 12345, [1]), null,
            CancellationToken.None);

        var result = await writer.UpsertAsync(CreateDetails("second@example.com", 202, 12345, [2]), null,
            CancellationToken.None);

        Assert.False(result.Applied);
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(PanelConflictTypes.Telegram, conflict.Type);
        Assert.Equal("tg:12345", conflict.DedupeKey);

        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await context.PanelClients.CountAsync());
        Assert.Equal(1, await context.AppUsers.CountAsync());

        var stored = await context.PanelSyncConflicts.AsNoTracking().SingleAsync();
        Assert.Equal(PanelConflictTypes.Telegram, stored.Type);
        Assert.Equal(202, stored.PanelClientId);
        Assert.Equal(101, stored.OtherPanelClientId);
        Assert.Equal(12345, stored.TelegramId);
    }

    [Fact]
    public async Task UpsertAsync_keeps_last_known_email_and_updates_panel_fields_on_conflict()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(_fixture.ConnectionString);
        using var provider = await CreateProviderAsync(database);
        var writer = provider.GetRequiredService<IPanelMirrorWriter>();

        await writer.UpsertAsync(CreateDetails("first@example.com", 101, 111, [1]), null,
            CancellationToken.None);
        await writer.UpsertAsync(CreateDetails("second@example.com", 202, 222, [2]), null,
            CancellationToken.None);

        using (var verifyScope = provider.CreateScope())
        {
            var context = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await context.AppUsers.SingleAsync(candidate => candidate.PanelClientId == 101);
            user.EmailVerifiedAt = Now.AddDays(-1);
            await context.SaveChangesAsync();
        }

        var result = await writer.UpsertAsync(CreateDetails("second@example.com", 101, 111, [1],
            expiryDays: 7, enable: false, comment: "changed"), null, CancellationToken.None);

        Assert.True(result.Applied);
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(PanelConflictTypes.Email, conflict.Type);

        using var scope = provider.CreateScope();
        var readContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var mirrored = await readContext.PanelClients.AsNoTracking()
            .SingleAsync(candidate => candidate.PanelClientId == 101);
        var appUser = await readContext.AppUsers.AsNoTracking()
            .SingleAsync(candidate => candidate.PanelClientId == 101);

        Assert.Equal("first@example.com", mirrored.EmailNormalized);
        Assert.Equal("first@example.com", appUser.EmailNormalized);
        Assert.False(mirrored.Enable);
        Assert.Equal("changed", mirrored.LegacyComment);
        Assert.Equal(Now.AddDays(-1), appUser.EmailVerifiedAt);
    }

    [Fact]
    public async Task UpsertAsync_keeps_last_known_telegram_id_on_conflict()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(_fixture.ConnectionString);
        using var provider = await CreateProviderAsync(database);
        var writer = provider.GetRequiredService<IPanelMirrorWriter>();

        await writer.UpsertAsync(CreateDetails("first@example.com", 101, 111, [1]), null,
            CancellationToken.None);
        await writer.UpsertAsync(CreateDetails("second@example.com", 202, 222, [2]), null,
            CancellationToken.None);

        var result = await writer.UpsertAsync(CreateDetails("first@example.com", 101, 222, [1]), null,
            CancellationToken.None);

        Assert.True(result.Applied);
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(PanelConflictTypes.Telegram, conflict.Type);

        using var scope = provider.CreateScope();
        var readContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var mirrored = await readContext.PanelClients.AsNoTracking()
            .SingleAsync(candidate => candidate.PanelClientId == 101);
        var appUser = await readContext.AppUsers.AsNoTracking()
            .SingleAsync(candidate => candidate.PanelClientId == 101);

        Assert.Equal(111, mirrored.TelegramId);
        Assert.Equal(111, appUser.TelegramId);
    }

    [Fact]
    public async Task UpsertAsync_reopens_a_resolved_conflict()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(_fixture.ConnectionString);
        using var provider = await CreateProviderAsync(database);
        var writer = provider.GetRequiredService<IPanelMirrorWriter>();

        await writer.UpsertAsync(CreateDetails("shared@example.com", 101, 111, [1]), null,
            CancellationToken.None);
        await writer.UpsertAsync(CreateDetails("shared@example.com", 202, 222, [2]), null,
            CancellationToken.None);

        using (var resolveScope = provider.CreateScope())
        {
            var context = resolveScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var storedConflict = await context.PanelSyncConflicts.SingleAsync();
            storedConflict.Status = PanelSyncConflictStatus.Resolved;
            storedConflict.ResolvedAt = Now;
            storedConflict.ResolvedBy = "telegram:1";
            storedConflict.NotifiedAt = Now;
            await context.SaveChangesAsync();
        }

        var result = await writer.UpsertAsync(CreateDetails("shared@example.com", 202, 222, [2]), null,
            CancellationToken.None);

        Assert.False(result.Applied);
        var reopened = Assert.Single(result.Conflicts);
        Assert.True(reopened.Reopened);

        using var scope = provider.CreateScope();
        var readContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conflict = await readContext.PanelSyncConflicts.AsNoTracking().SingleAsync();
        Assert.Equal(PanelSyncConflictStatus.Open, conflict.Status);
        Assert.Null(conflict.ResolvedAt);
        Assert.Null(conflict.ResolvedBy);
        Assert.Null(conflict.NotifiedAt);
    }

    [Fact]
    public async Task UpsertAsync_creates_a_missing_app_user_for_an_existing_mirror_row()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(_fixture.ConnectionString);
        using var provider = await CreateProviderAsync(database);
        var writer = provider.GetRequiredService<IPanelMirrorWriter>();

        var details = CreateDetails("user@example.com", 101, 12345, [1]);
        await writer.UpsertAsync(details, null, CancellationToken.None);

        using (var deleteScope = provider.CreateScope())
        {
            var context = deleteScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var removedUser = await context.AppUsers.SingleAsync();
            context.AppUsers.Remove(removedUser);
            await context.SaveChangesAsync();
        }

        var result = await writer.UpsertAsync(details, null, CancellationToken.None);

        Assert.True(result.Applied);
        Assert.NotNull(result.AppUserId);

        using var scope = provider.CreateScope();
        var readContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var appUser = await readContext.AppUsers.AsNoTracking().SingleAsync();
        Assert.Equal(101, appUser.PanelClientId);
        Assert.Equal("user@example.com", appUser.EmailNormalized);
        Assert.Null(appUser.EmailVerifiedAt);
    }

    [Fact]
    public async Task UpsertAsync_quarantines_a_client_without_an_email()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(_fixture.ConnectionString);
        using var provider = await CreateProviderAsync(database);
        var writer = provider.GetRequiredService<IPanelMirrorWriter>();

        var result = await writer.UpsertAsync(CreateDetails(" ", 101, 12345, [1]), null,
            CancellationToken.None);

        Assert.False(result.Applied);
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(PanelConflictTypes.InvalidEmail, conflict.Type);
        Assert.Equal("panel:101:invalid_email", conflict.DedupeKey);

        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await context.PanelClients.CountAsync());
        Assert.Equal(0, await context.AppUsers.CountAsync());

        var stored = await context.PanelSyncConflicts.AsNoTracking().SingleAsync();
        Assert.Null(stored.EmailNormalized);
        Assert.Equal(101, stored.PanelClientId);
    }

    private async Task<ServiceProvider> CreateProviderAsync(PostgresTestDatabase database)
    {
        var provider = PersistenceTestProvider.Build(database.ConnectionString,
            timeProvider: new FixedTimeProvider(Now));
        await provider.GetRequiredService<DatabaseMigrationRunner>().MigrateAsync();
        return provider;
    }

    private static PanelClientDetails CreateDetails(string email, long panelClientId,
        long telegramId = 0, int[]? inboundIds = null, int expiryDays = 30, bool enable = true,
        string? comment = null, long usedTraffic = 0)
    {
        inboundIds ??= [1];

        var client = new PanelClientModel(
            $"uuid-{panelClientId}",
            email,
            enable,
            Now.AddDays(expiryDays).ToUnixTimeMilliseconds(),
            telegramId,
            100L * 1024 * 1024 * 1024,
            comment ?? $"comment-{panelClientId}",
            2,
            3,
            "monthly",
            4,
            1,
            "reality",
            $"sub-{panelClientId}",
            "xtls-rprx-vision",
            panelClientId,
            inboundIds);

        return new PanelClientDetails(client, null, inboundIds, usedTraffic)
        {
            CreatedAt = Now.AddDays(-10),
            UpdatedAt = Now.AddDays(-1)
        };
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
