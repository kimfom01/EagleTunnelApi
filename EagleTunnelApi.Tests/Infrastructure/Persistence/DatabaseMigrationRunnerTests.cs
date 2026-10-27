using System.Reflection;
using EagleTunnelApi.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace EagleTunnelApi.Tests.Infrastructure.Persistence;

[Collection(PostgresCollection.Name)]
public class DatabaseMigrationRunnerTests
{
    private static readonly string[] ExpectedTables =
    [
        "__EFMigrationsHistory",
        "app_users",
        "audit_events",
        "cancellation_state",
        "credit_applications",
        "credit_entries",
        "email_login_codes",
        "email_outbox",
        "panel_client_inbounds",
        "panel_clients",
        "panel_inbounds",
        "panel_sync_conflicts",
        "panel_sync_state",
        "referral_attributions",
        "reminder_deliveries",
        "trial_periods",
        "web_sessions"
    ];

    private readonly PostgresContainerFixture _fixture;

    public DatabaseMigrationRunnerTests(PostgresContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task MigrateAsync_applies_the_initial_migration_and_releases_the_lock()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(_fixture.ConnectionString);
        using var provider = PersistenceTestProvider.Build(database.ConnectionString);
        var runner = provider.GetRequiredService<DatabaseMigrationRunner>();

        await runner.MigrateAsync();
        await runner.MigrateAsync();

        Assert.Equal(ExpectedTables, await GetTablesAsync(database.ConnectionString));
        Assert.Equal(1L, await ExecuteScalarAsync<long>(
            database.ConnectionString,
            "SELECT count(*) FROM \"__EFMigrationsHistory\""));
        Assert.Equal(0L, await ExecuteScalarAsync<long>(
            database.ConnectionString,
            """
            SELECT count(*)
            FROM information_schema.columns
            WHERE table_schema = 'public' AND column_name = 'xmin'
            """));
        Assert.Null(await ExecuteScalarAsync<uint?>(
            database.ConnectionString,
            "SELECT xmin FROM app_users LIMIT 1"));
        Assert.Equal(0L, await GetAdvisoryLockCountAsync(database.ConnectionString));
        Assert.True(await TryAcquireAdvisoryLockAsync(
            database.ConnectionString,
            GetAdvisoryLockKey()));
    }

    [Fact]
    public async Task MigrateAsync_releases_the_lock_when_migration_fails()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(_fixture.ConnectionString);
        await ExecuteNonQueryAsync(
            database.ConnectionString,
            "CREATE TABLE app_users (id uuid PRIMARY KEY)");

        using var provider = PersistenceTestProvider.Build(database.ConnectionString);
        var runner = provider.GetRequiredService<DatabaseMigrationRunner>();

        var exception = await Record.ExceptionAsync(() => runner.MigrateAsync());

        Assert.NotNull(exception);
        Assert.Equal(0L, await GetAdvisoryLockCountAsync(database.ConnectionString));
        Assert.True(await TryAcquireAdvisoryLockAsync(
            database.ConnectionString,
            GetAdvisoryLockKey()));
    }

    [Fact]
    public async Task MigrateAsync_serializes_concurrent_startups()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(_fixture.ConnectionString);
        using var firstProvider = PersistenceTestProvider.Build(database.ConnectionString);
        using var secondProvider = PersistenceTestProvider.Build(database.ConnectionString);

        var firstRunner = firstProvider.GetRequiredService<DatabaseMigrationRunner>();
        var secondRunner = secondProvider.GetRequiredService<DatabaseMigrationRunner>();

        await Task.WhenAll(firstRunner.MigrateAsync(), secondRunner.MigrateAsync())
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1L, await ExecuteScalarAsync<long>(
            database.ConnectionString,
            "SELECT count(*) FROM \"__EFMigrationsHistory\""));
    }

    [Fact]
    public async Task ResetAsync_preserves_the_schema_and_migration_history()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(_fixture.ConnectionString);
        using var provider = PersistenceTestProvider.Build(database.ConnectionString);
        await provider.GetRequiredService<DatabaseMigrationRunner>().MigrateAsync();

        await database.ResetAsync();

        Assert.Equal(ExpectedTables, await GetTablesAsync(database.ConnectionString));
        Assert.Equal(1L, await ExecuteScalarAsync<long>(
            database.ConnectionString,
            "SELECT count(*) FROM \"__EFMigrationsHistory\""));
    }

    private static async Task<string[]> GetTablesAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT tablename
            FROM pg_tables
            WHERE schemaname = 'public'
            ORDER BY tablename
            """;

        var tables = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        return [.. tables];
    }

    private static async Task<long> GetAdvisoryLockCountAsync(string connectionString)
    {
        return await ExecuteScalarAsync<long>(
            connectionString,
            """
            SELECT count(*)
            FROM pg_locks
            WHERE locktype = 'advisory'
              AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
            """);
    }

    private static async Task<bool> TryAcquireAdvisoryLockAsync(string connectionString, long key)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_try_advisory_lock(@key)";
        command.Parameters.AddWithValue("key", key);

        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static long GetAdvisoryLockKey()
    {
        var field = typeof(DatabaseMigrationRunner).GetField(
            "AdvisoryLockKey",
            BindingFlags.NonPublic | BindingFlags.Static);

        return (long)(field?.GetValue(null)
            ?? throw new InvalidOperationException("Advisory lock key was not found."));
    }

    private static async Task<T> ExecuteScalarAsync<T>(string connectionString, string sql)
    {
        var result = await ExecuteScalarAsync(connectionString, sql);
        return (T)result!;
    }

    private static async Task<object?> ExecuteScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        return await command.ExecuteScalarAsync();
    }

    private static async Task ExecuteNonQueryAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await command.ExecuteNonQueryAsync();
    }
}
