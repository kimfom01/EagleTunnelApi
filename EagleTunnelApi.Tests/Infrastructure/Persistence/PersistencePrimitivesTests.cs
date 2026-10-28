using System.Net;
using System.Text.Json;
using EagleTunnelApi.Application.Auditing;
using EagleTunnelApi.Application.Persistence;
using EagleTunnelApi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EagleTunnelApi.Tests.Infrastructure.Persistence;

[Collection(PostgresCollection.Name)]
public class PersistencePrimitivesTests
{
    private readonly PostgresContainerFixture _fixture;

    public PersistencePrimitivesTests(PostgresContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task AuditWriter_appends_and_unit_of_work_persists()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(_fixture.ConnectionString);
        using var provider = PersistenceTestProvider.Build(database.ConnectionString);
        await provider.GetRequiredService<DatabaseMigrationRunner>().MigrateAsync();

        var occurredAt = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

        using (var scope = provider.CreateScope())
        {
            var writer = scope.ServiceProvider.GetRequiredService<IAuditWriter>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            using var metadata = JsonDocument.Parse("""{"source":"test"}""");
            await writer.AppendAsync(new AuditEventRecord
            {
                OccurredAt = occurredAt,
                ActorType = "test",
                EventType = "web.test.event",
                EmailNormalized = "user@example.com",
                TelegramId = 123,
                CorrelationId = "corr-1",
                Ip = IPAddress.Parse("192.0.2.10"),
                UserAgent = "test-agent",
                Metadata = metadata
            });

            Assert.Equal(1, await unitOfWork.SaveChangesAsync());
        }

        using (var readScope = provider.CreateScope())
        {
            var readContext = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var auditEvent = await readContext.AuditEvents.AsNoTracking().SingleAsync();

            Assert.True(auditEvent.Id > 0);
            Assert.Equal(occurredAt, auditEvent.OccurredAt);
            Assert.Equal("test", auditEvent.ActorType);
            Assert.Equal("web.test.event", auditEvent.EventType);
            Assert.Equal("user@example.com", auditEvent.EmailNormalized);
            Assert.Equal(123, auditEvent.TelegramId);
            Assert.Equal("corr-1", auditEvent.CorrelationId);
            Assert.Equal(IPAddress.Parse("192.0.2.10"), auditEvent.Ip);
            Assert.Equal("test-agent", auditEvent.UserAgent);
            Assert.Equal("test", auditEvent.Metadata!.RootElement.GetProperty("source").GetString());
        }
    }

    [Fact]
    public async Task UnitOfWork_commits_changes_on_success()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(_fixture.ConnectionString);
        using var provider = PersistenceTestProvider.Build(database.ConnectionString);
        await provider.GetRequiredService<DatabaseMigrationRunner>().MigrateAsync();

        using var scope = provider.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<IAuditWriter>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var result = await unitOfWork.ExecuteInTransactionAsync(async cancellationToken =>
        {
            await writer.AppendAsync(CreateAuditEvent(), cancellationToken);
            return 42;
        });

        Assert.Equal(42, result);
        Assert.Equal(1, await context.AuditEvents.CountAsync());
    }

    [Fact]
    public async Task UnitOfWork_rolls_back_changes_on_failure()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(_fixture.ConnectionString);
        using var provider = PersistenceTestProvider.Build(database.ConnectionString);
        await provider.GetRequiredService<DatabaseMigrationRunner>().MigrateAsync();

        using var scope = provider.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<IAuditWriter>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            unitOfWork.ExecuteInTransactionAsync<int>(async cancellationToken =>
            {
                await writer.AppendAsync(CreateAuditEvent(), cancellationToken);
                throw new InvalidOperationException("boom");
            }));

        Assert.Equal(0, await context.AuditEvents.CountAsync());
    }

    private static AuditEventRecord CreateAuditEvent()
    {
        return new AuditEventRecord
        {
            OccurredAt = DateTimeOffset.UtcNow,
            ActorType = "test",
            EventType = "web.test.event"
        };
    }
}
