using EagleTunnelApi.Application.Panel;
using EagleTunnelApi.Infrastructure.Persistence;
using EagleTunnelApi.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using PanelClientModel = EagleTunnelApi.Application.Panel.PanelClient;

namespace EagleTunnelApi.Tests.Infrastructure.Persistence;

[Collection(PostgresCollection.Name)]
public class PanelSyncServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PanelCreatedAt = Now.AddDays(-10);
    private static readonly DateTimeOffset Expiry = Now.AddDays(30);

    private readonly PostgresContainerFixture _fixture;

    public PanelSyncServiceTests(PostgresContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RunAsync_first_cursor_run_upgrades_to_full_and_populates_the_mirror()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(_fixture.ConnectionString);
        var gateway = new FakePanelGateway();
        gateway.Rows.Add(new RowSpec("a@example.com", 1, 111, Now.AddMinutes(-20)));
        gateway.Rows.Add(new RowSpec("b@example.com", 2, 222, Now.AddMinutes(-10)));

        using var provider = await CreateProviderAsync(database, gateway);
        using var scope = provider.CreateScope();
        var sync = scope.ServiceProvider.GetRequiredService<IPanelSync>();

        var result = await sync.RunAsync(PanelSyncMode.Cursor, "test-owner", CancellationToken.None);

        Assert.False(result.Skipped);
        Assert.Equal(PanelSyncRunModes.Full, result.Mode);
        Assert.Equal(2, result.Seen);
        Assert.Equal(2, result.Changed);
        Assert.Equal(0, result.Deleted);
        Assert.Equal(0, result.Conflicts);
        Assert.Equal(0, result.Failures);
        Assert.Null(result.Error);
        Assert.Equal(2, gateway.DetailLookups.Count);
        Assert.All(gateway.Requests, request =>
            Assert.Equal(PanelClientSortFields.Email, request.Sort));

        using var readScope = provider.CreateScope();
        var context = readScope.ServiceProvider.GetRequiredService<AppDbContext>();

        var state = await context.PanelSyncStates.AsNoTracking().SingleAsync();
        Assert.Equal(PanelSyncRunModes.Full, state.LastRunMode);
        Assert.Equal(2, state.LastSeenCount);
        Assert.Equal(2, state.LastChangedCount);
        Assert.Equal(Now.AddMinutes(-10), state.CursorPanelUpdatedAt);
        Assert.Equal(Now, state.LastStartedAt);
        Assert.Equal(Now, state.LastSuccessAt);
        Assert.Equal(Now, state.LastCompletedAt);
        Assert.Null(state.LastError);
        Assert.Null(state.LockOwner);
        Assert.Null(state.LockExpiresAt);

        var clients = await context.PanelClients.AsNoTracking()
            .OrderBy(client => client.PanelClientId)
            .ToListAsync();
        Assert.Equal(2, clients.Count);
        Assert.All(clients, client =>
        {
            Assert.False(client.IsDeleted);
            Assert.Null(client.DeletedAt);
            Assert.Equal(Now, client.LastSyncedAt);
        });

        Assert.Equal(2, await context.AppUsers.CountAsync());
    }

    [Fact]
    public async Task RunAsync_cursor_fetches_only_changed_or_new_rows()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(_fixture.ConnectionString);
        var gateway = new FakePanelGateway();
        gateway.Rows.Add(new RowSpec("a@example.com", 1, 111, Now.AddMinutes(-20)));
        gateway.Rows.Add(new RowSpec("b@example.com", 2, 222, Now.AddMinutes(-10)));

        using var provider = await CreateProviderAsync(database, gateway);
        using var scope = provider.CreateScope();
        var sync = scope.ServiceProvider.GetRequiredService<IPanelSync>();

        await sync.RunAsync(PanelSyncMode.Cursor, "seed", CancellationToken.None);

        gateway.Requests.Clear();
        gateway.DetailLookups.Clear();
        gateway.Rows[1] = gateway.Rows[1] with
        {
            UpdatedAt = Now.AddMinutes(1),
            Comment = "changed"
        };
        gateway.Rows.Add(new RowSpec("c@example.com", 3, 333, Now.AddMinutes(2)));

        var result = await sync.RunAsync(PanelSyncMode.Cursor, "cursor", CancellationToken.None);

        Assert.Equal(PanelSyncRunModes.Cursor, result.Mode);
        Assert.Equal(3, result.Seen);
        Assert.Equal(2, result.Changed);
        Assert.Equal(0, result.Deleted);
        Assert.Equal(0, result.Conflicts);
        Assert.Equal(0, result.Failures);
        Assert.Equal(new[] { "b@example.com", "c@example.com" }, gateway.DetailLookups);
        Assert.All(gateway.Requests, request =>
            Assert.Equal(PanelClientSortFields.UpdatedAt, request.Sort));

        using var readScope = provider.CreateScope();
        var context = readScope.ServiceProvider.GetRequiredService<AppDbContext>();

        var state = await context.PanelSyncStates.AsNoTracking().SingleAsync();
        Assert.Equal(PanelSyncRunModes.Cursor, state.LastRunMode);
        Assert.Equal(3, state.LastSeenCount);
        Assert.Equal(2, state.LastChangedCount);
        Assert.Equal(Now.AddMinutes(2), state.CursorPanelUpdatedAt);
        Assert.Equal(Now, state.LastSuccessAt);

        var changed = await context.PanelClients.AsNoTracking()
            .SingleAsync(client => client.PanelClientId == 2);
        Assert.Equal("changed", changed.LegacyComment);

        Assert.Equal(1, await context.PanelClients.CountAsync(client =>
            client.PanelClientId == 3));
    }

    [Fact]
    public async Task RunAsync_falls_back_to_full_when_updatedAt_sort_is_rejected()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(_fixture.ConnectionString);
        var gateway = new FakePanelGateway();
        gateway.Rows.Add(new RowSpec("a@example.com", 1, 111, Now.AddMinutes(-20)));

        using var provider = await CreateProviderAsync(database, gateway);
        var writer = provider.GetRequiredService<IPanelMirrorWriter>();
        await writer.UpsertAsync(gateway.Rows[0].ToDetails(), null, CancellationToken.None);

        using (var seedScope = provider.CreateScope())
        {
            var context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.PanelSyncStates.Add(new PanelSyncState
            {
                Id = PanelSyncStateIds.Default,
                CursorPanelUpdatedAt = Now.AddMinutes(-30),
                LastRunMode = PanelSyncRunModes.Cursor,
                LastStartedAt = Now.AddMinutes(-30),
                LastCompletedAt = Now.AddMinutes(-30),
                LastSuccessAt = Now.AddMinutes(-30)
            });
            await context.SaveChangesAsync();
        }

        gateway.RejectUpdatedAtSort = true;

        using var scope = provider.CreateScope();
        var sync = scope.ServiceProvider.GetRequiredService<IPanelSync>();
        var result = await sync.RunAsync(PanelSyncMode.Cursor, "fallback", CancellationToken.None);

        Assert.Equal(PanelSyncRunModes.Full, result.Mode);
        Assert.Equal(1, result.Seen);
        Assert.Equal(1, result.Changed);
        Assert.Equal(0, result.Failures);
        Assert.Contains(gateway.Requests, request =>
            request.Sort == PanelClientSortFields.UpdatedAt);
        Assert.Contains(gateway.Requests, request =>
            request.Sort == PanelClientSortFields.Email);
        Assert.Equal(new[] { "a@example.com" }, gateway.DetailLookups);

        using var readScope = provider.CreateScope();
        var readContext = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var state = await readContext.PanelSyncStates.AsNoTracking().SingleAsync();
        Assert.Equal(PanelSyncRunModes.Full, state.LastRunMode);
        Assert.Equal(Now, state.LastSuccessAt);
        Assert.Equal(Now.AddMinutes(-20), state.CursorPanelUpdatedAt);
        Assert.Null(state.LockOwner);
    }

    [Fact]
    public async Task RunAsync_skips_when_another_owner_holds_the_lease()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(_fixture.ConnectionString);
        var gateway = new FakePanelGateway();
        gateway.Rows.Add(new RowSpec("a@example.com", 1, 111, Now.AddMinutes(-20)));

        using var provider = await CreateProviderAsync(database, gateway);

        using (var seedScope = provider.CreateScope())
        {
            var context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.PanelSyncStates.Add(new PanelSyncState
            {
                Id = PanelSyncStateIds.Default,
                LastRunMode = PanelSyncRunModes.Full,
                LockOwner = "other-owner",
                LockExpiresAt = Now.AddMinutes(5)
            });
            await context.SaveChangesAsync();
        }

        using var scope = provider.CreateScope();
        var sync = scope.ServiceProvider.GetRequiredService<IPanelSync>();
        var result = await sync.RunAsync(PanelSyncMode.Full, "test-owner", CancellationToken.None);

        Assert.True(result.Skipped);
        Assert.Equal(0, result.Seen);
        Assert.Contains("lease", result.Reason);
        Assert.Empty(gateway.Requests);

        using var readScope = provider.CreateScope();
        var readContext = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var state = await readContext.PanelSyncStates.AsNoTracking().SingleAsync();
        Assert.Equal("other-owner", state.LockOwner);
    }

    [Fact]
    public async Task RunAsync_full_marks_missing_clients_deleted_and_resolves_on_reappearance()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(_fixture.ConnectionString);
        var gateway = new FakePanelGateway();
        gateway.Rows.Add(new RowSpec("a@example.com", 1, 111, Now.AddMinutes(-20)));
        gateway.Rows.Add(new RowSpec("b@example.com", 2, 222, Now.AddMinutes(-10)));

        using var provider = await CreateProviderAsync(database, gateway);
        using var scope = provider.CreateScope();
        var sync = scope.ServiceProvider.GetRequiredService<IPanelSync>();

        await sync.RunAsync(PanelSyncMode.Full, "seed", CancellationToken.None);

        gateway.Rows.RemoveAll(row => row.Email == "b@example.com");

        var deleteResult = await sync.RunAsync(PanelSyncMode.Full, "delete",
            CancellationToken.None);

        Assert.Equal(1, deleteResult.Seen);
        Assert.Equal(0, deleteResult.Changed);
        Assert.Equal(1, deleteResult.Deleted);
        Assert.Equal(1, deleteResult.Conflicts);
        Assert.Equal(0, deleteResult.Failures);

        using (var readScope = provider.CreateScope())
        {
            var context = readScope.ServiceProvider.GetRequiredService<AppDbContext>();

            var deleted = await context.PanelClients.AsNoTracking()
                .SingleAsync(client => client.PanelClientId == 2);
            Assert.True(deleted.IsDeleted);
            Assert.Equal(Now, deleted.DeletedAt);

            var conflict = await context.PanelSyncConflicts.AsNoTracking().SingleAsync();
            Assert.Equal("panel:2:missing", conflict.DedupeKey);
            Assert.Equal(PanelConflictTypes.PanelClientMissing, conflict.Type);
            Assert.Equal(PanelSyncConflictStatus.Open, conflict.Status);
            Assert.Equal(2, conflict.PanelClientId);
            Assert.NotNull(conflict.AppUserId);

            Assert.Equal(1, await context.AppUsers.CountAsync(user =>
                user.PanelClientId == 2));

            var state = await context.PanelSyncStates.AsNoTracking().SingleAsync();
            Assert.Equal(Now.AddMinutes(-10), state.CursorPanelUpdatedAt);
        }

        gateway.Rows.Add(new RowSpec("b@example.com", 2, 222, Now.AddMinutes(5), "restored"));

        var restoreResult = await sync.RunAsync(PanelSyncMode.Full, "restore",
            CancellationToken.None);

        Assert.Equal(2, restoreResult.Seen);
        Assert.Equal(1, restoreResult.Changed);
        Assert.Equal(0, restoreResult.Deleted);
        Assert.Equal(0, restoreResult.Conflicts);

        using (var readScope = provider.CreateScope())
        {
            var context = readScope.ServiceProvider.GetRequiredService<AppDbContext>();

            var restored = await context.PanelClients.AsNoTracking()
                .SingleAsync(client => client.PanelClientId == 2);
            Assert.False(restored.IsDeleted);
            Assert.Null(restored.DeletedAt);
            Assert.Equal("restored", restored.LegacyComment);

            var conflict = await context.PanelSyncConflicts.AsNoTracking().SingleAsync();
            Assert.Equal(PanelSyncConflictStatus.Resolved, conflict.Status);
            Assert.Equal("panel-sync", conflict.ResolvedBy);
            Assert.Equal(Now, conflict.ResolvedAt);

            var state = await context.PanelSyncStates.AsNoTracking().SingleAsync();
            Assert.Equal(Now.AddMinutes(5), state.CursorPanelUpdatedAt);
        }
    }

    [Fact]
    public async Task RunAsync_full_uses_sub_id_when_an_email_moves_to_another_client()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(_fixture.ConnectionString);
        var gateway = new FakePanelGateway();
        gateway.Rows.Add(new RowSpec("a@example.com", 1, 111, Now.AddMinutes(-20)));
        gateway.Rows.Add(new RowSpec("b@example.com", 2, 222, Now.AddMinutes(-10)));

        using var provider = await CreateProviderAsync(database, gateway);
        using var scope = provider.CreateScope();
        var sync = scope.ServiceProvider.GetRequiredService<IPanelSync>();

        await sync.RunAsync(PanelSyncMode.Full, "seed", CancellationToken.None);

        gateway.Rows.RemoveAll(row => row.Email == "a@example.com");
        gateway.Rows[0] = gateway.Rows[0] with
        {
            Email = "a@example.com",
            UpdatedAt = Now.AddMinutes(1),
            Comment = "took-email"
        };

        var result = await sync.RunAsync(PanelSyncMode.Full, "reassign",
            CancellationToken.None);

        Assert.Equal(1, result.Seen);
        Assert.Equal(1, result.Changed);
        Assert.Equal(1, result.Deleted);
        Assert.Equal(2, result.Conflicts);

        using var readScope = provider.CreateScope();
        var context = readScope.ServiceProvider.GetRequiredService<AppDbContext>();

        var previousOwner = await context.PanelClients.AsNoTracking()
            .SingleAsync(client => client.PanelClientId == 1);
        Assert.True(previousOwner.IsDeleted);
        Assert.Equal(Now, previousOwner.DeletedAt);

        var client = await context.PanelClients.AsNoTracking()
            .SingleAsync(candidate => candidate.PanelClientId == 2);
        Assert.False(client.IsDeleted);
        Assert.Equal("b@example.com", client.EmailNormalized);
        Assert.Equal("sub-2", client.SubId);
        Assert.Equal("took-email", client.LegacyComment);

        var emailConflict = await context.PanelSyncConflicts.AsNoTracking()
            .SingleAsync(conflict => conflict.DedupeKey == "email:a@example.com");
        Assert.Equal(PanelConflictTypes.Email, emailConflict.Type);
        Assert.Equal(2, emailConflict.PanelClientId);
        Assert.Equal(1, emailConflict.OtherPanelClientId);
        Assert.Equal(PanelSyncConflictStatus.Open, emailConflict.Status);

        var missingConflict = await context.PanelSyncConflicts.AsNoTracking()
            .SingleAsync(conflict => conflict.DedupeKey == "panel:1:missing");
        Assert.Equal(PanelConflictTypes.PanelClientMissing, missingConflict.Type);
        Assert.Equal(PanelSyncConflictStatus.Open, missingConflict.Status);
    }

    [Fact]
    public async Task RunAsync_records_failure_and_releases_the_lease()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(_fixture.ConnectionString);
        var gateway = new FakePanelGateway
        {
            PageException = new PanelApiException("panel unavailable")
        };

        using var provider = await CreateProviderAsync(database, gateway);
        using var scope = provider.CreateScope();
        var sync = scope.ServiceProvider.GetRequiredService<IPanelSync>();

        var result = await sync.RunAsync(PanelSyncMode.Full, "failing", CancellationToken.None);

        Assert.False(result.Skipped);
        Assert.Equal(PanelSyncRunModes.Full, result.Mode);
        Assert.Equal("panel unavailable", result.Error);
        Assert.Equal("failed", result.Reason);
        Assert.Equal(0, result.Seen);

        using var readScope = provider.CreateScope();
        var context = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var state = await context.PanelSyncStates.AsNoTracking().SingleAsync();

        Assert.Equal(PanelSyncRunModes.Full, state.LastRunMode);
        Assert.Equal("panel unavailable", state.LastError);
        Assert.Equal(Now, state.LastStartedAt);
        Assert.Equal(Now, state.LastCompletedAt);
        Assert.Null(state.LastSuccessAt);
        Assert.Null(state.LockOwner);
        Assert.Null(state.LockExpiresAt);
    }

    private async Task<ServiceProvider> CreateProviderAsync(PostgresTestDatabase database,
        IPanelGateway gateway)
    {
        var provider = PersistenceTestProvider.Build(database.ConnectionString,
            timeProvider: new FixedTimeProvider(Now),
            configure: services => services.AddSingleton(gateway));

        await provider.GetRequiredService<DatabaseMigrationRunner>().MigrateAsync();
        return provider;
    }

    private sealed record RowSpec(
        string Email,
        long PanelClientId,
        long TelegramId,
        DateTimeOffset UpdatedAt,
        string? Comment = null)
    {
        public DateTimeOffset CreatedAt { get; init; } = PanelCreatedAt;

        public bool Enable { get; init; } = true;

        public PanelTrafficSummary? Traffic { get; init; }

        public PanelClientPageRow ToPageRow()
        {
            return new PanelClientPageRow(
                Email,
                $"sub-{PanelClientId}",
                Enable,
                Expiry.ToUnixTimeMilliseconds(),
                100L * 1024 * 1024 * 1024,
                2,
                3,
                4,
                1,
                0,
                0,
                [1],
                Traffic,
                CreatedAt,
                UpdatedAt);
        }

        public PanelClientDetails ToDetails()
        {
            var client = new PanelClientModel(
                $"uuid-{PanelClientId}",
                Email,
                Enable,
                Expiry.ToUnixTimeMilliseconds(),
                TelegramId,
                100L * 1024 * 1024 * 1024,
                Comment ?? $"comment-{PanelClientId}",
                2,
                3,
                "monthly",
                4,
                1,
                "reality",
                $"sub-{PanelClientId}",
                "xtls-rprx-vision",
                PanelClientId,
                [1]);

            return new PanelClientDetails(client, null, [1], Traffic?.Down ?? 0)
            {
                CreatedAt = CreatedAt,
                UpdatedAt = UpdatedAt
            };
        }
    }

    private sealed class FakePanelGateway : IPanelGateway
    {
        public List<RowSpec> Rows { get; } = [];

        public HashSet<string> MissingDetails { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool RejectUpdatedAtSort { get; set; }

        public Exception? PageException { get; set; }

        public List<PanelClientPageRequest> Requests { get; } = [];

        public List<string> DetailLookups { get; } = [];

        public Task<PanelClientPage> GetClientsPageAsync(PanelClientPageRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);

            if (PageException is not null) throw PageException;

            if (RejectUpdatedAtSort && request.Sort == PanelClientSortFields.UpdatedAt)
                throw new PanelApiException("updatedAt sorting is not supported");

            var sorted = request.Sort == PanelClientSortFields.UpdatedAt
                ? Rows.OrderBy(row => row.UpdatedAt)
                    .ThenBy(row => row.Email, StringComparer.Ordinal)
                    .ToList()
                : Rows.OrderBy(row => row.Email, StringComparer.Ordinal).ToList();

            var items = sorted
                .Skip((request.Page - 1) * request.PageSize)
                .Take(request.PageSize)
                .Select(row => row.ToPageRow())
                .ToArray();

            return Task.FromResult(new PanelClientPage(items, request.Page, request.PageSize,
                sorted.Count, sorted.Count));
        }

        public Task<PanelClientDetails?> GetClientByEmailAsync(string email,
            CancellationToken cancellationToken)
        {
            DetailLookups.Add(email);

            if (MissingDetails.Contains(email)) return Task.FromResult<PanelClientDetails?>(null);

            var row = Rows.SingleOrDefault(candidate =>
                candidate.Email.Equals(email, StringComparison.OrdinalIgnoreCase));

            return Task.FromResult(row?.ToDetails());
        }

        public Task<PanelClientDetails?> GetClientByTelegramIdAsync(long tgId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task AddClientAsync(PanelClientCreate client, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task UpdateClientAsync(PanelClientUpdate request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task UpdateClientAsync(string keyEmail, PanelClientUpdate request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task BulkDisableClientsAsync(IEnumerable<string> emails,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task BulkEnableClientsAsync(IEnumerable<string> emails,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<PanelClientSummary>> GetAllClientsAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<PanelInboundOption>> GetInboundOptionsAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ResetClientTrafficAsync(string email, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
