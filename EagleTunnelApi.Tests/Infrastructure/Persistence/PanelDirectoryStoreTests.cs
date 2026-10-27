using System.Text.Json;
using EagleTunnelApi.Application.Panel;
using EagleTunnelApi.Infrastructure.Persistence;
using EagleTunnelApi.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PanelClientEntity = EagleTunnelApi.Infrastructure.Persistence.Entities.PanelClient;
using Xunit;

namespace EagleTunnelApi.Tests.Infrastructure.Persistence;

[Collection(PostgresCollection.Name)]
public class PanelDirectoryStoreTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresContainerFixture _fixture;

    public PanelDirectoryStoreTests(PostgresContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetDefaultInboundIdsAsync_seeds_fallback_once_and_audits()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(_fixture.ConnectionString);
        using var provider = await CreateProviderAsync(database,
            new PanelDirectoryFallback([2, 5]));

        var directory = provider.GetRequiredService<IPanelDirectory>();

        Assert.Equal([2, 5], await directory.GetDefaultInboundIdsAsync(CancellationToken.None));
        Assert.Equal([2, 5], await directory.GetDefaultInboundIdsAsync(CancellationToken.None));

        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var inbounds = await context.PanelInbounds
            .AsNoTracking()
            .OrderBy(inbound => inbound.InboundId)
            .ToListAsync();

        Assert.Equal(2, inbounds.Count);
        Assert.All(inbounds, inbound => Assert.True(inbound.IsDefaultForNewClients));
        Assert.Equal([0, 1], inbounds.Select(inbound => inbound.SortOrder));
        Assert.All(inbounds, inbound => Assert.Equal(Now, inbound.LastSyncedAt));

        var auditEvent = await context.AuditEvents.AsNoTracking().SingleAsync();
        Assert.Equal("panel.default_inbounds.seeded", auditEvent.EventType);
        Assert.Equal("system", auditEvent.ActorType);
        Assert.Equal(Now, auditEvent.OccurredAt);
        Assert.Equal("configuration",
            auditEvent.Metadata!.RootElement.GetProperty("source").GetString());
        Assert.Equal([2, 5], auditEvent.Metadata.RootElement.GetProperty("seededInboundIds")
            .EnumerateArray().Select(value => value.GetInt32()));
    }

    [Fact]
    public async Task GetDefaultInboundIdsAsync_prefers_database_defaults_over_fallback()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(_fixture.ConnectionString);
        using var provider = await CreateProviderAsync(database,
            new PanelDirectoryFallback([2, 5]));

        using (var seedScope = provider.CreateScope())
        {
            var context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.PanelInbounds.Add(new PanelInbound
            {
                InboundId = 9,
                IsDefaultForNewClients = true,
                SortOrder = 0,
                LastSyncedAt = Now
            });
            await context.SaveChangesAsync();
        }

        var directory = provider.GetRequiredService<IPanelDirectory>();

        Assert.Equal([9], await directory.GetDefaultInboundIdsAsync(CancellationToken.None));

        using var scope = provider.CreateScope();
        var readContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await readContext.PanelInbounds.CountAsync());
        Assert.Equal(0, await readContext.AuditEvents.CountAsync());
    }

    [Fact]
    public async Task GetDefaultInboundIdsAsync_returns_empty_without_defaults_or_fallback()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(_fixture.ConnectionString);
        using var provider = await CreateProviderAsync(database);

        var directory = provider.GetRequiredService<IPanelDirectory>();

        Assert.Empty(await directory.GetDefaultInboundIdsAsync(CancellationToken.None));

        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await context.PanelInbounds.CountAsync());
        Assert.Equal(0, await context.AuditEvents.CountAsync());
    }

    [Fact]
    public async Task SetDefaultInboundsAsync_replaces_defaults_upserts_options_and_audits()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(_fixture.ConnectionString);
        using var provider = await CreateProviderAsync(database);

        using (var seedScope = provider.CreateScope())
        {
            var seedContext = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            seedContext.PanelInbounds.AddRange(
                new PanelInbound
                {
                    InboundId = 1,
                    IsDefaultForNewClients = true,
                    SortOrder = 0,
                    LastSyncedAt = Now.AddDays(-1)
                },
                new PanelInbound
                {
                    InboundId = 2,
                    IsDefaultForNewClients = true,
                    SortOrder = 1,
                    LastSyncedAt = Now.AddDays(-1)
                },
                new PanelInbound
                {
                    InboundId = 3,
                    IsDefaultForNewClients = false,
                    SortOrder = 0,
                    LastSyncedAt = Now.AddDays(-1)
                });
            await seedContext.SaveChangesAsync();
        }

        var directory = provider.GetRequiredService<IPanelDirectory>();

        await directory.SetDefaultInboundsAsync([3, 2], CreateOptions(), "telegram:42",
            CancellationToken.None);

        Assert.Equal([3, 2], await directory.GetDefaultInboundIdsAsync(CancellationToken.None));

        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var inbounds = await context.PanelInbounds
            .AsNoTracking()
            .OrderBy(inbound => inbound.InboundId)
            .ToListAsync();

        Assert.False(inbounds[0].IsDefaultForNewClients);
        Assert.True(inbounds[1].IsDefaultForNewClients);
        Assert.Equal(1, inbounds[1].SortOrder);
        Assert.True(inbounds[2].IsDefaultForNewClients);
        Assert.Equal(0, inbounds[2].SortOrder);
        Assert.All(inbounds, inbound => Assert.Equal(Now, inbound.LastSyncedAt));
        Assert.All(inbounds, inbound => Assert.Equal($"Inbound {inbound.InboundId}", inbound.Remark));

        var auditEvent = await context.AuditEvents.AsNoTracking().SingleAsync();
        Assert.Equal("panel.default_inbounds.changed", auditEvent.EventType);
        Assert.Equal("admin", auditEvent.ActorType);
        Assert.Equal(Now, auditEvent.OccurredAt);
        Assert.Equal("telegram:42",
            auditEvent.Metadata!.RootElement.GetProperty("actor").GetString());
        Assert.Equal([1, 2], auditEvent.Metadata.RootElement
            .GetProperty("oldDefaultInboundIds").EnumerateArray().Select(value => value.GetInt32()));
        Assert.Equal([3, 2], auditEvent.Metadata.RootElement
            .GetProperty("newDefaultInboundIds").EnumerateArray().Select(value => value.GetInt32()));
        Assert.Equal([1, 2, 3], auditEvent.Metadata.RootElement
            .GetProperty("availableInboundIds").EnumerateArray().Select(value => value.GetInt32()));
    }

    [Fact]
    public async Task SetDefaultInboundsAsync_rejects_an_empty_set_without_writing()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(_fixture.ConnectionString);
        using var provider = await CreateProviderAsync(database);
        var directory = provider.GetRequiredService<IPanelDirectory>();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            directory.SetDefaultInboundsAsync([], CreateOptions(), "telegram:42",
                CancellationToken.None));

        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await context.PanelInbounds.CountAsync());
        Assert.Equal(0, await context.AuditEvents.CountAsync());
    }

    [Fact]
    public async Task SetDefaultInboundsAsync_rejects_an_unknown_selected_inbound()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(_fixture.ConnectionString);
        using var provider = await CreateProviderAsync(database);
        var directory = provider.GetRequiredService<IPanelDirectory>();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            directory.SetDefaultInboundsAsync([99], CreateOptions(1, 2), "telegram:42",
                CancellationToken.None));

        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await context.PanelInbounds.CountAsync());
        Assert.Equal(0, await context.AuditEvents.CountAsync());
    }

    [Fact]
    public async Task SetDefaultInboundsAsync_keeps_a_selected_stale_default()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(_fixture.ConnectionString);
        using var provider = await CreateProviderAsync(database);

        using (var seedScope = provider.CreateScope())
        {
            var seedContext = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            seedContext.PanelInbounds.Add(new PanelInbound
            {
                InboundId = 9,
                Remark = "Retired inbound",
                IsDefaultForNewClients = true,
                SortOrder = 0,
                LastSyncedAt = Now.AddDays(-2)
            });
            await seedContext.SaveChangesAsync();
        }

        var directory = provider.GetRequiredService<IPanelDirectory>();

        await directory.SetDefaultInboundsAsync([9, 1], CreateOptions(1, 2), "telegram:42",
            CancellationToken.None);

        Assert.Equal([9, 1], await directory.GetDefaultInboundIdsAsync(CancellationToken.None));

        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stale = await context.PanelInbounds.AsNoTracking()
            .SingleAsync(inbound => inbound.InboundId == 9);

        Assert.True(stale.IsDefaultForNewClients);
        Assert.Equal(0, stale.SortOrder);
        Assert.Equal("Retired inbound", stale.Remark);
        Assert.Equal(Now.AddDays(-2), stale.LastSyncedAt);
    }

    [Fact]
    public async Task GetClientByEmailAsync_reads_mirror_inbounds_and_ignores_deleted_clients()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(_fixture.ConnectionString);
        using var provider = await CreateProviderAsync(database);

        var clientId = Guid.NewGuid();

        using (var seedScope = provider.CreateScope())
        {
            var context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.PanelInbounds.AddRange(
                new PanelInbound { InboundId = 1, LastSyncedAt = Now },
                new PanelInbound { InboundId = 2, LastSyncedAt = Now });
            context.PanelClients.AddRange(
                new PanelClientEntity
                {
                    Id = clientId,
                    PanelClientId = 101,
                    EmailNormalized = "user@example.com",
                    TelegramId = 12345,
                    Enable = true,
                    ExpiryTime = Now.AddDays(30),
                    TotalGb = 50L * 1024 * 1024 * 1024,
                    LimitIp = 2,
                    LimitHwid = 3,
                    TrafficReset = "monthly",
                    TrafficResetDay = 4,
                    Reset = 1,
                    SubId = "sub-101",
                    Flow = "xtls-rprx-vision",
                    Security = "reality",
                    TrafficUp = 123,
                    TrafficDown = 456,
                    TrafficEnabled = true,
                    LegacyComment = "legacy",
                    PanelCreatedAt = Now.AddDays(-10),
                    PanelUpdatedAt = Now.AddDays(-1),
                    LastSyncedAt = Now,
                    IsDeleted = false
                },
                new PanelClientEntity
                {
                    Id = Guid.NewGuid(),
                    PanelClientId = 102,
                    EmailNormalized = "deleted@example.com",
                    Enable = true,
                    LastSyncedAt = Now,
                    IsDeleted = true,
                    DeletedAt = Now
                });
            context.PanelClientInbounds.AddRange(
                new PanelClientInbound { PanelClientId = clientId, InboundId = 1 },
                new PanelClientInbound { PanelClientId = clientId, InboundId = 2 });
            await context.SaveChangesAsync();
        }

        var directory = provider.GetRequiredService<IPanelDirectory>();

        var client = await directory.GetClientByEmailAsync(" USER@EXAMPLE.COM ", CancellationToken.None);

        Assert.NotNull(client);
        Assert.Equal(clientId, client.Id);
        Assert.Equal(101, client.PanelClientId);
        Assert.Equal("user@example.com", client.EmailNormalized);
        Assert.Equal(12345, client.TelegramId);
        Assert.True(client.Enable);
        Assert.Equal(Now.AddDays(30), client.ExpiryTime);
        Assert.Equal(50L * 1024 * 1024 * 1024, client.TotalGb);
        Assert.Equal(2, client.LimitIp);
        Assert.Equal(3, client.LimitHwid);
        Assert.Equal("monthly", client.TrafficReset);
        Assert.Equal(4, client.TrafficResetDay);
        Assert.Equal(1, client.Reset);
        Assert.Equal("sub-101", client.SubId);
        Assert.Equal("xtls-rprx-vision", client.Flow);
        Assert.Equal("reality", client.Security);
        Assert.Equal(123, client.TrafficUp);
        Assert.Equal(456, client.TrafficDown);
        Assert.True(client.TrafficEnabled);
        Assert.Equal("legacy", client.LegacyComment);
        Assert.Equal(Now, client.LastSyncedAt);
        Assert.Equal([1, 2], client.InboundIds);

        Assert.Null(await directory.GetClientByEmailAsync("deleted@example.com", CancellationToken.None));
        Assert.Null(await directory.GetClientByEmailAsync("missing@example.com", CancellationToken.None));
        Assert.Null(await directory.GetClientByEmailAsync(" ", CancellationToken.None));
    }

    [Fact]
    public async Task GetSyncStatusAsync_returns_empty_until_a_state_row_exists()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(_fixture.ConnectionString);
        using var provider = await CreateProviderAsync(database);
        var directory = provider.GetRequiredService<IPanelDirectory>();

        var empty = await directory.GetSyncStatusAsync(CancellationToken.None);
        Assert.Null(empty.LastRunMode);
        Assert.Null(empty.LastSuccessAt);
        Assert.Equal(0, empty.LastSeenCount);

        using (var seedScope = provider.CreateScope())
        {
            var context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.PanelSyncStates.Add(new PanelSyncState
            {
                Id = PanelSyncStateIds.Default,
                LastRunMode = PanelSyncRunModes.Cursor,
                LastStartedAt = Now.AddMinutes(-2),
                LastCompletedAt = Now.AddMinutes(-1),
                LastSuccessAt = Now.AddMinutes(-1),
                LastError = "previous timeout",
                LastSeenCount = 308,
                LastChangedCount = 4,
                CursorPanelUpdatedAt = Now.AddMinutes(-10),
                LockOwner = "instance-a",
                LockExpiresAt = Now.AddMinutes(5)
            });
            await context.SaveChangesAsync();
        }

        var status = await directory.GetSyncStatusAsync(CancellationToken.None);

        Assert.Equal(PanelSyncRunModes.Cursor, status.LastRunMode);
        Assert.Equal(Now.AddMinutes(-1), status.LastSuccessAt);
        Assert.Equal("previous timeout", status.LastError);
        Assert.Equal(308, status.LastSeenCount);
        Assert.Equal(4, status.LastChangedCount);
        Assert.Equal("instance-a", status.LockOwner);
    }

    [Fact]
    public async Task GetSyncConflictsAsync_filters_orders_and_respects_the_limit()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(_fixture.ConnectionString);
        using var provider = await CreateProviderAsync(database);

        var oldest = Guid.NewGuid();
        var newest = Guid.NewGuid();
        var middle = Guid.NewGuid();
        var resolved = Guid.NewGuid();

        using (var seedScope = provider.CreateScope())
        {
            var context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.PanelSyncConflicts.AddRange(
                CreateConflict(oldest, "email:old@example.com", PanelSyncConflictStatus.Open,
                    Now.AddHours(-3)),
                CreateConflict(newest, "email:new@example.com", PanelSyncConflictStatus.Open,
                    Now.AddHours(-1)),
                CreateConflict(middle, "email:middle@example.com", PanelSyncConflictStatus.Open,
                    Now.AddHours(-2)),
                CreateConflict(resolved, "email:resolved@example.com",
                    PanelSyncConflictStatus.Resolved, Now.AddHours(-4)));
            await context.SaveChangesAsync();
        }

        var directory = provider.GetRequiredService<IPanelDirectory>();

        var open = await directory.GetSyncConflictsAsync(null, 10, CancellationToken.None);

        Assert.Equal([newest, middle, oldest], open.Select(conflict => conflict.Id));
        Assert.All(open, conflict => Assert.Equal(PanelSyncConflictStates.Open, conflict.Status));
        Assert.Equal("email:new@example.com", open[0].DedupeKey);
        Assert.NotNull(open[0].DetailsJson);

        var limited = await directory.GetSyncConflictsAsync(null, 1, CancellationToken.None);
        Assert.Equal([newest], limited.Select(conflict => conflict.Id));

        var resolvedConflicts = await directory.GetSyncConflictsAsync(
            PanelSyncConflictStates.Resolved, 10, CancellationToken.None);
        Assert.Equal([resolved], resolvedConflicts.Select(conflict => conflict.Id));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            directory.GetSyncConflictsAsync("unknown", 10, CancellationToken.None));
    }

    [Fact]
    public async Task ResolveSyncConflictAsync_marks_the_conflict_and_audits_once()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(_fixture.ConnectionString);
        using var provider = await CreateProviderAsync(database);

        var conflictId = Guid.NewGuid();

        using (var seedScope = provider.CreateScope())
        {
            var context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.PanelSyncConflicts.Add(
                CreateConflict(conflictId, "tg:12345", PanelSyncConflictStatus.Open, Now));
            await context.SaveChangesAsync();
        }

        var directory = provider.GetRequiredService<IPanelDirectory>();

        await directory.ResolveSyncConflictAsync(conflictId, PanelSyncConflictStates.Resolved,
            "telegram:42", CancellationToken.None);
        await directory.ResolveSyncConflictAsync(conflictId, PanelSyncConflictStates.Resolved,
            "telegram:99", CancellationToken.None);

        using var scope = provider.CreateScope();
        var readContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conflict = await readContext.PanelSyncConflicts.AsNoTracking().SingleAsync();

        Assert.Equal(PanelSyncConflictStates.Resolved, conflict.Status);
        Assert.Equal(Now, conflict.ResolvedAt);
        Assert.Equal("telegram:42", conflict.ResolvedBy);

        var auditEvent = await readContext.AuditEvents.AsNoTracking().SingleAsync();
        Assert.Equal("panel.conflict.resolved", auditEvent.EventType);
        Assert.Equal("admin", auditEvent.ActorType);
        Assert.Equal("user@example.com", auditEvent.EmailNormalized);
        Assert.Equal(12345, auditEvent.TelegramId);
        Assert.Equal("telegram:42",
            auditEvent.Metadata!.RootElement.GetProperty("actor").GetString());
        Assert.Equal(PanelSyncConflictStatus.Open,
            auditEvent.Metadata.RootElement.GetProperty("previousStatus").GetString());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            directory.ResolveSyncConflictAsync(conflictId, PanelSyncConflictStates.Open,
                "telegram:42", CancellationToken.None));
    }

    private async Task<ServiceProvider> CreateProviderAsync(PostgresTestDatabase database,
        PanelDirectoryFallback? fallback = null)
    {
        var provider = PersistenceTestProvider.Build(database.ConnectionString,
            fallback ?? PanelDirectoryFallback.Empty, new FixedTimeProvider(Now));
        await provider.GetRequiredService<DatabaseMigrationRunner>().MigrateAsync();
        return provider;
    }

    private static IReadOnlyList<PanelInboundOption> CreateOptions(params int[] inboundIds)
    {
        if (inboundIds.Length == 0) inboundIds = [1, 2, 3];

        return inboundIds
            .Select(id => new PanelInboundOption(id, $"Inbound {id}", $"tag-{id}", "vless", 443 + id, true))
            .ToArray();
    }

    private static PanelSyncConflict CreateConflict(Guid id, string dedupeKey, string status,
        DateTimeOffset lastSeenAt)
    {
        return new PanelSyncConflict
        {
            Id = id,
            DedupeKey = dedupeKey,
            Type = "email",
            EmailNormalized = "user@example.com",
            TelegramId = 12345,
            PanelClientId = 101,
            OtherPanelClientId = 102,
            Details = JsonDocument.Parse("""{"reason":"duplicate"}"""),
            Status = status,
            FirstSeenAt = lastSeenAt.AddDays(-1),
            LastSeenAt = lastSeenAt,
            ResolvedAt = status == PanelSyncConflictStatus.Resolved ? lastSeenAt : null,
            ResolvedBy = status == PanelSyncConflictStatus.Resolved ? "telegram:1" : null
        };
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
